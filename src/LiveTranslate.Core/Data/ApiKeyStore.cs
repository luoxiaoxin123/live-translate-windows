using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiveTranslate.Core.Data;

/// <summary>
/// Stores up to 10 API keys DPAPI-encrypted (current user) at %LocalAppData%\LiveTranslate\apikeys.dat.
/// Sessions rotate through the keys round-robin; the rotation index is in-memory only.
/// </summary>
public sealed class ApiKeyStore
{
    public const int MaxKeys = 10;

    private readonly string _filePath;
    private readonly object _lock = new();
    private List<string>? _cache;
    private int _rotationIndex = -1;

    public ApiKeyStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(UserSettingsRepository.DefaultDirectory(), "apikeys.dat");
    }

    public IReadOnlyList<string> GetKeys()
    {
        lock (_lock)
        {
            _cache ??= LoadKeys();
            return _cache.ToList();
        }
    }

    public void SaveKeys(IEnumerable<string> keys)
    {
        var cleaned = keys
            .Select(k => k.Trim())
            .Where(k => k.Length > 0)
            .Take(MaxKeys)
            .ToList();

        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                var json = JsonSerializer.Serialize(cleaned);
                var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
                var tempPath = _filePath + ".tmp";
                File.WriteAllBytes(tempPath, encrypted);
                File.Move(tempPath, _filePath, overwrite: true);
            }
            finally
            {
                _cache = cleaned;
            }
        }
    }

    /// <summary>Round-robin: each call returns the next stored key. Null when none are stored.</summary>
    public string? NextRotatedKey()
    {
        lock (_lock)
        {
            _cache ??= LoadKeys();
            if (_cache.Count == 0) return null;
            _rotationIndex = (_rotationIndex + 1) % _cache.Count;
            return _cache[_rotationIndex];
        }
    }

    public bool HasKeys() => GetKeys().Count > 0;

    private List<string> LoadKeys()
    {
        try
        {
            if (!File.Exists(_filePath)) return new List<string>();
            var encrypted = File.ReadAllBytes(_filePath);
            var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser));
            var keys = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            return keys
                .Select(k => k.Trim())
                .Where(k => k.Length > 0)
                .Take(MaxKeys)
                .ToList();
        }
        catch
        {
            // Unreadable (corrupt or different user) — treat as no keys rather than crash.
            return new List<string>();
        }
    }
}
