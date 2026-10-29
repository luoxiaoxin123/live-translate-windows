using System.Net.Http.Headers;
using System.Text.Json;

namespace LiveTranslate.Core.Update;

/// <summary>A published GitHub release that is newer than the running build.</summary>
public sealed record AvailableUpdate(string Version, string Url);

/// <summary>
/// Asks GitHub for the latest (non-draft, non-prerelease) release and reports it when its tag
/// is newer than the running version. Every failure — offline, rate limited, malformed tag —
/// counts as "no update" so startup never breaks.
/// </summary>
public static class UpdateChecker
{
    private const string LatestReleaseApi = "https://api.github.com/repos/luoxiaoxin123/live-translate-windows/releases/latest";
    public const string ReleasesPage = "https://github.com/luoxiaoxin123/live-translate-windows/releases/latest";

    public static async Task<AvailableUpdate?> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            // GitHub's API rejects requests without a User-Agent.
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LiveTranslate", "1.0"));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await http.GetAsync(LatestReleaseApi, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag) || !IsNewer(tag, currentVersion)) return null;

            var url = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            return new AvailableUpdate(tag.TrimStart('v', 'V'), string.IsNullOrWhiteSpace(url) ? ReleasesPage : url);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>True when <paramref name="latest"/> has a higher numeric version than <paramref name="current"/> ("v0.2.4" &gt; "0.2.3-debug.x").</summary>
    public static bool IsNewer(string latest, string current)
    {
        var a = NumericParts(latest);
        var b = NumericParts(current);
        if (a is null || b is null) return false;
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var diff = (i < a.Length ? a[i] : 0).CompareTo(i < b.Length ? b[i] : 0);
            if (diff != 0) return diff > 0;
        }
        return false;
    }

    private static int[]? NumericParts(string version)
    {
        var core = new string(version.Trim().TrimStart('v', 'V').TakeWhile(c => char.IsAsciiDigit(c) || c == '.').ToArray());
        var parts = core.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out numbers[i])) return null;
        }
        return numbers;
    }
}
