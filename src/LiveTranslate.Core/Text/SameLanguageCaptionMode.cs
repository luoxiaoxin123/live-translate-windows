using LiveTranslate.Core.Data;

namespace LiveTranslate.Core.Text;

/// <summary>
/// Session-scoped switch: when the input is already the target language, the overlay shows a
/// single caption line instead of source + translation, and the echoed translation audio stays
/// silent so it does not double the original soundtrack.
///
/// Enters on the first matching language code (or a high-confidence script fallback). Leaves
/// only after two consecutive mismatches so one mis-detected fragment doesn't flicker the
/// layout or unmute a false echo. Not thread-safe; callers serialize access.
/// </summary>
public sealed class SameLanguageCaptionMode
{
    private int _mismatchStreak;

    public bool Enabled { get; private set; }

    /// <summary>
    /// Translation audio plays only when the user turned it on and the source is not already
    /// the target language (the echo would just repeat the video).
    /// </summary>
    public bool ShouldPlayTranslatedAudio(bool userEnabled) => userEnabled && !Enabled;

    public void Reset()
    {
        Enabled = false;
        _mismatchStreak = 0;
    }

    /// <summary>Returns the new <see cref="Enabled"/> value if it changed, otherwise null.</summary>
    public bool? OnDetectedInputLanguage(string? detected, string target)
    {
        if (string.IsNullOrWhiteSpace(detected)) return null;

        if (LanguageCodes.MatchesTarget(detected, target))
        {
            _mismatchStreak = 0;
            if (Enabled) return null;
            Enabled = true;
            return true;
        }

        _mismatchStreak++;
        if (!Enabled || _mismatchStreak < 2) return null;
        Enabled = false;
        return false;
    }

    /// <summary>
    /// When the API omits <c>languageCode</c>, infer same-language from the input transcript's
    /// script — only while no translation text has arrived yet.
    /// </summary>
    public bool? OnInputTextFallback(string text, string target, bool outputEmpty)
    {
        if (Enabled || !outputEmpty) return null;
        if (!LanguageCodes.TextLooksLikeTarget(text, target)) return null;
        Enabled = true;
        _mismatchStreak = 0;
        return true;
    }
}
