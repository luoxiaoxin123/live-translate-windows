using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiveTranslate.Core.Data;

/// <summary>
/// Stores up to 10 API keys DPAPI-encrypted (current user) at %LocalAppData%\LiveTranslate\apikeys.dat.
/// Sessions stick to the active key and only fail over to the next one when it errors or runs out
/// of quota; the active index is in-memory only.
/// </summary>
public sealed class ApiKeyStore
{
    public const int MaxKeys = 10;

    private readonly string _filePath;
    private readonly object _lock = new();
    private List<string>? _cache;
    private int _activeIndex;

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
                _cache = cleaned;
            }
            catch
            {
                // Persist failed — drop the cache so reads reflect what is actually on disk.
                _cache = null;
                throw;
            }
        }
    }

    /// <summary>The key sessions use until it fails. Null when none are stored.</summary>
    public string? ActiveKey()
    {
        lock (_lock)
        {
            _cache ??= LoadKeys();
            if (_cache.Count == 0) return null;
            return _cache[Math.Clamp(_activeIndex, 0, _cache.Count - 1)];
        }
    }

    /// <summary>
    /// <paramref name="failed"/> errored or ran out of quota: make the key after it active and
    /// return that, or null when there is no other key to fall back to.
    /// </summary>
    public string? FailoverFrom(string failed)
    {
        lock (_lock)
        {
            _cache ??= LoadKeys();
            if (_cache.Count < 2) return null;
            _activeIndex = FailoverIndex(_cache, failed, _activeIndex);
            return _cache[_activeIndex];
        }
    }

    /// <summary>
    /// Index of the key after <paramref name="failed"/>, wrapping. If the keys were edited and
    /// <paramref name="failed"/> is gone, the current active key (a different one) takes over.
    /// </summary>
    internal static int FailoverIndex(IReadOnlyList<string> keys, string failed, int activeIndex)
    {
        var failedIndex = keys.ToList().IndexOf(failed);
        return failedIndex >= 0 ? (failedIndex + 1) % keys.Count : Math.Clamp(activeIndex, 0, keys.Count - 1);
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
