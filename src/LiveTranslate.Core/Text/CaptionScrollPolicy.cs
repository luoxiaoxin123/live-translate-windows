namespace LiveTranslate.Core.Text;

public enum CaptionScrollAction
{
    /// <summary>Line count unchanged — keep the reader's eyes steady.</summary>
    HoldSteady,

    /// <summary>Scroll so the newest line is visible.</summary>
    ShowLastLine,

    /// <summary>Text was really cleared or replaced — jump back to the top.</summary>
    ResetToTop,
}

/// <summary>
/// Auto-scroll decision for a caption pane after its text changed. Head-trimming of the display
/// buffer (long sessions) briefly lowers the wrapped line count; that must keep the pane anchored
/// on the newest line instead of snapping to the top, which made captions jump up and down.
/// </summary>
public static class CaptionScrollPolicy
{
    public static CaptionScrollAction Decide(int lineCount, int previousLineCount, int textLength)
    {
        if (lineCount > previousLineCount) return CaptionScrollAction.ShowLastLine;
        if (lineCount == previousLineCount) return CaptionScrollAction.HoldSteady;
        // Fewer lines: only a near-empty pane is a real reset; otherwise the head was trimmed.
        return lineCount <= 1 && textLength < 32 ? CaptionScrollAction.ResetToTop : CaptionScrollAction.ShowLastLine;
    }
}
