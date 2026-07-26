namespace LiveTranslate.Core.Text;

/// <summary>
/// Accumulates streaming transcript chunks. The Live API often sends cumulative rewrites:
/// if a new chunk starts with everything we already have, it replaces the buffer; an exact
/// duplicate tail is ignored; anything else is appended (with a space only between Latin
/// word characters). The front of the buffer is trimmed once the cap is exceeded.
/// </summary>
public sealed class TranscriptAccumulator
{
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
                _text = _text[^_maxChars..];
            }
        }
    }

    public void Reset()
    {
        lock (this) _text = "";
    }

    private static bool NeedsSpace(char last, char next) =>
        IsLatinWordChar(last) && IsLatinWordChar(next);

    private static bool IsLatinWordChar(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
}
