using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveTranslate.App.Localization;
using LiveTranslate.Core.Data;
using LiveTranslate.Core.Live;
using Microsoft.UI.Xaml.Controls;

namespace LiveTranslate.App.ViewModels;

public sealed partial class ApiKeyEntry : ObservableObject
{
    private readonly Action<ApiKeyEntry> _remove;

    [ObservableProperty]
    public partial string Value { get; set; }

    [ObservableProperty]
    public partial PasswordRevealMode RevealMode { get; set; }

    public ApiKeyEntry(string initial, Action<ApiKeyEntry> remove)
    {
        Value = initial;
        RevealMode = PasswordRevealMode.Hidden;
        _remove = remove;
    }

    [RelayCommand]
    private void Remove() => _remove(this);
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly UserSettingsRepository _settings;
    private readonly ApiKeyStore _keys;
    private readonly bool _initializing;

    public ObservableCollection<ApiKeyEntry> ApiKeys { get; } = new();

    [ObservableProperty]
    public partial string Endpoint { get; set; }

    [ObservableProperty]
    public partial string ModelId { get; set; }

    [ObservableProperty]
    public partial bool RevealKeys { get; set; }

    [ObservableProperty]
    public partial bool CanAddKey { get; set; }

    [ObservableProperty]
    public partial bool IsTesting { get; set; }

    [ObservableProperty]
    public partial string TestResult { get; set; }

    [ObservableProperty]
    public partial double FontSize { get; set; }

    [ObservableProperty]
    public partial double OpacityPercent { get; set; }

    [ObservableProperty]
    public partial bool Bilingual { get; set; }

    [ObservableProperty]
    public partial string BilingualSummary { get; set; }

    [ObservableProperty]
    public partial bool PlayTranslatedAudio { get; set; }

    [ObservableProperty]
    public partial bool EchoTargetLanguage { get; set; }

    [ObservableProperty]
    public partial double VolumePercent { get; set; }

    [ObservableProperty]
    public partial bool VolumeBoosted { get; set; }

    public string VersionText { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var assembly = typeof(SettingsViewModel).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }
        return assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    }

    public SettingsViewModel(UserSettingsRepository settings, ApiKeyStore keys)
    {
        _settings = settings;
        _keys = keys;

        _initializing = true;
        TestResult = "";
        var current = _settings.Current;
        Endpoint = current.Endpoint;
        ModelId = current.ModelId;
        FontSize = current.FontSize;
        OpacityPercent = Math.Round(current.BackgroundOpacity * 100);
        Bilingual = current.Bilingual;
        BilingualSummary = current.Bilingual ? L.BilingualOn : L.BilingualOff;
        PlayTranslatedAudio = current.PlayTranslatedAudio;
        EchoTargetLanguage = current.EchoTargetLanguage;
        VolumePercent = Math.Round(current.TranslatedVolume * 100);
        VolumeBoosted = VolumePercent > 100;
        _initializing = false;

        foreach (var key in _keys.GetKeys())
        {
            ApiKeys.Add(new ApiKeyEntry(key, RemoveKey));
        }
        if (ApiKeys.Count == 0)
        {
            ApiKeys.Add(new ApiKeyEntry("", RemoveKey));
        }
        UpdateCanAddKey();
    }

    // ---- API section ----

    [RelayCommand]
    private void AddKey()
    {
        if (ApiKeys.Count >= ApiKeyStore.MaxKeys) return;
        var entry = new ApiKeyEntry("", RemoveKey)
        {
            RevealMode = RevealKeys ? PasswordRevealMode.Visible : PasswordRevealMode.Hidden,
        };
        ApiKeys.Add(entry);
        UpdateCanAddKey();
    }

    private void RemoveKey(ApiKeyEntry entry)
    {
        if (ApiKeys.Count <= 1)
        {
            entry.Value = "";
            return;
        }
        ApiKeys.Remove(entry);
        UpdateCanAddKey();
    }

    private void UpdateCanAddKey() => CanAddKey = ApiKeys.Count < ApiKeyStore.MaxKeys;

    partial void OnRevealKeysChanged(bool value)
    {
        var mode = value ? PasswordRevealMode.Visible : PasswordRevealMode.Hidden;
        foreach (var entry in ApiKeys)
        {
            entry.RevealMode = mode;
        }
    }

    [RelayCommand]
    private void SaveKeys()
    {
        TestResult = PersistApiSettings(out var error) ? L.Saved : L.SaveFailed(error);
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task SaveAndTestAsync(CancellationToken cancellationToken)
    {
        if (!PersistApiSettings(out var error))
        {
            TestResult = L.SaveFailed(error);
            return;
        }

        IsTesting = true;
        var lines = new List<string>();
        try
        {
            var keys = ApiKeys.Select(k => k.Value.Trim()).Where(k => k.Length > 0).ToList();
            if (keys.Count == 0)
            {
                TestResult = L.NoApiKey;
                return;
            }

            var settings = _settings.Current;
            var ok = 0;
            for (var i = 0; i < keys.Count; i++)
            {
                TestResult = string.Join("\n", lines.Append(L.Testing(i + 1, keys.Count)));

                if (keys[i].Length < 16)
                {
                    lines.Add($"Key {i + 1}: ❌ {L.KeyTooShort}");
                    continue;
                }

                using var client = new LiveTranslateClient();
                var (success, message) = await client.TestConnectionAsync(new SessionConfig(
                    settings.Endpoint, keys[i], settings.ModelId, settings.TargetLanguageCode,
                    settings.EchoTargetLanguage),
                    cancellationToken: cancellationToken);
                if (success)
                {
                    ok++;
                    lines.Add($"Key {i + 1}: {L.KeyOk}");
                }
                else
                {
                    lines.Add($"Key {i + 1}: ❌ {message}");
                }
            }

            lines.Add(L.TestSummary(ok, keys.Count));
            TestResult = string.Join("\n", lines);
        }
        catch (OperationCanceledException)
        {
            lines.Add(L.TestCancelled);
            TestResult = string.Join("\n", lines);
        }
        finally
        {
            IsTesting = false;
        }
    }

    private bool PersistApiSettings(out string error)
    {
        var endpointValue = Endpoint.Trim();
        var modelValue = ModelId.Trim();
        var persisted = _settings.Update(s => s with
        {
            Endpoint = endpointValue.Length > 0 ? endpointValue : UserSettings.DefaultEndpoint,
            ModelId = modelValue.Length > 0 ? modelValue : UserSettings.DefaultModelId,
        });
        _settings.Flush();
        // Blank fields fall back to defaults — reflect what was actually persisted in the UI.
        Endpoint = persisted.Endpoint;
        ModelId = persisted.ModelId;

        try
        {
            _keys.SaveKeys(ApiKeys.Select(k => k.Value));
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // ---- appearance ----

    partial void OnFontSizeChanged(double value)
    {
        if (_initializing) return;
        _settings.Update(s => s with { FontSize = Math.Clamp(value, 12, 32) });
    }

    partial void OnOpacityPercentChanged(double value)
    {
        if (_initializing) return;
        _settings.Update(s => s with { BackgroundOpacity = Math.Clamp(value / 100.0, 0.10, 0.95) });
    }

    partial void OnBilingualChanged(bool value)
    {
        BilingualSummary = value ? L.BilingualOn : L.BilingualOff;
        if (_initializing) return;
        _settings.Update(s => s with { Bilingual = value });
    }

    [RelayCommand]
    private void ResetAppearance()
    {
        var reset = _settings.Update(s => s.ResetSubtitleAppearance());
        FontSize = reset.FontSize;
        OpacityPercent = Math.Round(reset.BackgroundOpacity * 100);
        Bilingual = reset.Bilingual;
    }

    // ---- translated voice ----

    partial void OnPlayTranslatedAudioChanged(bool value)
    {
        if (_initializing) return;
        _settings.Update(s => s with { PlayTranslatedAudio = value });
    }

    partial void OnEchoTargetLanguageChanged(bool value)
    {
        if (_initializing) return;
        _settings.Update(s => s with { EchoTargetLanguage = value });
    }

    partial void OnVolumePercentChanged(double value)
    {
        VolumeBoosted = value > 100;
        if (_initializing) return;
        _settings.Update(s => s with { TranslatedVolume = Math.Clamp(value / 100.0, 0, 2.0) });
    }
}
