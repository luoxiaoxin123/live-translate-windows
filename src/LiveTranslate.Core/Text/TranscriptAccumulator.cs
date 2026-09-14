using System.Text;

namespace LiveTranslate.Core.Text;

/// <summary>
/// Accumulates streaming transcript chunks. The Live API often sends cumulative rewrites:
/// if a new chunk starts with everything we already have, it replaces the buffer; an exact
/// duplicate tail is ignored; anything else is appended (with a space only between Latin
/// word characters). Once the cap is exceeded the front is trimmed, preferably at a sentence
/// boundary so the overlay's wrapped lines don't shift mid-sentence.
/// </summary>
public sealed class TranscriptAccumulator
{
    private const int SentenceAlignWindow = 64;
    private static readonly char[] BreakableSpaces = [' ', '\u00A0', '\u3000'];

    private readonly int _maxChars;
    private string _text = "";

    public TranscriptAccumulator(int maxChars) => _maxChars = maxChars;

    public string Text
    {
        get
        {
            lock (this) return _text;
        }
    }

    public void Append(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;

        lock (this)
        {
            if (_text.Length == 0)
            {
                _text = chunk;
            }
            else if (chunk.Length >= _text.Length && chunk.StartsWith(_text, StringComparison.Ordinal))
            {
                _text = chunk; // cumulative rewrite from the server
            }
            else if (_text.EndsWith(chunk, StringComparison.Ordinal))
            {
                return; // duplicate tail
            }
            else
            {
                _text = NeedsSpace(_text[^1], chunk[0]) ? _text + " " + chunk : _text + chunk;
            }

            if (_text.Length > _maxChars)
            {
                var start = _text.Length - _maxChars;
                if (char.IsLowSurrogate(_text[start])) start++; // never split a surrogate pair
                start = AlignCutToSentenceBoundary(_text, start);
                _text = _text[start..];
            }
        }
    }

    public void Reset()
    {
        lock (this) _text = "";
    }

    /// <summary>
    /// Moves a trim point forward past the rest of a partial sentence so trimming drops whole
    /// sentences — a mid-sentence cut changes the wrapped line count mid-stream and makes the
    /// overlay jump. Only a small window after <paramref name="cut"/> is searched; without a
    /// terminator nearby the original cut is kept, so the cap still holds.
    /// </summary>
    internal static int AlignCutToSentenceBoundary(string text, int cut)
    {
        if (cut <= 0 || cut >= text.Length) return cut;
        var limit = Math.Min(cut + SentenceAlignWindow, text.Length);
        for (var i = cut; i < limit; i++)
        {
            if (!IsSentenceEnd(text[i])) continue;
            var end = i + 1;
            while (end < text.Length && IsCloser(text[end])) end++;
            if (end < text.Length && text[end] == ' ') end++; // kept text starts cleanly
            return end;
        }
        return cut;
    }

    /// <summary>
    /// Drops spaces that landed between two Han/kana characters (from chunk joins or from the
    /// model itself), e.g. "做 成" → "做成". Latin text, mixed "用 hello" and Korean (which does
    /// separate words with spaces) are left alone.
    /// </summary>
    public static string CollapseCjkInteriorSpaces(string text)
    {
        if (text.Length < 3 || text.IndexOfAny(BreakableSpaces) < 0) return text;

        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (!IsBreakableSpace(text[i]))
            {
                sb.Append(text[i++]);
                continue;
            }

            var end = i;
            while (end < text.Length && IsBreakableSpace(text[end])) end++;
            var betweenCjk = i > 0 && end < text.Length && IsSpacelessCjk(text[i - 1]) && IsSpacelessCjk(text[end]);
            if (!betweenCjk) sb.Append(text, i, end - i);
            i = end;
        }
        return sb.Length == text.Length ? text : sb.ToString();
    }

    private static bool NeedsSpace(char last, char next) =>
        IsLatinWordChar(last) && IsLatinWordChar(next);

    private static bool IsLatinWordChar(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    private static bool IsSentenceEnd(char c) =>
        c is '。' or '．' or '！' or '？' or '.' or '!' or '?' or '…' or '\n';

    private static bool IsCloser(char c) =>
        c is '”' or '’' or '"' or '\'' or '」' or '』' or '）' or ')' or '】' or '》' or '>';

    private static bool IsBreakableSpace(char c) => c is ' ' or '\u00A0' or '\u3000';

    // Han, kana, CJK punctuation and fullwidth forms: scripts written without spaces between words.
    private static bool IsSpacelessCjk(char c) => c switch
    {
        '\u3000' => false,                    // ideographic space is a space, not a letter
        >= '\u3130' and <= '\u318F' => false, // Hangul compatibility jamo
        >= '\u2E80' and <= '\u9FFF' => true,
        >= '\uF900' and <= '\uFAFF' => true,
        >= '\uFF00' and <= '\uFFEF' => true,
        _ => false,
    };
}
