using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiveTranslate.Core.Data;

/// <summary>
/// JSON persistence for <see cref="UserSettings"/> at %LocalAppData%\LiveTranslate\settings.json.
/// Thread-safe; writes are atomic (temp file + move). Corrupt or missing files fall back to defaults.
/// </summary>
public sealed class UserSettingsRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _filePath;
    private readonly object _lock = new();
    private UserSettings _current;

    public event Action<UserSettings>? SettingsChanged;

    public UserSettingsRepository(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(DefaultDirectory(), "settings.json");
        _current = Load();
    }

    public static string DefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveTranslate");

    public UserSettings Current
    {
        get
        {
            lock (_lock) return _current;
        }
    }

    public UserSettings Update(Func<UserSettings, UserSettings> mutate)
    {
        UserSettings updated;
        lock (_lock)
        {
            updated = mutate(_current);
            if (updated == _current) return updated;
            _current = updated;
            Save(updated);
        }
        SettingsChanged?.Invoke(updated);
        return updated;
    }

    private UserSettings Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var loaded = JsonSerializer.Deserialize<UserSettings>(json, JsonOptions);
                if (loaded != null) return loaded;
            }
        }
        catch
        {
            // Corrupt settings file — start over with defaults.
        }
        return new UserSettings();
    }

    private void Save(UserSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(directory);
            var tempPath = _filePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch
        {
            // Persisting settings must never crash the app; the in-memory value stays authoritative.
        }
    }
}
