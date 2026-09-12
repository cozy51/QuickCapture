using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using QuickCapture.Interop;
using QuickCapture.Models;
using QuickCapture.Controls;
using QuickCapture.Services;

namespace QuickCapture.Windows;

public sealed class CaptureWindow : Window
{
    internal const double FrameThickness = CaptureFrame.Thickness;
    /// Smallest window, in physical pixels like the rest of the frame.
    internal const double MinPixelWidth = 160, MinPixelHeight = 100 + CaptureFrame.HeaderHeight;
    private HwndSource? source;
    private readonly ImageDocument document;
    private readonly CaptureFrame frame;
    public DateTimeOffset CapturedAt => document.CapturedAt;
    internal int CaptureNumber => frame.CaptureNumber;
    internal int CaptureCount => frame.CaptureCount;
    private readonly ImageViewport viewport;
    private readonly AppSettings settings;
    private readonly Action<bool> setNextAutoClose;
    private readonly Action<bool> setExportHeader;
    /// Colour and width choices are kept for the captures that follow.
    private readonly Action<AppSettings> saveDrawing;
    private readonly ImageExportService exporter = new();
    private readonly ClipboardService clipboard = new();
    private readonly CaptureToolbar toolbar;
    private readonly TextBox textEditor;
    /// The editor and its あ/A switch travel together over the picture.
    private readonly StackPanel textEditorRow;
    private readonly Border imeSwitch;
    private readonly TextBlock imeSwitchLabel;
    private Point textOrigin;
    private bool textEditorHeldTopmost;
    /// Set while the editor is retyping a label that is already on the picture.
    private bool retyping;
    /// Japanese input, on for the first label and then as the writer last left it.
    private bool imeOn = true;
    private readonly Brush imeOnBrush = new SolidColorBrush(Color.FromRgb(47, 123, 246));
    private readonly Brush imeOffBrush = new SolidColorBrush(Color.FromRgb(96, 104, 116));
    private readonly Border status;
    private readonly TextBlock statusText;
    private readonly DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    /// Ticks while a capture is counting down, so the number on it keeps up.
    private readonly DispatcherTimer autoCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private const double AutoCloseSeconds = 3;
    private DateTime autoCloseDeadline;
    private readonly TextBlock countdownText;
    private readonly Border countdown;
    public bool AutoCloseEnabled { get; private set; }
    private bool copying, closed;
    private bool manualCopyRequested;
    private int zoomResizeRevision;
    /// Physical size of the window, and the size WPF is about to force on it for a
    /// monitor scaling change.
    private int pixelWidth, pixelHeight;
    private Int32Rect dpiSuggestion;
    /// The user dragged the frame to another size; the window stops hugging then.
    private bool userSized;
    private int sizingWidth;
    /// Where this capture came from, and whether the window still belongs there.
    /// Moving, resizing or zooming hands the position over to the user.
    private readonly Int32Rect capturedRegion;
    private bool keepAtCapture = true;

    public CaptureWindow(ImageDocument document, Int32Rect region, AppSettings? settings = null, Action<bool>? setNextAutoClose = null, Action<bool>? setExportHeader = null, Action<AppSettings>? saveDrawing = null)
    {
        this.document = document; this.settings = settings ?? new(); capturedRegion = region;
        this.setNextAutoClose = setNextAutoClose ?? (enabled => this.settings.AutoCloseCaptures = enabled);
        this.setExportHeader = setExportHeader ?? (enabled => SetExportHeader(enabled));
        this.saveDrawing = saveDrawing ?? (_ => { });
        Title = $"QuickCapture · {document.Width} × {document.Height}";
        Icon = AppIcon.Image;
        Topmost = this.settings.AlwaysOnTop; ApplyMinimumSize(1);
        Width = Math.Max(MinWidth, region.Width + FrameThickness * 2); Height = Math.Max(MinHeight, region.Height + FrameThickness * 2 + CaptureFrame.HeaderHeight);
        MaxWidth = 32767; MaxHeight = 32767;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize;
        Background = new SolidColorBrush(Color.FromRgb(24, 27, 32));
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });
        viewport = new ImageViewport(document);
        viewport.Highlighter.Color = DrawingPalette.Parse(this.settings.HighlighterColor);
        viewport.Highlighter.Width = this.settings.HighlighterWidth;
        viewport.Highlighter.Opacity = this.settings.HighlighterOpacity;
        viewport.Pen.Color = DrawingPalette.Parse(this.settings.PenColor);
        viewport.Pen.Width = this.settings.PenWidth;
        viewport.TextColor = DrawingPalette.Parse(this.settings.TextColor);
        viewport.TextSize = this.settings.TextSize;
        viewport.TextRequested += StartTextEdit;
        viewport.TextEditRequested += EditText;
        var grid = new Grid { ClipToBounds = true };
        frame = new CaptureFrame { Child = grid, CapturedAt = document.CapturedAt, RecordHeader = this.settings.ExportHeaderEnabled };
        Content = frame;
        frame.CloseRequested += Close;
        frame.MouseLeftButtonDown += (_, e) =>
        {
            var point = e.GetPosition(frame);
            if (point.Y < frame.BandHeight && !frame.IsOverCloseButton(point)) { e.Handled = true; DragMove(); }
        };
        grid.Children.Add(viewport);
        toolbar = new CaptureToolbar(DragMove, () => _ = CopyAsync(), viewport.ToggleHighlighter, viewport.TogglePen, viewport.ToggleText,
            Undo, viewport.ActualSize, viewport.Fit, Save, Close, PickColor) { Visibility = Visibility.Collapsed };
        toolbar.Update(viewport.Tool, false, viewport.ToolColor);
        grid.Children.Add(toolbar);
        // Labels are typed in a real text box so the IME works; it sits over the
        // picture at the spot that was clicked and leaves a TextAnnotation behind.
        textEditor = new TextBox
        {
            Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            MinWidth = 60, MaxWidth = 640, BorderThickness = new Thickness(2), Padding = new Thickness(2, 0, 2, 0), AcceptsReturn = true,
            Background = new SolidColorBrush(Color.FromArgb(242, 255, 255, 255)),
            FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.NoWrap
        };
        // Japanese input needs the IME on the box and the candidate window in front
        // of the picture, so the editor turns the always-on-top state off while it is
        // open, and asks for the IME to be switched on as soon as it takes focus.
        InputMethod.SetIsInputMethodEnabled(textEditor, true);
        InputMethod.SetPreferredImeConversionMode(textEditor, ImeConversionModeValues.Native | ImeConversionModeValues.FullShape);
        textEditor.PreviewKeyDown += TextEditorKey;
        // A switch that takes a mouse click, because the mode key does not always
        // reach the IME: it says which mode is on and turns it over when pressed.
        imeSwitchLabel = new TextBlock { Text = "あ", Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold };
        imeSwitch = new Border
        {
            CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, Child = imeSwitchLabel,
            ToolTip = "日本語入力のON/OFF · 半角/全角キー / Ctrl+Space"
        };
        imeSwitch.MouseLeftButtonDown += (_, e) => { e.Handled = true; ToggleIme(); textEditor.Focus(); };
        textEditorRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
        };
        textEditorRow.Children.Add(textEditor); textEditorRow.Children.Add(imeSwitch);
        // Focus leaving for another control finishes the label; focus leaving the
        // application (the IME candidate window) must not close the editor.
        textEditor.LostKeyboardFocus += (_, e) => { if (e.NewFocus != null) CommitText(); };
        grid.Children.Add(textEditorRow);
        statusText = new TextBlock { Foreground = Brushes.White, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        status = new Border { Background = new SolidColorBrush(Color.FromArgb(220, 29, 33, 41)), CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Child = statusText, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        grid.Children.Add(status);
        countdownText = new TextBlock { Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 96, TextAlignment = TextAlignment.Center };
        countdown = new Border { Background = new SolidColorBrush(Color.FromArgb(170, 20, 22, 26)), CornerRadius = new CornerRadius(20), Padding = new Thickness(26, 0, 26, 10), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = countdownText, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        grid.Children.Add(countdown);
        statusTimer.Tick += StatusExpired;
        autoCloseTimer.Tick += AutoCloseExpired;
        SetAutoClose(this.settings.AutoCloseCaptures);
        viewport.ViewChanged += ViewChanged;
        viewport.ZoomSizeChanged += ResizeForZoom;
        SizeChanged += (_, _) => HugImage();
        ContextMenu = BuildContextMenu();
        PreviewMouseMove += (_, e) =>
        {
            var point = e.GetPosition(grid);
            toolbar.Visibility = !viewport.IsInteracting && (point.Y <= 38 || toolbar.IsMouseOver) ? Visibility.Visible : Visibility.Collapsed;
        };
        MouseLeave += (_, _) => toolbar.Visibility = Visibility.Collapsed;
        SourceInitialized += (_, _) =>
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(WindowMessage);
            PlaceAtCapture(region);
        };
        PreviewKeyDown += HandleKey;
        PreviewKeyUp += (_, _) => viewport.UpdateCursor();
        Loaded += (_, _) =>
        {
            CaptureWindowRegistry.Register(this);
            if (AutoCloseEnabled) StartAutoClose();
            // Once everything has been laid out, put the picture on the very pixels
            // it came from, measured rather than assumed.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => AlignToCapture());
        };
        IsVisibleChanged += (_, _) => CaptureWindowRegistry.Refresh();
        StateChanged += (_, _) => CaptureWindowRegistry.Refresh();
        Deactivated += (_, _) => { viewport.CancelInteraction(); toolbar.Visibility = Visibility.Collapsed; frame.CancelPointer(); };
        Closed += (_, _) =>
        {
            closed = true; statusTimer.Stop(); statusTimer.Tick -= StatusExpired;
            autoCloseTimer.Stop(); autoCloseTimer.Tick -= AutoCloseExpired;
            CaptureWindowRegistry.Unregister(this);
            frame.CloseRequested -= Close;
            source?.RemoveHook(WindowMessage); source = null;
            viewport.ViewChanged -= ViewChanged; viewport.Dispose();
            ContextMenu = null; Content = null; document.Dispose();
        };
    }
    internal void SetAutoClose(bool enabled)
    {
        if (closed) return;
        autoCloseTimer.Stop();
        AutoCloseEnabled = enabled;
        frame.AutoClose = enabled;
        frame.InvalidateVisual();
        if (enabled && IsLoaded) StartAutoClose(); else countdown.Visibility = Visibility.Collapsed;
    }
    /// The three seconds start now, counted down by the big number on the image.
    private void StartAutoClose()
    {
        autoCloseDeadline = DateTime.UtcNow.AddSeconds(AutoCloseSeconds);
        ShowCountdown(AutoCloseSeconds);
        autoCloseTimer.Start();
    }
    private void ShowCountdown(double remaining)
    {
        countdownText.Text = Math.Max(1, Math.Ceiling(remaining)).ToString("0", CultureInfo.InvariantCulture);
        // Large enough to read at a glance, never wider than the picture itself.
        countdownText.FontSize = Math.Max(28, Math.Min(120, Math.Min(viewport.ActualWidth, viewport.ActualHeight) / 2));
        countdown.Visibility = Visibility.Visible;
    }
    /// The seconds left on this capture while it counts down, for tests.
    internal string? Countdown => countdown.Visibility == Visibility.Visible ? countdownText.Text : null;
    internal void SetExportBorder(bool enabled) => settings.ExportBorderEnabled = enabled;
    internal void SetExportHeader(bool enabled)
    {
        settings.ExportHeaderEnabled = enabled;
        if (closed) return;
        frame.RecordHeader = enabled; frame.InvalidateVisual();
    }
    /// Right-click switching applies to every open image, is kept for later
    /// captures, and refreshes the clipboard so the copy matches what is shown.
    private void SetExportHeaderMode(bool enabled)
    {
        try { setExportHeader(enabled); }
        catch (Exception ex) { SetExportHeader(enabled); ShowStatus("設定を保存できません: " + ex.Message); return; }
        _ = CopyAsync(false, enabled ? "カウンターと日時情報を含めてコピーし直しました" : "画像だけでコピーし直しました");
    }
    /// The header of this window as copy and save should record it, or null while
    /// the export is the image alone.
    private CaptureHeaderInfo? ExportHeader => settings.ExportHeaderEnabled ? frame.Header : null;
    /// Switching the mode takes effect on this image at once and stays on for the
    /// captures that follow. Images already open keep the mode they were given.
    private void SetNextCapturesAutoClose(bool enabled)
    {
        try { setNextAutoClose(enabled); }
        catch (Exception ex) { SetAutoClose(enabled); ShowStatus("設定を保存できません: " + ex.Message); return; }
        SetAutoClose(enabled);
        ShowStatus(enabled ? "この画像から3秒で閉じます" : "この画像から画面に残します");
    }
    private void AutoCloseExpired(object? sender, EventArgs e)
    {
        double remaining = (autoCloseDeadline - DateTime.UtcNow).TotalSeconds;
        if (remaining > 0) { ShowCountdown(remaining); return; }
        autoCloseTimer.Stop();
        countdown.Visibility = Visibility.Collapsed;
        if (AutoCloseEnabled && !closed) Close();
    }
    internal void UpdateSequence(int number, int count)
    {
        if (frame.CaptureNumber == number && frame.CaptureCount == count) return;
        frame.CaptureNumber = number; frame.CaptureCount = count;
        frame.InvalidateVisual();
        Title = $"QuickCapture · {number:00} / {count:00} · {CapturedAt.ToLocalTime():HH:mm:ss}";
    }
    /// WPF keeps the minimum in layout units; hold it at the same pixel size so a
    /// small capture is framed the same way on every monitor scaling.
    private void ApplyMinimumSize(double scale)
    {
        double pixel = 1 / Math.Max(0.25, scale);
        MinWidth = MinPixelWidth * pixel; MinHeight = MinPixelHeight * pixel;
    }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplyMinimumSize(newDpi.DpiScaleX);
    }
    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0024) // WM_GETMINMAXINFO: Windows defaults also cap oversized frames.
        {
            var info = Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);
            double dpi = Math.Max(1, NativeMethods.GetDpiForWindow(hwnd) / 96d);
            info.MinTrackSize = new NativeMethods.POINT { X = (int)Math.Ceiling(MinWidth * dpi), Y = (int)Math.Ceiling(MinHeight * dpi) };
            info.MaxTrackSize = new NativeMethods.POINT { X = 32767, Y = 32767 };
            Marshal.StructureToPtr(info, lParam, false);
            handled = true;
        }
        else if (message == 0x0005 && wParam.ToInt64() != 1) RememberPixelSize(hwnd); // WM_SIZE, not minimized
        else if (message == 0x02E0) UndoDpiResize(hwnd, Marshal.PtrToStructure<NativeMethods.RECT>(lParam).Pixels); // WM_DPICHANGED
        // WM_ENTERSIZEMOVE / WM_EXITSIZEMOVE: only a size the user dragged counts
        // as their own, a move does not.
        else if (message == 0x0231) { keepAtCapture = false; sizingWidth = NativeMethods.GetWindowRect(hwnd, out var before) ? before.Pixels.Width : 0; }
        else if (message == 0x0232 && sizingWidth > 0 && NativeMethods.GetWindowRect(hwnd, out var after) && after.Pixels.Width != sizingWidth) userSized = true;
        return IntPtr.Zero;
    }
    /// The pixel size the window is meant to keep. Every resize counts except the
    /// one WPF applies for a monitor scaling change.
    private void RememberPixelSize(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return;
        var pixels = rect.Pixels;
        if (pixels.Width == dpiSuggestion.Width && pixels.Height == dpiSuggestion.Height) return;
        pixelWidth = pixels.Width; pixelHeight = pixels.Height;
    }
    /// WPF answers a monitor scaling change by resizing the window to the rectangle
    /// Windows suggests, which keeps the layout size and so grows the window by the
    /// scaling. The frame and the image are physical pixels, so the window has to
    /// keep its pixel size instead: undo that one resize after WPF has applied it.
    private void UndoDpiResize(IntPtr hwnd, Int32Rect suggested)
    {
        dpiSuggestion = suggested;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            dpiSuggestion = default;
            if (closed || pixelWidth <= 0 || pixelHeight <= 0) return;
            if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return;
            var pixels = rect.Pixels;
            // Only WPF's rescale is undone; a size set after the change stays.
            if (pixels.Width != suggested.Width || pixels.Height != suggested.Height) return;
            if (pixels.Width == pixelWidth && pixels.Height == pixelHeight) return;
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, pixelWidth, pixelHeight, 0x0002 | 0x0004 | 0x0010);
            // The viewport repaints from its own SizeChanged; refreshing here would
            // also pop the zoom status over a window the user has not touched.
            UpdateLayout();
            // WPF also moved the window to the suggested rectangle; put the picture
            // back on the pixels it came from.
            AlignToCapture();
        });
    }
    /// The pixel size that shows the whole picture inside the frame.
    private (int Width, int Height) PixelSizeForImage(double dpi)
    {
        double imageWidth = document.Width * viewport.Zoom.Zoom, imageHeight = document.Height * viewport.Zoom.Zoom;
        int width = (int)Math.Min(32767, Math.Max(Math.Ceiling(MinWidth * dpi), Math.Ceiling(imageWidth + 2 * FrameThickness)));
        int height = (int)Math.Min(32767, Math.Max(Math.Ceiling(MinHeight * dpi), Math.Ceiling(imageHeight + 2 * FrameThickness + CaptureFrame.HeaderHeight)));
        return (width, height);
    }
    /// WPF owns Width and Height in layout units and re-applies them on its own
    /// passes, which on a monitor at 125% turns a 400px capture into a 500px
    /// window. Keep its numbers on the pixel size the window was just given.
    private void SyncLayoutSize(int width, int height)
    {
        pixelWidth = width; pixelHeight = height;
        // WPF's own scale, which is what it converts these numbers back with.
        double scale = Math.Max(0.25, VisualTreeHelper.GetDpi(this).DpiScaleX);
        double layoutWidth = width / scale, layoutHeight = height / scale;
        if (Math.Abs(Width - layoutWidth) > 0.01) Width = layoutWidth;
        if (Math.Abs(Height - layoutHeight) > 0.01) Height = layoutHeight;
    }
    /// Whatever leaves the window larger than its picture — WPF re-applying a
    /// layout size taken on another scaling, or a monitor change — shows up as
    /// black bands around the image. Trim that space away; the window is never
    /// grown here, so zooming past the screen edges and a smaller size the user
    /// chose are both left alone.
    private void HugImage()
    {
        if (closed || userSized || viewport.Zoom.IsFit || viewport.ActualWidth <= 0) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect)) return;
        double dpi = Math.Max(1, NativeMethods.GetDpiForWindow(hwnd) / 96d);
        var wanted = PixelSizeForImage(dpi);
        var pixels = rect.Pixels;
        if (pixels.Width <= wanted.Width && pixels.Height <= wanted.Height) return;
        int width = Math.Min(pixels.Width, wanted.Width), height = Math.Min(pixels.Height, wanted.Height);
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, width, height, 0x0002 | 0x0004 | 0x0010);
        SyncLayoutSize(width, height);
    }
    /// Where the first pixel of the picture actually landed decides the position:
    /// the band, the border and anything Windows puts around the window are all
    /// accounted for by measuring instead of adding them up. The window still has
    /// to fit the work area, so a capture at the very edge of the screen keeps as
    /// much of the correction as there is room for.
    private void AlignToCapture()
    {
        if (closed || !keepAtCapture || !viewport.IsLoaded || viewport.ActualWidth <= 0 || viewport.Zoom.Zoom != 1) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect)) return;
        var shown = viewport.PointToScreen(viewport.Zoom.ToViewport(new Point(0, 0)));
        var pixels = rect.Pixels;
        int dx = capturedRegion.X - (int)Math.Round(shown.X), dy = capturedRegion.Y - (int)Math.Round(shown.Y);
        if (dx == 0 && dy == 0) return;
        var work = NativeMethods.WorkArea(new Point(capturedRegion.X, capturedRegion.Y));
        int x = Math.Clamp(pixels.X + dx, work.X, Math.Max(work.X, work.X + work.Width - pixels.Width));
        int y = Math.Clamp(pixels.Y + dy, work.Y, Math.Max(work.Y, work.Y + work.Height - pixels.Height));
        if (x == pixels.X && y == pixels.Y) return;
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);
    }
    private void PlaceAtCapture(Int32Rect region)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var work = NativeMethods.WorkArea(new Point(region.X, region.Y));
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, region.X, region.Y, 0, 0, 0x0001 | 0x0004 | 0x0010);
        double dpi = Math.Max(1, NativeMethods.GetDpiForWindow(hwnd) / 96d);
        ApplyMinimumSize(dpi);
        // The frame is physical pixels, and so is the minimum window size, so the
        // capture is framed identically whatever scaling the monitor uses.
        int width = Math.Min(work.Width, Math.Max((int)Math.Ceiling(MinWidth * dpi), Math.Min(region.Width + (int)Math.Ceiling(FrameThickness * 2), work.Width - 32)));
        int height = Math.Min(work.Height, Math.Max((int)Math.Ceiling(MinHeight * dpi), Math.Min(region.Height + (int)Math.Ceiling(FrameThickness * 2 + CaptureFrame.HeaderHeight), work.Height - 32)));
        // The picture starts inside the band and the border, so the window sits that
        // much up and to the left: what it shows then covers the very pixels it was
        // taken from, instead of sitting a border's width down and to the right.
        int x = region.X - (int)Math.Round(FrameThickness);
        int y = region.Y - (int)Math.Round(CaptureFrame.HeaderHeight + FrameThickness);
        NativeMethods.SetWindowPos(hwnd, Topmost ? new IntPtr(-1) : new IntPtr(-2), Math.Clamp(x, work.X, work.X + work.Width - width), Math.Clamp(y, work.Y, work.Y + work.Height - height), width, height, 0x0010);
        // Width and Height still hold the layout size WPF created the window with,
        // and WPF applies them again after this; without the sync the window grows
        // back by the monitor scaling and leaves black bands around the picture.
        SyncLayoutSize(width, height);
    }
    private void ResizeForZoom(Point imageAnchor, Point screenAnchor)
    {
        keepAtCapture = false;
        int revision = ++zoomResizeRevision;
        double zoom = viewport.Zoom.Zoom;
        ApplyZoomBounds(imageAnchor, screenAnchor);
        // A frame growing across monitors can trigger WPF's deferred DPI resize.
        // Reapply physical pixel bounds after that transition, once per wheel update.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (closed || revision != zoomResizeRevision || viewport.Zoom.IsFit || viewport.Zoom.Zoom != zoom) return;
            ApplyZoomBounds(imageAnchor, screenAnchor);
            viewport.Refresh();
        });
    }
    private void ApplyZoomBounds(Point imageAnchor, Point screenAnchor)
    {
        if (closed) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        double dpi = viewport.Zoom.DpiScale;
        double border = FrameThickness;
        double header = CaptureFrame.HeaderHeight;
        double imageWidth = document.Width * viewport.Zoom.Zoom;
        double imageHeight = document.Height * viewport.Zoom.Zoom;
        var (width, height) = PixelSizeForImage(dpi);
        double padX = Math.Max(0, (width - 2 * border - imageWidth) / 2);
        double padY = Math.Max(0, (height - 2 * border - header - imageHeight) / 2);
        int x = (int)Math.Round(screenAnchor.X - imageAnchor.X * viewport.Zoom.Zoom - border - padX);
        int y = (int)Math.Round(screenAnchor.Y - imageAnchor.Y * viewport.Zoom.Zoom - border - header - padY);
        // Grow through the screen edges. Clamping the frame while restoring the
        // pointer anchor shifts the image down inside it and creates a black strip.
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, 0x0004 | 0x0010);
        SyncLayoutSize(width, height);
        UpdateLayout();
        // SizeChanged/DPI may adjust the center. Restore the original screen anchor
        // only after the native position and WPF layout have both settled.
        var anchor = viewport.PointFromScreen(screenAnchor);
        viewport.Zoom.Pan(anchor - viewport.Zoom.ToViewport(imageAnchor));
    }
    private void HandleKey(object sender, KeyEventArgs e)
    {
        // While a label is being typed the keys belong to the text box.
        if (textEditor.IsKeyboardFocusWithin) return;
        viewport.UpdateCursor();
        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.Escape) viewport.CancelMode();
        else if (e.Key == Key.Space) viewport.UpdateCursor();
        else if (modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.C: _ = CopyAsync(); break;
                case Key.S: Save(); break;
                case Key.Z: Undo(); break;
                case Key.Y: viewport.CancelInteraction(); document.Redo(); break;
                case Key.Delete: Clear(); break;
                case Key.D0: case Key.NumPad0: viewport.CancelInteraction(); viewport.ActualSize(); break;
                default: return;
            }
        }
        else if (modifiers == ModifierKeys.None)
        {
            switch (e.Key)
            {
                case Key.Delete: Close(); break;
                case Key.H: if (!e.IsRepeat) viewport.ToggleHighlighter(); break;
                case Key.P: if (!e.IsRepeat) viewport.TogglePen(); break;
                case Key.X: if (!e.IsRepeat) viewport.ToggleText(); break;
                case Key.T: if (!e.IsRepeat) SetNextCapturesAutoClose(!AutoCloseEnabled); break;
                case Key.F: viewport.CancelInteraction(); viewport.Fit(); break;
                case Key.D1: case Key.NumPad1: viewport.CancelInteraction(); viewport.ActualSize(); break;
                case Key.OemOpenBrackets: ChangeWidth(-1); break;
                case Key.OemCloseBrackets: ChangeWidth(1); break;
                default: return;
            }
        }
        else return;
        e.Handled = true;
    }
    /// A colour from the toolbar or the menu applies to the tool in hand and is
    /// kept for the captures that follow.
    private void PickColor(Color color)
    {
        viewport.SetColor(color);
        if (textEditorRow.Visibility == Visibility.Visible) textEditor.Foreground = new SolidColorBrush(viewport.TextColor);
        SaveDrawing();
    }
    private void ChangeWidth(int direction) { viewport.ChangeWidth(direction); SaveDrawing(); }
    private void SaveDrawing()
    {
        settings.HighlighterColor = AppSettings.ToHex(viewport.Highlighter.Color);
        settings.HighlighterWidth = viewport.Highlighter.Width;
        settings.PenColor = AppSettings.ToHex(viewport.Pen.Color);
        settings.PenWidth = viewport.Pen.Width;
        settings.TextColor = AppSettings.ToHex(viewport.TextColor);
        settings.TextSize = viewport.TextSize;
        try { saveDrawing(settings); }
        catch (Exception ex) { ShowStatus("設定を保存できません: " + ex.Message); }
    }
    /// The label tool was clicked on the picture: open the editor right there.
    private void StartTextEdit(Point origin)
    {
        if (closed) return;
        CommitText();
        textOrigin = origin;
        var at = viewport.Zoom.ToViewport(origin);
        textEditorRow.Margin = new Thickness(Math.Max(0, at.X - 4), Math.Max(0, at.Y - 4), 0, 0);
        textEditor.FontSize = Math.Max(8, viewport.TextSize * viewport.Zoom.ViewScale);
        var ink = new SolidColorBrush(viewport.TextColor); ink.Freeze();
        textEditor.Foreground = ink; textEditor.BorderBrush = ink; textEditor.CaretBrush = ink;
        textEditor.Text = string.Empty; retyping = false;
        textEditorRow.Visibility = Visibility.Visible;
        if (Topmost) { textEditorHeldTopmost = true; Topmost = false; }
        if (!IsActive) Activate();
        // A box that has just become visible is not arranged yet, and focus put on
        // it before that does not stick — which is what keeps the IME away.
        textEditorRow.UpdateLayout();
        textEditor.Focus(); Keyboard.Focus(textEditor);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (closed || textEditorRow.Visibility != Visibility.Visible) return;
            if (!textEditor.IsKeyboardFocusWithin) { textEditor.Focus(); Keyboard.Focus(textEditor); }
            ApplyIme(imeOn);
            textEditor.CaretIndex = textEditor.Text.Length;
        });
        ShowStatus($"文字を入力（日本語入力{(imeOn ? "ON" : "OFF")} · 右の「あ/A」ボタン・半角/全角キー・Ctrl+Spaceで切替） · Enterで確定 · Escで取り消し");
    }
    /// WPF asks for the IME through the focused element, but a window whose IME
    /// context was dropped along the way ignores that and the mode key alike, so
    /// the editor puts the default context back and sets the state itself.
    private void ApplyIme(bool on)
    {
        imeOn = on;
        imeSwitchLabel.Text = on ? "あ" : "A";
        imeSwitch.Background = on ? imeOnBrush : imeOffBrush;
        try
        {
            InputMethod.SetPreferredImeState(textEditor, on ? InputMethodState.On : InputMethodState.Off);
            InputMethod.Current.ImeState = on ? InputMethodState.On : InputMethodState.Off;
            if (!NativeMethods.SetIme(new WindowInteropHelper(this).Handle, on) && on)
                ShowStatus("日本語入力を開けませんでした · Ctrl+Vでの貼り付けをお試しください");
        }
        catch (Exception ex) { ShowStatus("日本語入力を切り替えられません: " + ex.Message); }
    }
    /// Turn Japanese input over to the other mode. Whether the IME took the key
    /// itself or ignored it, the state asked for is the one that ends up set, so
    /// pressing again always switches back.
    private void ToggleIme()
    {
        bool wanted = !imeOn;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (closed) return;
            ApplyIme(wanted);
            ShowStatus(wanted ? "日本語入力 ON（あ）" : "日本語入力 OFF · 英数（A）");
        });
    }
    /// A label was double clicked: open the editor on its own text, colour and
    /// size, and put what comes out back in its place.
    private void EditText(TextAnnotation label)
    {
        StartTextEdit(label.Origin);
        retyping = true;
        textEditor.FontSize = Math.Max(8, label.FontSize * viewport.Zoom.ViewScale);
        var ink = new SolidColorBrush(label.Color); ink.Freeze();
        textEditor.Foreground = ink; textEditor.BorderBrush = ink; textEditor.CaretBrush = ink;
        textEditor.Text = label.Text; textEditor.SelectAll();
        ShowStatus("文字を書き直す · Enterで確定 · Escで取り消し · 空にすると消えます");
    }
    private void TextEditorKey(object sender, KeyEventArgs e)
    {
        // The IME takes the mode key for itself on some systems and ignores it on
        // others; either way the pressed key is readable here, and the mode ends
        // up as the press asked for rather than wherever the IME left it.
        var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (key is Key.KanjiMode or Key.OemAuto or Key.OemEnlw or Key.ImeModeChange
            || (key == Key.Space && (Keyboard.Modifiers & ModifierKeys.Control) != 0))
        { e.Handled = true; ToggleIme(); return; }
        if (e.Key == Key.Escape) { e.Handled = true; CancelText(); }
        else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { e.Handled = true; CommitText(); }
    }
    private void CommitText()
    {
        if (textEditorRow.Visibility != Visibility.Visible) return;
        string text = textEditor.Text;
        bool edit = retyping;
        CloseTextEditor();
        if (edit) viewport.ApplyTextEdit(text); else viewport.AddText(textOrigin, text);
    }
    private void CancelText()
    {
        if (textEditorRow.Visibility != Visibility.Visible) return;
        CloseTextEditor();
    }
    private void CloseTextEditor()
    {
        textEditorRow.Visibility = Visibility.Collapsed; textEditor.Text = string.Empty; retyping = false;
        if (textEditorHeldTopmost) { Topmost = true; textEditorHeldTopmost = false; }
        if (!closed) viewport.Focus();
    }
    private void Undo() { viewport.CancelInteraction(); document.Undo(); }
    private void Clear() { viewport.CancelInteraction(); document.Clear(); }
    private void ViewChanged()
    {
        toolbar.Update(viewport.Tool, document.History.CanUndo, viewport.ToolColor);
        ShowStatus(ViewStatus());
    }
    private string ViewStatus() => $"{viewport.Zoom.Zoom:P0}" + viewport.Tool switch
    {
        DrawingTool.Highlighter => $"  ·  蛍光ペン {viewport.Highlighter.Width:0}px  ·  Alt / Spaceでパン",
        DrawingTool.Pen => $"  ·  ペン {viewport.Pen.Width:0}px  ·  Alt / Spaceでパン",
        DrawingTool.Text => $"  ·  テキスト {viewport.TextSize:0}px  ·  クリックで入力、文字をドラッグで移動、[ ]で大きさ",
        _ => ""
    };
    private void ShowStatus(string text)
    {
        if (closed) return;
        statusText.Text = text; status.Visibility = Visibility.Visible;
        statusTimer.Stop(); statusTimer.Start();
    }
    private void StatusExpired(object? sender, EventArgs e)
    {
        statusTimer.Stop();
        if (viewport.Tool != DrawingTool.None) statusText.Text = ViewStatus();
        else status.Visibility = Visibility.Collapsed;
    }
    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        void Add(string label, string gesture, Action action)
        {
            var item = new MenuItem { Header = label, InputGestureText = gesture };
            item.Click += (_, _) => action(); menu.Items.Add(item);
        }
        Add("閉じる", "Delete", Close);
        // The two switches used most often are set in bold so they stand out.
        var autoClose = new MenuItem { Header = "この画像から3秒で閉じる", InputGestureText = "T", IsCheckable = true, FontWeight = FontWeights.Bold };
        autoClose.Click += (_, _) => { SetNextCapturesAutoClose(autoClose.IsChecked); autoClose.IsChecked = AutoCloseEnabled; }; menu.Items.Add(autoClose);
        menu.Items.Add(new Separator());
        Add("コピー", "Ctrl+C", () => _ = CopyAsync());
        Add("PNGで保存…", "Ctrl+S", Save);
        var exportHeader = new MenuItem { Header = "コピー対象に上部のカウンターと日時情報を含める", IsCheckable = true, FontWeight = FontWeights.Bold };
        exportHeader.Click += (_, _) => SetExportHeaderMode(exportHeader.IsChecked);
        menu.Items.Add(exportHeader);
        menu.Items.Add(new Separator());
        Add("等倍表示", "1 / Ctrl+0", viewport.ActualSize);
        Add("ウィンドウに合わせる", "F", viewport.Fit);
        var drawing = new MenuItem { Header = "描画ツール" };
        var marker = new MenuItem { Header = "蛍光ペン", InputGestureText = "H", IsCheckable = true };
        marker.Click += (_, _) => viewport.ToggleHighlighter(); drawing.Items.Add(marker);
        var plain = new MenuItem { Header = "ペン", InputGestureText = "P", IsCheckable = true };
        plain.Click += (_, _) => viewport.TogglePen(); drawing.Items.Add(plain);
        var label = new MenuItem { Header = "テキスト", InputGestureText = "X", IsCheckable = true };
        label.Click += (_, _) => viewport.ToggleText(); drawing.Items.Add(label);
        drawing.Items.Add(new Separator());
        // The colours belong to whichever tool is in hand and are saved with it.
        foreach (var color in DrawingPalette.Colors)
        {
            var item = new MenuItem { Header = color.Name, Icon = new System.Windows.Shapes.Ellipse { Width = 12, Height = 12, Fill = (Brush)new BrushConverter().ConvertFromString(color.Hex)! } };
            item.Click += (_, _) => PickColor(DrawingPalette.Parse(color.Hex));
            drawing.Items.Add(item);
        }
        drawing.Items.Add(new Separator());
        var thin = new MenuItem { Header = "細くする", InputGestureText = "[" }; thin.Click += (_, _) => ChangeWidth(-1); drawing.Items.Add(thin);
        var thick = new MenuItem { Header = "太くする", InputGestureText = "]" }; thick.Click += (_, _) => ChangeWidth(1); drawing.Items.Add(thick);
        menu.Items.Add(drawing);
        Add("描画をクリア", "Ctrl+Delete", Clear);
        var topmost = new MenuItem { Header = "常に手前に表示", IsCheckable = true };
        topmost.Click += (_, _) => Topmost = topmost.IsChecked; menu.Items.Add(topmost);
        menu.Items.Add(new Separator());
        Add("閉じる", "Delete", Close);
        menu.Opened += (_, _) =>
        {
            viewport.CancelInteraction(); topmost.IsChecked = Topmost;
            marker.IsChecked = viewport.Tool == DrawingTool.Highlighter;
            plain.IsChecked = viewport.Tool == DrawingTool.Pen;
            label.IsChecked = viewport.Tool == DrawingTool.Text;
            autoClose.IsChecked = AutoCloseEnabled;
            exportHeader.IsChecked = settings.ExportHeaderEnabled;
        };
        return menu;
    }
    private async Task CopyAsync(bool allowClose = true, string success = "コピーしました")
    {
        manualCopyRequested = true;
        if (copying || closed) return;
        copying = true;
        try
        {
            CommitText(); viewport.CancelInteraction();
            if (!await clipboard.CopyAsync(exporter.Compose(document, settings.CopyIncludesAnnotations, settings.ExportBorderEnabled, ExportHeader))) return;
            if (closed) return;
            ShowStatus(success); if (allowClose && settings.CloseAfterCopy) Close();
        }
        catch (Exception ex) { ShowStatus("コピーできません: " + ex.Message); }
        finally { copying = false; }
    }
    internal async Task CopyInitialCaptureAsync()
    {
        var image = document.Image;
        if (image == null) return;
        // Let the image appear before PNG encoding. The original bitmap stays alive
        // until the copy finishes even if the user immediately closes its window.
        await Dispatcher.Yield(DispatcherPriority.Background);
        if (manualCopyRequested) return;
        try
        {
            if (await clipboard.CopyAsync(ImageExportService.Decorate(image, settings.ExportBorderEnabled, ExportHeader))) ShowStatus("キャプチャをコピーしました");
        }
        catch (Exception ex) { ShowStatus("自動コピーできません: " + ex.Message); }
        // CloseAfterCopy applies only to an explicit copy, not capture-time copying.
    }
    private void Save()
    {
        CommitText(); viewport.CancelInteraction();
        var dialog = new SaveFileDialog { Filter = "PNG画像 (*.png)|*.png", DefaultExt = ".png", FileName = $"Capture_{DateTime.Now:yyyyMMdd_HHmmss}.png", AddExtension = true };
        autoCloseTimer.Stop();
        try
        {
            if (dialog.ShowDialog(this) != true) return;
            exporter.SavePng(document, dialog.FileName, settings.ExportBorderEnabled, ExportHeader); ShowStatus("保存しました");
        }
        catch (Exception ex) { ShowStatus("保存できません: " + ex.Message); }
        finally { if (!closed && AutoCloseEnabled) StartAutoClose(); }
    }
}
