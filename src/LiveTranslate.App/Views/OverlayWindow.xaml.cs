using System.Runtime.InteropServices;
using LiveTranslate.App.Localization;
using LiveTranslate.Core.Data;
using LiveTranslate.Core.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using WinUIEx;

namespace LiveTranslate.App.Views;

/// <summary>
/// The floating subtitle window: borderless, always on top, never activated (focus stays in
/// the video app), per-pixel transparent — a semi-transparent rounded backdrop with fully
/// opaque text. Top grabber drags; the always-visible corner arc resizes (font size unchanged);
/// each pane auto-scrolls only when its line count grows. When the input is already the target
/// language the session collapses bilingual mode to a single pane.
///
/// Like the Android overlay, the top control strip (grabber + close button) stays out of the way:
/// it shows for a few seconds after the window appears and whenever the mouse is over it, then
/// tucks away shortly after the mouse leaves — the caption area takes the freed space.
/// </summary>
public sealed partial class OverlayWindow : WindowEx
{
    /// <summary>Raised when the user clicks the strip's close button; the session should end.</summary>
    public event Action? CloseRequested;

    private const int MinWidthDip = 200;
    private const int MinHeightDip = 80;

    private const long ControlsShownOnStartMs = 5000; // same as Android's first reveal
    private const long ControlsHideDelayMs = 1000;    // after the mouse leaves the window

    private DispatcherQueueTimer? _controlsTimer;
    private bool _controlsShown = true; // the XAML starts expanded
    private long _hideControlsAt;

    private readonly UserSettingsRepository _settings;
    private bool _allowClose;

    private bool _dragging;
    private bool _resizing;
    private PointInt32 _startPointer;
    private PointInt32 _startPosition;
    private SizeInt32 _startSize;

    private int _sourceLines;
    private int _translationLines;
    private bool _sourceTextDirty;
    private bool _translationTextDirty;

    private bool _bilingual;
    private bool _sameLanguage;

    private IntPtr _hwnd;
    private RectInt32 _workArea;
    private double _scale;

    public OverlayWindow(UserSettingsRepository settings)
    {
        _settings = settings;
        InitializeComponent();
        AutomationProperties.SetName(CloseButton, L.CloseSubtitles);

        SystemBackdrop = new TransparentTintBackdrop();

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        AppWindow.IsShownInSwitchers = false;

        ApplyNoActivateStyle();
        RemoveWindowFrame();
        ApplyInitialGeometry();
        ApplyAppearance(_settings.Current);

        AppWindow.Closing += (_, e) =>
        {
            // No chrome exists, but block programmatic/system closes unless the session ends it.
            if (!_allowClose) e.Cancel = true;
        };
    }

    public void ShowNoActivate()
    {
        AppWindow.Show(activateWindow: false);
        RevealControls(ControlsShownOnStartMs);
    }

    public void CloseOverlay()
    {
        _controlsTimer?.Stop();
        _allowClose = true;
        Close();
    }

    public void SetTexts(string source, string translation)
    {
        if (SourceText.Text != source)
        {
            _sourceTextDirty = true;
            SourceText.Text = source;
        }
        if (TranslationText.Text != translation)
        {
            _translationTextDirty = true;
            TranslationText.Text = translation;
        }
    }

    public void ApplyAppearance(UserSettings settings)
    {
        var fontSize = Math.Clamp(settings.FontSize, 12, 32);
        TranslationText.FontSize = fontSize;
        TranslationText.LineHeight = Math.Ceiling(fontSize * 1.45);
        TranslationText.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        SourceText.FontSize = fontSize * 0.9;
        SourceText.LineHeight = Math.Ceiling(fontSize * 0.9 * 1.45);
        SourceText.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;

        Bg.Opacity = Math.Clamp(settings.BackgroundOpacity, 0, 0.95);

        _bilingual = settings.Bilingual;
        ApplyLayoutMode();
    }

    /// <summary>
    /// Input already in the target language: show one pane even if bilingual is on.
    /// Session-scoped; the bilingual setting itself is untouched.
    /// </summary>
    public void SetSameLanguageMode(bool enabled)
    {
        if (_sameLanguage == enabled) return;
        _sameLanguage = enabled;
        ApplyLayoutMode();
    }

    private void ApplyLayoutMode()
    {
        var bilingual = _bilingual && !_sameLanguage;
        SourceScroll.Visibility = bilingual ? Visibility.Visible : Visibility.Collapsed;
        Divider.Visibility = bilingual ? Visibility.Visible : Visibility.Collapsed;
        SourceRowDef.Height = bilingual ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        // Layout changed — re-baseline the line counters so autoscroll doesn't jump.
        _sourceLines = 0;
        _translationLines = 0;
    }

    // ---- placement ----

    private void ApplyInitialGeometry()
    {
        var s = _settings.Current;
        var scale = GetScale();
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;

        int width, height, x, y;
        if (s.OverlayWidth > 0 && s.OverlayHeight > 0)
        {
            width = s.OverlayWidth;
            height = s.OverlayHeight;
            x = s.OverlayX;
            y = s.OverlayY;
        }
        else
        {
            width = Math.Min((int)(workArea.Width * 0.6), (int)(900 * scale));
            height = (int)(150 * scale);
            x = workArea.X + (workArea.Width - width) / 2;
            y = workArea.Y + (int)(workArea.Height * 0.75) - height / 2;
        }

        var rect = ClampToWorkArea(new RectInt32(x, y, width, height));
        AppWindow.MoveAndResize(rect);
    }

    private RectInt32 ClampToWorkArea(RectInt32 rect)
    {
        var workArea = DisplayArea.GetFromRect(rect, DisplayAreaFallback.Nearest).WorkArea;
        var scale = GetScale();

        var minWidth = (int)(MinWidthDip * scale);
        var minHeight = (int)(MinHeightDip * scale);
        var width = Math.Clamp(rect.Width, minWidth, workArea.Width);
        var height = Math.Clamp(rect.Height, minHeight, workArea.Height);
        var x = Math.Clamp(rect.X, workArea.X, workArea.X + workArea.Width - width);
        var y = Math.Clamp(rect.Y, workArea.Y, workArea.Y + workArea.Height - height);
        return new RectInt32(x, y, width, height);
    }

    private double GetScale() => GetDpiForWindow(this.GetWindowHandle()) / 96.0;

    private void CachePlacementContext()
    {
        _hwnd = this.GetWindowHandle();
        _workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        _scale = GetScale();
    }

    private RectInt32 CurrentRect()
    {
        if (_hwnd == IntPtr.Zero) _hwnd = this.GetWindowHandle();
        if (GetWindowRect(_hwnd, out var native))
        {
            return new RectInt32(native.Left, native.Top, native.Right - native.Left, native.Bottom - native.Top);
        }
        return new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
    }

    private void SaveGeometry()
    {
        var rect = CurrentRect();
        _settings.Update(s => s with
        {
            OverlayX = rect.X,
            OverlayY = rect.Y,
            OverlayWidth = rect.Width,
            OverlayHeight = rect.Height,
        });
    }

    // ---- auto-hiding controls ----

    private void Root_PointerActivity(object sender, PointerRoutedEventArgs e) => RevealControls(ControlsHideDelayMs);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    /// <summary>Shows the control strip for at least <paramref name="visibleMs"/>.</summary>
    private void RevealControls(long visibleMs)
    {
        _hideControlsAt = Math.Max(_hideControlsAt, Environment.TickCount64 + visibleMs);
        if (!_controlsShown)
        {
            _controlsShown = true;
            Grabber.IsHitTestVisible = true;
            ShowControlsStoryboard.Begin();
        }

        if (_controlsTimer == null)
        {
            _controlsTimer = DispatcherQueue.CreateTimer();
            _controlsTimer.Interval = TimeSpan.FromMilliseconds(200);
            _controlsTimer.Tick += (_, _) => OnControlsTimerTick();
        }
        if (!_controlsTimer.IsRunning) _controlsTimer.Start();
    }

    // Polls the cursor instead of relying on PointerExited, which a no-activate window
    // doesn't reliably get when the mouse leaves quickly or a drag ends outside it.
    private void OnControlsTimerTick()
    {
        var now = Environment.TickCount64;
        if (_dragging || _resizing || IsCursorOverWindow())
        {
            _hideControlsAt = Math.Max(_hideControlsAt, now + ControlsHideDelayMs);
            return;
        }
        if (now < _hideControlsAt) return;

        _controlsTimer?.Stop();
        _controlsShown = false;
        Grabber.IsHitTestVisible = false; // also covers the close button inside the strip
        HideControlsStoryboard.Begin();
    }

    private bool IsCursorOverWindow()
    {
        if (!GetCursorPos(out var cursor)) return false;
        var rect = CurrentRect();
        return cursor.X >= rect.X && cursor.X < rect.X + rect.Width
            && cursor.Y >= rect.Y && cursor.Y < rect.Y + rect.Height;
    }

    // ---- drag to move ----

    private void Grabber_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!GetCursorPos(out var cursor)) return;
        CachePlacementContext();
        _dragging = Grabber.CapturePointer(e.Pointer);
        _startPointer = new PointInt32(cursor.X, cursor.Y);
        var rect = CurrentRect();
        _startPosition = new PointInt32(rect.X, rect.Y);
    }

    private void Grabber_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || !GetCursorPos(out var cursor)) return;
        SetWindowPos(
            _hwnd,
            IntPtr.Zero,
            _startPosition.X + cursor.X - _startPointer.X,
            _startPosition.Y + cursor.Y - _startPointer.Y,
            0,
            0,
            SwpNosize | SwpNozorder | SwpNoactivate);
    }

    private void Grabber_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Grabber.ReleasePointerCapture(e.Pointer);
        var rect = ClampToWorkArea(CurrentRect());
        AppWindow.MoveAndResize(rect);
        SaveGeometry();
    }

    // ---- drag to resize ----

    private void ResizeHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!GetCursorPos(out var cursor)) return;
        CachePlacementContext();
        _resizing = ResizeHandle.CapturePointer(e.Pointer);
        _startPointer = new PointInt32(cursor.X, cursor.Y);
        var rect = CurrentRect();
        _startPosition = new PointInt32(rect.X, rect.Y);
        _startSize = new SizeInt32(rect.Width, rect.Height);
    }

    private void ResizeHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizing || !GetCursorPos(out var cursor)) return;

        var maxWidth = _workArea.X + _workArea.Width - _startPosition.X;
        var maxHeight = _workArea.Y + _workArea.Height - _startPosition.Y;
        var minWidth = (int)(MinWidthDip * _scale);
        var minHeight = (int)(MinHeightDip * _scale);

        var width = Math.Clamp(_startSize.Width + cursor.X - _startPointer.X, minWidth, Math.Max(minWidth, maxWidth));
        var height = Math.Clamp(_startSize.Height + cursor.Y - _startPointer.Y, minHeight, Math.Max(minHeight, maxHeight));
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, width, height, SwpNomove | SwpNozorder | SwpNoactivate);
    }

    private void ResizeHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizing) return;
        _resizing = false;
        ResizeHandle.ReleasePointerCapture(e.Pointer);
        SaveGeometry();
    }

    // ---- mouse wheel: one notch = one subtitle line ----

    private void SourceScroll_PointerWheelChanged(object sender, PointerRoutedEventArgs e) =>
        ScrollByWheel(SourceScroll, SourceText.LineHeight, e);

    private void TranslationScroll_PointerWheelChanged(object sender, PointerRoutedEventArgs e) =>
        ScrollByWheel(TranslationScroll, TranslationText.LineHeight, e);

    private static void ScrollByWheel(Microsoft.UI.Xaml.Controls.ScrollViewer viewer, double lineHeight, PointerRoutedEventArgs e)
    {
        if (viewer.ScrollableHeight <= 0) return;

        var delta = e.GetCurrentPoint(viewer).Properties.MouseWheelDelta;
        if (delta == 0) return;

        var step = lineHeight > 0 ? lineHeight : 24;
        // WHEEL_DELTA is 120 per notch. Smooth wheels report fractions of that.
        var lines = delta / 120.0;
        var offset = Math.Clamp(viewer.VerticalOffset - lines * step, 0, viewer.ScrollableHeight);
        viewer.ChangeView(null, offset, null, disableAnimation: true);
        e.Handled = true;
    }

    // ---- autoscroll: only when the wrapped line count grows ----

    // The control strip sliding in (or the window getting shorter) shrinks a pane's viewport;
    // a pane that was showing its newest line keeps it in view instead of hiding it below.
    private void CaptionScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var viewer = (Microsoft.UI.Xaml.Controls.ScrollViewer)sender;
        if (e.NewSize.Height >= e.PreviousSize.Height) return;
        var previousScrollable = viewer.ExtentHeight - e.PreviousSize.Height;
        if (viewer.VerticalOffset >= previousScrollable - 2)
            viewer.ChangeView(null, viewer.ExtentHeight, null, disableAnimation: true);
    }

    private void SourceText_SizeChanged(object sender, SizeChangedEventArgs e) =>
        AutoScroll(SourceScroll, SourceText, ref _sourceLines, ref _sourceTextDirty);

    private void TranslationText_SizeChanged(object sender, SizeChangedEventArgs e) =>
        AutoScroll(TranslationScroll, TranslationText, ref _translationLines, ref _translationTextDirty);

    private static void AutoScroll(Microsoft.UI.Xaml.Controls.ScrollViewer viewer, Microsoft.UI.Xaml.Controls.TextBlock text, ref int lastLines, ref bool textDirty)
    {
        if (text.LineHeight <= 0) return;
        var lines = (int)Math.Round(text.ActualHeight / text.LineHeight);

        // SizeChanged also fires when the window is resized and the text re-wraps;
        // only a text update may scroll — a pure resize just re-baselines the counter.
        if (textDirty)
        {
            switch (CaptionScrollPolicy.Decide(lines, lastLines, text.Text.Length))
            {
                case CaptionScrollAction.ShowLastLine:
                    // A shrink here is the display buffer trimming its head: stay on the newest line.
                    viewer.ChangeView(null, viewer.ExtentHeight, null, disableAnimation: lines < lastLines);
                    break;
                case CaptionScrollAction.ResetToTop:
                    viewer.ChangeView(null, 0, null, disableAnimation: true);
                    break;
            }
            textDirty = false;
        }
        lastLines = lines;
    }

    // ---- win32 ----

    private void ApplyNoActivateStyle()
    {
        const int GWL_EXSTYLE = -20;
        const long WS_EX_NOACTIVATE = 0x08000000;
        const long WS_EX_TOOLWINDOW = 0x00000080;

        var hwnd = this.GetWindowHandle();
        var exStyle = GetWindowLongPtrW(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, new IntPtr(exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
    }

    // SetBorderAndTitleBar(false, false) still leaves WS_DLGFRAME: a 3px non-client frame that the
    // transparent backdrop never covers, so it renders as a white outline around the captions.
    // Drop the frame styles so the client area fills the window, and clear the 1px border
    // Windows 11 DWM draws around top-level windows (that attribute is a no-op on Windows 10).
    private void RemoveWindowFrame()
    {
        const int GWL_STYLE = -16;
        const int GWL_EXSTYLE = -20;
        const long WS_BORDER = 0x00800000;
        const long WS_DLGFRAME = 0x00400000;
        const long WS_THICKFRAME = 0x00040000;
        const long WS_EX_DLGMODALFRAME = 0x00000001;
        const long WS_EX_WINDOWEDGE = 0x00000100;
        const int DWMWA_BORDER_COLOR = 34;

        var hwnd = this.GetWindowHandle();
        var style = GetWindowLongPtrW(hwnd, GWL_STYLE).ToInt64();
        SetWindowLongPtrW(hwnd, GWL_STYLE, new IntPtr(style & ~(WS_BORDER | WS_DLGFRAME | WS_THICKFRAME)));
        var exStyle = GetWindowLongPtrW(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, new IntPtr(exStyle & ~(WS_EX_DLGMODALFRAME | WS_EX_WINDOWEDGE)));
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNomove | SwpNosize | SwpNozorder | SwpNoactivate | SwpFramechanged);

        var colorNone = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorNone, sizeof(int));
    }

    private const uint SwpNosize = 0x0001;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNozorder = 0x0004;
    private const uint SwpNoactivate = 0x0010;
    private const uint SwpFramechanged = 0x0020;

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
