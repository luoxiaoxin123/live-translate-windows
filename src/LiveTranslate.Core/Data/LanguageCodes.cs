namespace LiveTranslate.Core.Data;

/// <summary>
/// BCP-47 helpers for Live Translate. The API takes no source language: it auto-detects the
/// input and reports <c>languageCode</c> on transcriptions. These helpers decide when the
/// detected input language is already the configured target.
/// </summary>
public static class LanguageCodes
{
    public readonly record struct Parsed(string Language, string? Script, string? Region);

    private static readonly Dictionary<string, string> LanguageAliases = new(StringComparer.Ordinal)
    {
        ["nb"] = "no",
        ["nn"] = "no",
        ["iw"] = "he",
        ["in"] = "id",
        ["jw"] = "jv",
        ["tl"] = "fil",
        ["cmn"] = "zh",
        ["yue"] = "zh",
        ["wuu"] = "zh",
    };

    /// <summary>
    /// True when <paramref name="detected"/> is the same language as <paramref name="target"/>,
    /// including aliases (<c>nb</c>/<c>no</c>, <c>cmn</c>/<c>zh</c>) and Chinese script/region
    /// (<c>zh-CN</c> ≈ <c>zh-Hans</c>). <c>zh</c> without a script matches both Simplified and
    /// Traditional; <c>zh-Hans</c> never matches <c>zh-Hant</c>. Region-sensitive targets like
    /// <c>pt-BR</c>/<c>pt-PT</c> mismatch when both sides carry different regions; a side
    /// without a region stays compatible with either.
    /// </summary>
    public static bool MatchesTarget(string? detected, string target)
    {
        if (Parse(detected) is not { } d || Parse(target) is not { } t) return false;
        if (d.Language != t.Language) return false;
        var ds = EffectiveScript(d);
        var ts = EffectiveScript(t);
        if (ds != null && ts != null && ds != ts) return false;
        if (d.Region != null && t.Region != null && d.Region != t.Region) return false;
        return true;
    }

    /// <summary>
    /// Fallback when the API omits <c>languageCode</c>. Only high-confidence scripts are used
    /// (Han / kana / Hangul / Latin-English) so similar Latin languages are never guessed.
    /// </summary>
    public static bool TextLooksLikeTarget(string text, string target)
    {
        if (Parse(target) is not { } t) return false;
        var sample = text.Trim();
        if (sample.Length < 8) return false;

        int han = 0, hangul = 0, kana = 0, latin = 0, letters = 0;
        foreach (var ch in sample)
        {
            if (char.IsLetter(ch)) letters++;
            if (ch is >= '\u4E00' and <= '\u9FFF' or >= '\u3400' and <= '\u4DBF') han++;
            else if (ch is >= '\u3040' and <= '\u30FF' or >= '\u31F0' and <= '\u31FF') kana++;
            else if (ch is >= '\uAC00' and <= '\uD7AF') hangul++;
            else if (ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z') latin++;
        }
        if (letters == 0) return false;

        return t.Language switch
        {
            "zh" => han >= 6 && han * 2 >= letters && kana == 0 && hangul == 0,
            "ja" => kana >= 2 && hangul == 0,
            "ko" => hangul >= 6,
            "en" => latin >= 8 && han == 0 && hangul == 0 && kana == 0,
            _ => false,
        };
    }

    public static Parsed? Parse(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var parts = code.Trim().Replace('_', '-').Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return null;

        var language = parts[0].ToLowerInvariant();
        if (LanguageAliases.TryGetValue(language, out var alias)) language = alias;

        string? script = null;
        string? region = null;
        foreach (var part in parts.Skip(1))
        {
            if (part.Length == 4 && part.All(char.IsLetter))
                script = char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant();
            else if (part.Length == 2 && part.All(char.IsLetter))
                region = part.ToUpperInvariant();
            else if (part.Length == 3 && part.All(char.IsDigit))
                region = part;
        }
        return new Parsed(language, script, region);
    }

    private static string? EffectiveScript(Parsed p)
    {
        if (p.Script != null) return p.Script.ToLowerInvariant();
        if (p.Language != "zh") return null;
        return p.Region switch
        {
            "TW" or "HK" or "MO" => "hant",
            "CN" or "SG" => "hans",
            _ => null,
        };
    }
}
