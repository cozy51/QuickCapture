using System;
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
    private readonly Func<bool> getNextAutoClose;
    private readonly Action<bool> setNextAutoClose;
    private readonly Action<bool> setExportHeader;
    private readonly ImageExportService exporter = new();
    private readonly ClipboardService clipboard = new();
    private readonly CaptureToolbar toolbar;
    private readonly Border status;
    private readonly TextBlock statusText;
    private readonly DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer autoCloseTimer = new() { Interval = TimeSpan.FromSeconds(3) };
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

    public CaptureWindow(ImageDocument document, Int32Rect region, AppSettings? settings = null, Func<bool>? getNextAutoClose = null, Action<bool>? setNextAutoClose = null, Action<bool>? setExportHeader = null)
    {
        this.document = document; this.settings = settings ?? new();
        this.getNextAutoClose = getNextAutoClose ?? (() => this.settings.AutoCloseCaptures);
        this.setNextAutoClose = setNextAutoClose ?? (enabled => this.settings.AutoCloseCaptures = enabled);
        this.setExportHeader = setExportHeader ?? (enabled => SetExportHeader(enabled));
        Title = $"QuickCapture · {document.Width} × {document.Height}";
        Icon = AppIcon.Image;
        Topmost = this.settings.AlwaysOnTop; ApplyMinimumSize(1);
        Width = Math.Max(MinWidth, region.Width + FrameThickness * 2); Height = Math.Max(MinHeight, region.Height + FrameThickness * 2 + CaptureFrame.HeaderHeight);
        MaxWidth = 32767; MaxHeight = 32767;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize;
        Background = new SolidColorBrush(Color.FromRgb(24, 27, 32));
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });
        viewport = new ImageViewport(document);
        viewport.Highlighter.Color = (Color)ColorConverter.ConvertFromString(this.settings.HighlighterColor);
        viewport.Highlighter.Width = this.settings.HighlighterWidth;
        viewport.Highlighter.Opacity = this.settings.HighlighterOpacity;
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
        toolbar = new CaptureToolbar(DragMove, () => _ = CopyAsync(), viewport.ToggleHighlighter, Undo, viewport.ActualSize, viewport.Fit, Save, Close) { Visibility = Visibility.Collapsed };
        grid.Children.Add(toolbar);
        statusText = new TextBlock { Foreground = Brushes.White, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        status = new Border { Background = new SolidColorBrush(Color.FromArgb(220, 29, 33, 41)), CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Child = statusText, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        grid.Children.Add(status);
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
        Loaded += (_, _) => { CaptureWindowRegistry.Register(this); if (AutoCloseEnabled) autoCloseTimer.Start(); };
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
        if (enabled && IsLoaded) autoCloseTimer.Start();
    }
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
        _ = CopyAsync(false, enabled ? "上部バー付きでコピーし直しました" : "画像だけでコピーし直しました");
    }
    /// The header of this window as copy and save should record it, or null while
    /// the export is the image alone.
    private CaptureHeaderInfo? ExportHeader => settings.ExportHeaderEnabled ? frame.Header : null;
    private void SetNextCapturesAutoClose(bool enabled)
    {
        try
        {
            setNextAutoClose(enabled);
            ShowStatus(enabled ? "次回以降のキャプチャは3秒で閉じます" : "次回以降のキャプチャは画面に残します");
        }
        catch (Exception ex) { ShowStatus("設定を保存できません: " + ex.Message); }
    }
    private void AutoCloseExpired(object? sender, EventArgs e)
    {
        autoCloseTimer.Stop();
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
        else if (message == 0x0231) sizingWidth = NativeMethods.GetWindowRect(hwnd, out var before) ? before.Pixels.Width : 0;
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
        int y = region.Y - (int)Math.Round(CaptureFrame.HeaderHeight);
        NativeMethods.SetWindowPos(hwnd, Topmost ? new IntPtr(-1) : new IntPtr(-2), Math.Clamp(region.X, work.X, work.X + work.Width - width), Math.Clamp(y, work.Y, work.Y + work.Height - height), width, height, 0x0010);
        // Width and Height still hold the layout size WPF created the window with,
        // and WPF applies them again after this; without the sync the window grows
        // back by the monitor scaling and leaves black bands around the picture.
        SyncLayoutSize(width, height);
    }
    private void ResizeForZoom(Point imageAnchor, Point screenAnchor)
    {
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
                case Key.T: if (!e.IsRepeat) SetNextCapturesAutoClose(!getNextAutoClose()); break;
                case Key.F: viewport.CancelInteraction(); viewport.Fit(); break;
                case Key.D1: case Key.NumPad1: viewport.CancelInteraction(); viewport.ActualSize(); break;
                case Key.OemOpenBrackets: viewport.ChangeWidth(-1); break;
                case Key.OemCloseBrackets: viewport.ChangeWidth(1); break;
                default: return;
            }
        }
        else return;
        e.Handled = true;
    }
    private void Undo() { viewport.CancelInteraction(); document.Undo(); }
    private void Clear() { viewport.CancelInteraction(); document.Clear(); }
    private void ViewChanged()
    {
        toolbar.Update(viewport.IsHighlighting || viewport.Highlighter.IsDrawing, document.History.CanUndo, viewport.Highlighter.Color);
        ShowStatus(ViewStatus());
    }
    private string ViewStatus() => $"{viewport.Zoom.Zoom:P0}" + (viewport.IsHighlighting ? $"  ·  蛍光ペン {viewport.Highlighter.Width:0}px  ·  Alt / Spaceでパン" : "");
    private void ShowStatus(string text)
    {
        if (closed) return;
        statusText.Text = text; status.Visibility = Visibility.Visible;
        statusTimer.Stop(); statusTimer.Start();
    }
    private void StatusExpired(object? sender, EventArgs e)
    {
        statusTimer.Stop();
        if (viewport.IsHighlighting) statusText.Text = ViewStatus();
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
        var autoClose = new MenuItem { Header = "次回以降のキャプチャを3秒で閉じる", InputGestureText = "T", IsCheckable = true };
        autoClose.Click += (_, _) => { SetNextCapturesAutoClose(autoClose.IsChecked); autoClose.IsChecked = getNextAutoClose(); }; menu.Items.Add(autoClose);
        var retain = new MenuItem { Header = "この画像を残す（自動終了を解除）" };
        retain.Click += (_, _) => SetAutoClose(false); menu.Items.Add(retain);
        menu.Items.Add(new Separator());
        Add("コピー", "Ctrl+C", () => _ = CopyAsync());
        Add("PNGで保存…", "Ctrl+S", Save);
        var exportHeader = new MenuItem { Header = "コピー・保存に上部バーを含める", IsCheckable = true };
        exportHeader.Click += (_, _) => SetExportHeaderMode(exportHeader.IsChecked);
        menu.Items.Add(exportHeader);
        menu.Items.Add(new Separator());
        Add("等倍表示", "1 / Ctrl+0", viewport.ActualSize);
        Add("ウィンドウに合わせる", "F", viewport.Fit);
        var highlighter = new MenuItem { Header = "蛍光ペン", InputGestureText = "H" };
        var toggle = new MenuItem { Header = "ON / OFF", IsCheckable = true };
        toggle.Click += (_, _) => viewport.ToggleHighlighter(); highlighter.Items.Add(toggle);
        foreach (var color in HighlighterTool.Palette)
        {
            var item = new MenuItem { Header = color.Name, Icon = new System.Windows.Shapes.Ellipse { Width = 12, Height = 12, Fill = (Brush)new BrushConverter().ConvertFromString(color.Hex)! } };
            item.Click += (_, _) => { viewport.CancelInteraction(); viewport.Highlighter.Color = (Color)ColorConverter.ConvertFromString(color.Hex); if (!viewport.IsHighlighting) viewport.ToggleHighlighter(); viewport.Refresh(); };
            highlighter.Items.Add(item);
        }
        highlighter.Items.Add(new Separator());
        var thin = new MenuItem { Header = "細くする", InputGestureText = "[" }; thin.Click += (_, _) => viewport.ChangeWidth(-1); highlighter.Items.Add(thin);
        var thick = new MenuItem { Header = "太くする", InputGestureText = "]" }; thick.Click += (_, _) => viewport.ChangeWidth(1); highlighter.Items.Add(thick);
        menu.Items.Add(highlighter);
        Add("描画をクリア", "Ctrl+Delete", Clear);
        var topmost = new MenuItem { Header = "常に手前に表示", IsCheckable = true };
        topmost.Click += (_, _) => Topmost = topmost.IsChecked; menu.Items.Add(topmost);
        menu.Items.Add(new Separator());
        Add("閉じる", "Delete", Close);
        menu.Opened += (_, _) =>
        {
            viewport.CancelInteraction(); toggle.IsChecked = viewport.IsHighlighting; topmost.IsChecked = Topmost;
            autoClose.IsChecked = getNextAutoClose(); retain.Visibility = AutoCloseEnabled ? Visibility.Visible : Visibility.Collapsed;
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
            viewport.CancelInteraction();
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
        viewport.CancelInteraction();
        var dialog = new SaveFileDialog { Filter = "PNG画像 (*.png)|*.png", DefaultExt = ".png", FileName = $"Capture_{DateTime.Now:yyyyMMdd_HHmmss}.png", AddExtension = true };
        autoCloseTimer.Stop();
        try
        {
            if (dialog.ShowDialog(this) != true) return;
            exporter.SavePng(document, dialog.FileName, settings.ExportBorderEnabled, ExportHeader); ShowStatus("保存しました");
        }
        catch (Exception ex) { ShowStatus("保存できません: " + ex.Message); }
        finally { if (!closed && AutoCloseEnabled) autoCloseTimer.Start(); }
    }
}
