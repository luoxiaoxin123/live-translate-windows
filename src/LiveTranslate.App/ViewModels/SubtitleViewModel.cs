using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveTranslate.App.Localization;
using LiveTranslate.App.Services;
using LiveTranslate.Core.Audio;
using LiveTranslate.Core.Data;
using Microsoft.UI.Xaml.Controls;

namespace LiveTranslate.App.ViewModels;

public sealed record LanguageChoice(string Code, string Name);

public sealed partial class SubtitleViewModel : ObservableObject
{
    private readonly SubtitleSessionService _session;
    private readonly UserSettingsRepository _settings;
    private bool _initializing = true;

    public IReadOnlyList<LanguageChoice> SourceLanguages { get; }
    public IReadOnlyList<LanguageChoice> TargetLanguages { get; }
    public IReadOnlyList<string> AudioSources { get; } = new[]
    {
        L.AudioSourceMedia,
        L.AudioSourceMic,
        L.AudioSourceBoth,
    };

    [ObservableProperty]
    public partial int SourceLanguageIndex { get; set; }

    [ObservableProperty]
    public partial int TargetLanguageIndex { get; set; }

    [ObservableProperty]
    public partial int AudioSourceIndex { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; }

    [ObservableProperty]
    public partial InfoBarSeverity StatusSeverity { get; set; }

    [ObservableProperty]
    public partial string StartStopText { get; set; }

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    public partial bool CanEditSession { get; set; }

    [ObservableProperty]
    public partial string InputPreview { get; set; }

    [ObservableProperty]
    public partial string OutputPreview { get; set; }

    [ObservableProperty]
    public partial bool HasPreview { get; set; }

    [ObservableProperty]
    public partial bool CanExport { get; set; }

    [ObservableProperty]
    public partial string ExportMessage { get; set; }

    public SubtitleViewModel(SubtitleSessionService session, UserSettingsRepository settings)
    {
        _session = session;
        _settings = settings;

        SourceLanguages = Languages.SourceOptions
            .Select(o => new LanguageChoice(o.Code, o.DisplayName(L.IsChinese)))
            .ToList();
        TargetLanguages = Languages.TargetOptions
            .Select(o => new LanguageChoice(o.Code, o.DisplayName(L.IsChinese)))
            .ToList();

        var current = _settings.Current;
        SourceLanguageIndex = IndexOf(SourceLanguages, current.SourceLanguageCode);
        TargetLanguageIndex = IndexOf(TargetLanguages, current.TargetLanguageCode);
        AudioSourceIndex = current.AudioSourceMode switch
        {
            AudioSourceMode.Mic => 1,
            AudioSourceMode.MediaAndMic => 2,
            _ => 0,
        };
        _initializing = false;

        ExportMessage = "";
        _session.StateChanged += RefreshFromSession;
        RefreshFromSession();
    }

    private static int IndexOf(IReadOnlyList<LanguageChoice> options, string code)
    {
        for (var i = 0; i < options.Count; i++)
        {
            if (options[i].Code == code) return i;
        }
        return 0;
    }

    partial void OnSourceLanguageIndexChanged(int value)
    {
        if (_initializing || value < 0 || value >= SourceLanguages.Count) return;
        var code = SourceLanguages[value].Code;
        _settings.Update(s => s with { SourceLanguageCode = code });
    }

    partial void OnTargetLanguageIndexChanged(int value)
    {
        if (_initializing || value < 0 || value >= TargetLanguages.Count) return;
        var code = TargetLanguages[value].Code;
        _settings.Update(s => s with { TargetLanguageCode = code });
    }

    partial void OnAudioSourceIndexChanged(int value)
    {
        if (_initializing) return;
        var mode = value switch
        {
            1 => AudioSourceMode.Mic,
            2 => AudioSourceMode.MediaAndMic,
            _ => AudioSourceMode.Media,
        };
        _settings.Update(s => s with { AudioSourceMode = mode });
    }

    [RelayCommand]
    private async Task ToggleSessionAsync()
    {
        ExportMessage = "";
        if (_session.IsActive)
        {
            await _session.StopAsync();
        }
        else
        {
            await _session.StartAsync();
        }
    }

    [RelayCommand]
    private void Export()
    {
        var (_, message) = _session.ExportLastSession();
        ExportMessage = message;
    }

    private void RefreshFromSession()
    {
        IsActive = _session.IsActive;
        CanEditSession = !_session.IsActive;
        StartStopText = _session.IsActive ? L.StopSubtitles : L.StartSubtitles;

        (StatusText, StatusSeverity) = _session.Status switch
        {
            SessionStatus.Starting => (L.StatusStarting, InfoBarSeverity.Warning),
            SessionStatus.Running => (L.StatusRunning, InfoBarSeverity.Success),
            SessionStatus.Stopped => (L.StatusStopped, InfoBarSeverity.Informational),
            SessionStatus.Error => (L.StatusError, InfoBarSeverity.Error),
            _ => (L.StatusIdle, InfoBarSeverity.Informational),
        };
        StatusMessage = _session.StatusMessage;

        InputPreview = _session.InputPreview;
        OutputPreview = _session.OutputPreview;
        HasPreview = InputPreview.Length > 0 || OutputPreview.Length > 0;
        CanExport = _session.CanExport && !_session.IsActive;
    }
}
