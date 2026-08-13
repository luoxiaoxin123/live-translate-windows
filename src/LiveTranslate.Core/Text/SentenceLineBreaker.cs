using System.Text;

namespace LiveTranslate.Core.Text;

/// <summary>
/// Display-layer sentence wrapping. The Live API streams a single paragraph; this inserts
/// newlines after sentence terminators once the current line is long enough, so short
/// fragments ("OK." / "好的。") stay packed together.
///
/// Must not be applied to the accumulator buffer — the server sends cumulative rewrites
/// of the raw text, and injected newlines would break prefix matching.
/// </summary>
public static class SentenceLineBreaker
{
    public const int DefaultMinCharsPerLine = 16;

    public static string Format(string text, int minCharsPerLine = DefaultMinCharsPerLine)
    {
        if (string.IsNullOrEmpty(text) || minCharsPerLine <= 0) return text ?? "";

        var sb = new StringBuilder(text.Length + 8);
        var lineStart = 0;
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\n')
            {
                sb.Append('\n');
                i++;
                lineStart = sb.Length;
                continue;
            }

            if (!TryConsumeTerminator(text, i, out var consumed))
            {
                sb.Append(text[i]);
                i++;
                continue;
            }

            sb.Append(text, i, consumed);
            i += consumed;

            while (i < text.Length && IsCloser(text[i]))
            {
                sb.Append(text[i]);
                i++;
            }

            var lineLength = sb.Length - lineStart;
            if (lineLength < minCharsPerLine || !HasNonWhitespace(text, i)) continue;

            while (i < text.Length && text[i] is ' ' or '\t') i++;
            if (i < text.Length && text[i] != '\n')
            {
                sb.Append('\n');
                lineStart = sb.Length;
            }
        }

        return sb.ToString();
    }

    private static bool TryConsumeTerminator(string text, int index, out int consumed)
    {
        if (index + 2 < text.Length
            && text[index] == '.' && text[index + 1] == '.' && text[index + 2] == '.')
        {
            consumed = 3;
            return true;
        }

        switch (text[index])
        {
            case '。':
            case '！':
            case '？':
            case '…':
            case '!':
            case '?':
                consumed = 1;
                return true;
            case '.':
                if (IsDecimalPoint(text, index))
                {
                    consumed = 0;
                    return false;
                }
                consumed = 1;
                return true;
            default:
                consumed = 0;
                return false;
        }
    }

    private static bool IsDecimalPoint(string text, int index) =>
        index > 0 && char.IsDigit(text[index - 1])
        && index + 1 < text.Length && char.IsDigit(text[index + 1]);

    // Closing quotes/brackets that belong to the sentence we just finished.
    // Opening marks ( “ 「 『 ( ) stay with the next sentence.
    private static bool IsCloser(char c) => c is
        '"' or '\'' or '”' or '’' or '」' or '』' or '）' or ')' or '》' or ']' or '】';

    private static bool HasNonWhitespace(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i])) return true;
        }
        return false;
    }
}
