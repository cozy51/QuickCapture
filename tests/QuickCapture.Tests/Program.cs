using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuickCapture.Controls;
using QuickCapture.Interop;
using QuickCapture.Models;
using QuickCapture.Services;
using QuickCapture.Windows;

namespace QuickCapture.Tests;

internal static class Program
{
    private static int passed;
    private static string root = "";
    [STAThread]
    private static int Main(string[] args)
    {
        root = Directory.GetCurrentDirectory();
        Console.WriteLine("Test arguments: " + string.Join(" | ", args));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int result = 0;
        app.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                Run("Physical selection / negative monitor coordinates", Geometry);
                Run("Zoom anchors / DPI 100, 125, 150% / pan", Zoom);
                Run("Frame border and band keep their pixel size at any scaling", FrameMetrics);
                Run("Left drag moves / Alt or Space pans / H or Ctrl draws", DragMapping);
                Run("Embedded multi-resolution application and tray icons", Icons);
                Run("Undo / redo / clear / branching / history limit", History);
                Run("Highlighter source pixels / opacity / PNG", Rendering);
                Run("Export border / intact edge pixels / annotations / PNG / opt-out", ExportBorder);
                Run("Header record / band above the capture / close button excluded", ExportHeader);
                Run("Axis correction / preserves curves / corrected Undo and export", Straightening);
                Run("Crop owns only selected pixels", Crop);
                Run("Settings validation / persistence / corrupt JSON", Settings);
                Run("Global shortcut conflict and cleanup", Hotkey);
                await WindowsAndMemory();
                await CaptureSequence();
                await HeaderCloseButton();
                await RecordHeaderMode(args.Contains("--integration"));
                await AutoCloseWindows();
                await NextCaptureMode();
                await ZoomWindowSizing();
                await CaptureModeAppearance();
                if (args.Contains("--integration"))
                {
                    await ClipboardRoundtrip();
                    await AutomaticCopy();
                    await OverlayGeometry();
                    await NativeCapture();
                    await MonitorTransition();
                    await Benchmark();
                }
                await Screenshot();
                Console.WriteLine($"PASS: {passed} checks. OS={Environment.OSVersion}, x64={Environment.Is64BitProcess}");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally { app.Shutdown(); }
        });
        app.Run(); return result;
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Near(double actual, double expected, string message) => Assert(Math.Abs(actual - expected) < 0.00001, $"{message}: {actual} != {expected}");
    private static void Run(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
    private static BitmapSource White(int w = 200, int h = 100)
    {
        var bytes = new byte[w * h * 4]; Array.Fill(bytes, (byte)255);
        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bytes, w * 4); bitmap.Freeze(); return bitmap;
    }
    private static void Geometry()
    {
        var bounds = PixelGeometry.Union(new[] { new Int32Rect(-3840, -500, 3840, 2160), new Int32Rect(0, 0, 2560, 1440) });
        Assert(bounds == new Int32Rect(-3840, -500, 6400, 2160), "Virtual desktop union");
        Assert(PixelGeometry.Selection(new Point(100, 400), new Point(-750, -20), bounds) == new Int32Rect(-750, -20, 850, 420), "Reverse drag across origin");
        Assert(PixelGeometry.Selection(new Point(-9000, -9000), new Point(9000, 9000), bounds) == bounds, "Selection clamps to desktop");
    }
    private static void Zoom()
    {
        foreach (double dpi in new[] { 1d, 1.25, 1.5 })
        {
            var zoom = new ZoomController(); zoom.SetDpi(dpi, new Point());
            zoom.ActualSize(new Size(700, 450), new Size(1600, 900)); Near(zoom.ViewScale * dpi, 1, "Physical 100%");
            var mouse = new Point(321, 123); var before = zoom.ToImage(mouse);
            zoom.Wheel(120, mouse); var after = zoom.ToImage(mouse);
            Near(after.X, before.X, "Zoom anchor X"); Near(after.Y, before.Y, "Zoom anchor Y");
            zoom.Pan(new Vector(63, -27)); var projected = zoom.ToViewport(before);
            Near(projected.X, mouse.X + 63, "Pan X"); Near(projected.Y, mouse.Y - 27, "Pan Y");
            var inverse = zoom.ToImage(projected); Near(inverse.X, before.X, "Drawing inverse X"); Near(inverse.Y, before.Y, "Drawing inverse Y");
            zoom.Fit(new Size(700, 450), new Size(3840, 2160)); Near(zoom.ViewScale, 700d / 3840, "4K fit");
            zoom.SetZoom(100, mouse); Near(zoom.Zoom, ZoomController.Maximum, "Maximum zoom");
            zoom.SetZoom(0, mouse); Near(zoom.Zoom, ZoomController.Minimum, "Minimum zoom");
        }
        var moving = new ZoomController(); var anchor = new Point(150, 100); var source = moving.ToImage(anchor);
        moving.SetDpi(1.5, anchor); Near(moving.ToImage(anchor).X, source.X, "Monitor transition anchor");
    }
    private static HighlighterStroke Stroke(double y = 50) => new(new[] { new Point(20, y), new Point(80, y), new Point(180, y) }, Colors.Yellow, 20, 0.45);
    /// The window frame is drawn in physical pixels: moving a capture to a monitor
    /// with another scaling must not change the border, the band or the dashes.
    private static void FrameMetrics()
    {
        foreach (double scaling in new[] { 1d, 1.25, 1.5, 2d })
        {
            var metrics = CaptureFrame.Metrics(scaling);
            Near(metrics.Left * scaling, CaptureFrame.Thickness, "Left border pixels");
            Near(metrics.Right * scaling, CaptureFrame.Thickness, "Right border pixels");
            Near(metrics.Bottom * scaling, CaptureFrame.Thickness, "Bottom border pixels");
            Near((metrics.Top - metrics.Bottom) * scaling, CaptureFrame.HeaderHeight, "Header band pixels");
        }
    }
    private static void DragMapping()
    {
        Assert(ImageViewport.ResolveDrag(false, ModifierKeys.None, false) == ImageViewport.DragAction.MoveWindow, "Default left drag moves window");
        Assert(ImageViewport.ResolveDrag(false, ModifierKeys.Alt, false) == ImageViewport.DragAction.PanImage, "Alt left drag pans");
        Assert(ImageViewport.ResolveDrag(true, ModifierKeys.None, false) == ImageViewport.DragAction.Highlight, "H left drag draws");
        Assert(ImageViewport.ResolveDrag(true, ModifierKeys.Alt, false) == ImageViewport.DragAction.PanImage, "Alt overrides highlighter");
        Assert(ImageViewport.ResolveDrag(true, ModifierKeys.None, true) == ImageViewport.DragAction.PanImage, "Space overrides highlighter");
        Assert(ImageViewport.ResolveDrag(false, ModifierKeys.Control, false) == ImageViewport.DragAction.Highlight, "Ctrl draws without H mode");
        Assert(ImageViewport.ResolveDrag(false, ModifierKeys.Control | ModifierKeys.Alt, false) == ImageViewport.DragAction.PanImage, "Alt overrides Ctrl drawing");
        Assert(ImageViewport.ResolveDrag(false, ModifierKeys.Control, true) == ImageViewport.DragAction.PanImage, "Space overrides Ctrl drawing");
    }
    private static void Icons()
    {
        using var stream = Application.GetResourceStream(new Uri("/QuickCapture;component/Assets/QuickCapture.ico", UriKind.Relative))!.Stream;
        var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Assert(decoder.Frames.Select(f => f.PixelWidth).OrderBy(x => x).SequenceEqual(new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 }), "Icon supports tray through high-DPI shell sizes");
        Assert(AppIcon.Image.IsFrozen, "Window icon is shared and frozen");
        using var tray = AppIcon.CreateTrayIcon();
        Assert(tray.Width == 32 && tray.Height == 32, "Tray icon remains usable after resource stream closes");
    }
    private static async Task CaptureSequence()
    {
        var captured = new DateTimeOffset(2026, 9, 9, 12, 34, 56, TimeSpan.FromHours(9));
        using var firstDoc = new ImageDocument(White(), captured);
        using var secondDoc = new ImageDocument(White(), captured.AddSeconds(10));
        using var thirdDoc = new ImageDocument(White(), captured.AddSeconds(20));
        var region = new Int32Rect(200, 200, 200, 100);
        var first = new CaptureWindow(firstDoc, region);
        var second = new CaptureWindow(secondDoc, region);
        var third = new CaptureWindow(thirdDoc, region);
        try
        {
            third.Show(); first.Show(); second.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert(first.CaptureNumber == 1 && second.CaptureNumber == 2 && third.CaptureNumber == 3 && first.CaptureCount == 3, "Numbers follow capture time rather than show order");
            first.Activate(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert(first.CaptureNumber == 1 && third.CaptureNumber == 3, "Activation does not reorder captures");
            second.Hide(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert(first.CaptureCount == 2 && third.CaptureNumber == 2, "Hidden capture excluded");
            second.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert(second.CaptureNumber == 2 && third.CaptureCount == 3, "Showing restores capture order");
            first.WindowState = WindowState.Minimized; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert(second.CaptureNumber == 1 && third.CaptureCount == 2, "Minimized capture excluded");
            first.WindowState = WindowState.Normal; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert(first.CaptureNumber == 1 && third.CaptureCount == 3, "Restore preserves order");
            second.Close(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert(first.CaptureCount == 2 && third.CaptureNumber == 2 && third.CaptureCount == 2, "Closing renumbers remaining captures");
            Assert(first.CapturedAt == captured && first.Title.Contains(captured.ToLocalTime().ToString("HH:mm:ss")), "Capture timestamp remains fixed");
            var frame = (CaptureFrame)first.Content;
            Assert(frame.Child.TranslatePoint(new Point(), frame).Y >= frame.BandHeight, "Metadata sits above the image border");
            firstDoc.Add(Stroke());
            var exported = new ImageExportService().Compose(firstDoc);
            Assert(exported.PixelWidth == 200 && exported.PixelHeight == 100, "Metadata and frame excluded from image export");
        }
        finally { first.Close(); second.Close(); third.Close(); }
        passed++; Console.WriteLine("PASS Capture numbering / hide, minimize, restore, close / fixed timestamp / metadata excluded from export");
    }
    private static async Task ZoomWindowSizing()
    {
        foreach (var monitor in NativeMethods.Monitors())
        {
            using var doc = new ImageDocument(White(400, 240));
            var window = new CaptureWindow(doc, new Int32Rect(monitor.X + 180, monitor.Y + 180, 400, 240));
            try
            {
                window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var view = ((Grid)((Border)window.Content).Child).Children.OfType<ImageViewport>().Single();
                var hwnd = new WindowInteropHelper(window).Handle;
                GetWindowRect(hwnd, out var opened);
                // No black band around the picture: the window is the capture plus the
                // frame in pixels, whatever scaling this monitor uses.
                int frame = (int)CaptureFrame.Thickness * 2, band = (int)CaptureFrame.HeaderHeight;
                Assert(opened.Pixels.Width == 400 + frame && opened.Pixels.Height == 240 + frame + band,
                    $"Window hugs the capture in pixels: {opened.Pixels.Width} x {opened.Pixels.Height}");
                // The picture must land back on the pixels it was taken from.
                var area = NativeMethods.WorkArea(new Point(monitor.X + 180, monitor.Y + 180));
                int pictureX = opened.Pixels.X + (int)CaptureFrame.Thickness, pictureY = opened.Pixels.Y + band + (int)CaptureFrame.Thickness;
                if (monitor.X + 180 - CaptureFrame.Thickness >= area.X && monitor.Y + 180 - band - CaptureFrame.Thickness >= area.Y)
                    Assert(pictureX == monitor.X + 180 && pictureY == monitor.Y + 180, $"Picture covers the captured pixels: {pictureX}, {pictureY}");
                GetWindowRect(hwnd, out var before);
                var anchor = new Point(75, 65); var source = view.Zoom.ToImage(anchor); var screen = view.PointToScreen(anchor);
                view.ZoomAt(120, anchor); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                GetWindowRect(hwnd, out var bigger);
                Assert(bigger.Pixels.Width > before.Pixels.Width && bigger.Pixels.Height > before.Pixels.Height, "Window grows with zoom");
                var after = view.PointToScreen(view.Zoom.ToViewport(source));
                Assert((after - screen).Length < 1, $"Screen anchor moved on zoom: {screen} -> {after}");
                view.ZoomAt(-120, view.PointFromScreen(screen)); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                GetWindowRect(hwnd, out var smaller); Assert(smaller.Pixels.Width < bigger.Pixels.Width && smaller.Pixels.Height < bigger.Pixels.Height, "Window shrinks with zoom");
                view.ZoomAt(6000, view.PointFromScreen(screen)); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var work = NativeMethods.WorkArea(screen); GetWindowRect(hwnd, out var limit);
                Assert(limit.Pixels.Width >= doc.Width * view.Zoom.Zoom && limit.Pixels.Height >= doc.Height * view.Zoom.Zoom, $"Window must grow to image size past screen edges: {limit.Pixels}");
                Assert(limit.Bottom > work.Y + work.Height, "Window grows below the screen instead of stopping");
                Assert(Math.Abs(view.Zoom.Offset.Y) < 2 && Math.Abs(view.Zoom.Offset.X) < 2, $"No black top/left strip after zoom: {view.Zoom.Offset}");
                after = view.PointToScreen(view.Zoom.ToViewport(source)); Assert((after - screen).Length < 1, "Screen anchor survives maximum window size");
                view.ActualSize(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); GetWindowRect(hwnd, out var actual);
                Assert(actual.Pixels.Width < limit.Pixels.Width, "100% restores window size");
            }
            finally { window.Close(); }
        }
        passed++; Console.WriteLine("PASS Zoom beyond screen edges / no black top strip / screen anchor / 100%");
    }
    private static async Task AutoCloseWindows()
    {
        using var temporaryDoc = new ImageDocument(White());
        using var regularDoc = new ImageDocument(White());
        using var pinnedDoc = new ImageDocument(White());
        var nextSettings = new AppSettings { AutoCloseCaptures = true };
        var region = new Int32Rect(200, 200, 200, 100);
        var temporary = new CaptureWindow(temporaryDoc, region, nextSettings);
        var regular = new CaptureWindow(regularDoc, region);
        var pinned = new CaptureWindow(pinnedDoc, region, nextSettings);
        var elapsed = Stopwatch.StartNew();
        TimeSpan? closedAt = null;
        temporary.Closed += (_, _) => closedAt = elapsed.Elapsed;
        try
        {
            temporary.Show(); regular.Show(); pinned.Show();
            pinned.SetAutoClose(false);
            nextSettings.AutoCloseCaptures = false;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert(temporary.AutoCloseEnabled && !regular.AutoCloseEnabled && !pinned.AutoCloseEnabled, "Each capture owns its close mode independently of future settings");
            Assert(((CaptureFrame)temporary.Content).AutoClose && !((CaptureFrame)pinned.Content).AutoClose, "Frame distinguishes temporary and retained images");
            Assert(temporary.Countdown != null && regular.Countdown == null && pinned.Countdown == null, $"Only a temporary capture counts down: {temporary.Countdown}");
            await Task.Delay(2400);
            Assert(temporary.IsVisible, "Temporary capture remains visible before three seconds");
            Assert(temporary.Countdown == "1", $"The number counts the seconds down: {temporary.Countdown}");
            await Task.Delay(1100);
            Assert(closedAt.HasValue && closedAt.Value.TotalSeconds >= 2.9 && closedAt.Value.TotalSeconds < 3.6, "Capture closes after three seconds");
            Assert(temporaryDoc.Image == null && regular.IsVisible && pinned.IsVisible, "Only temporary image closes and releases bitmap");
            Assert(regular.CaptureCount == 2 && pinned.CaptureCount == 2, "Auto-close renumbers retained images");
            regular.SetAutoClose(true);
            await Task.Delay(600);
            regular.SetAutoClose(false);
            await Task.Delay(2600);
            Assert(regular.IsVisible, "Disabling auto-close cancels its pending timer");
            pinned.SetAutoClose(true); pinned.Close();
            Assert(pinnedDoc.Image == null, "Manual close disposes image with active timer");
        }
        finally { temporary.Close(); regular.Close(); pinned.Close(); }
        passed++; Console.WriteLine("PASS Three-second close / mixed retained captures / pin cancels timer / release and renumber");
    }
    private static async Task NextCaptureMode()
    {
        var preferences = new AppSettings();
        var opened = new List<CaptureWindow>();
        CaptureWindow Create()
        {
            var window = new CaptureWindow(new ImageDocument(White()), new Int32Rect(200, 200, 200, 100), preferences with { },
                enabled => preferences = preferences with { AutoCloseCaptures = enabled });
            opened.Add(window); window.Show(); return window;
        }
        MenuItem Mode(CaptureWindow window)
        {
            window.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            return window.ContextMenu.Items.OfType<MenuItem>().Single(item => item.InputGestureText == "T");
        }
        void SetMode(CaptureWindow window, bool enabled)
        {
            var item = Mode(window); item.IsChecked = enabled;
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        }
        try
        {
            var original = Create(); var peer = Create();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            SetMode(original, true);
            Assert(preferences.AutoCloseCaptures, "The mode is kept for the captures that follow");
            Assert(!Mode(peer).IsChecked && Mode(original).IsChecked, "Each menu shows the mode of its own image");
            Assert(original.AutoCloseEnabled && !peer.AutoCloseEnabled, "The image whose menu was used switches at once, the others keep theirs");
            Assert(original.Countdown == "3", $"The switched image starts counting down: {original.Countdown}");
            var next = Create(); var following = Create();
            Assert(next.AutoCloseEnabled && following.AutoCloseEnabled, "Mode applies continuously to future captures");
            SetMode(peer, false);
            var retained = Create();
            Assert(!retained.AutoCloseEnabled && next.AutoCloseEnabled && following.AutoCloseEnabled, "Turning off frees that image and future captures, not the armed ones");
            await Task.Delay(3500);
            Assert(!original.IsVisible && !next.IsVisible && !following.IsVisible, "The switched image and the temporary captures expire");
            Assert(peer.IsVisible && retained.IsVisible, "Images left on the retained mode stay open");
        }
        finally { foreach (var window in opened) window.Close(); }
        passed++; Console.WriteLine("PASS Context menu switches that image at once / shared mode / continuous captures / other images unchanged");
    }
    private static async Task CaptureModeAppearance()
    {
        var bounds = new Int32Rect(100, 100, 500, 350);
        var original = White(500, 350);
        var overlay = new CaptureOverlayWindow(original, bounds, bounds);
        try
        {
            overlay.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            overlay.SetPointer(new Point(200, 180));
            overlay.SetSelection(new Int32Rect(125, 125, 230, 180)); overlay.UpdateLayout();
            var grid = (Grid)overlay.Content;
            var layer = grid.Children.OfType<Canvas>().Single();
            var cat = layer.Children.OfType<CaptureCat>().Single();
            Assert(cat.Visibility == Visibility.Visible && !cat.IsHitTestVisible, "Capture cat visible and does not block selection");
            Assert(Canvas.GetLeft(cat) > 100 && Canvas.GetLeft(cat) <= 104 && Canvas.GetTop(cat) > 80 && Canvas.GetTop(cat) <= 86, "Cat is close to lower-right of pointer");
            var image = new RenderTargetBitmap((int)grid.ActualWidth, (int)grid.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            image.Render(grid);
            var pixel = new byte[4]; image.CopyPixels(new Int32Rect(400, 40, 1, 1), pixel, 4, 0);
            Assert(pixel[0] == 255 && pixel[1] == 255 && pixel[2] == 255, "Outside selection is not darkened");
            Directory.CreateDirectory(Path.Combine(root, "docs"));
            using (var file = File.Create(Path.Combine(root, "docs", "capture-mode.png"))) ImageExportService.WritePng(image, file);
            overlay.SetPointer(new Point(220, 210)); overlay.UpdateLayout();
            Assert(Canvas.GetLeft(cat) > 120 && Canvas.GetTop(cat) > 110, "Cat follows pointer");
            overlay.SetPointer(new Point(-200, -200)); Assert(cat.Visibility == Visibility.Collapsed, "Cat hidden on other monitors");
            var clean = new CaptureService().Crop(original, new Int32Rect(0, 0, 500, 350));
            clean.CopyPixels(new Int32Rect(120, 110, 1, 1), pixel, 4, 0); Assert(pixel[0] == 255, "Cat not baked into capture");
        }
        finally { overlay.ReleaseImage(); overlay.Close(); }
        passed++; Console.WriteLine("PASS Undimmed capture / following cat / decoration excluded from image");
    }
    private static async Task AutomaticCopy()
    {
        using var doc = new ImageDocument(White(200, 100)); doc.Add(Stroke());
        var window = new CaptureWindow(doc, new Int32Rect(100, 100, 200, 100), new AppSettings { CloseAfterCopy = true }); window.Show();
        try
        {
            await window.CopyInitialCaptureAsync();
            var image = Clipboard.GetImage(); Assert(image != null && image.PixelWidth == 202 && image.PixelHeight == 102, "Initial copy adds border outside original pixels");
            var pixel = new byte[4]; image!.CopyPixels(new Int32Rect(100, 50, 1, 1), pixel, 4, 0);
            Assert(pixel[0] == 255 && pixel[1] == 255 && pixel[2] == 255, "Automatic copy uses original capture");
            Assert(window.IsVisible, "Automatic copy must leave reference window open");
            image.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
            Assert(pixel[0] == 200 && pixel[1] == 200 && pixel[2] == 200, "Automatic clipboard copy contains gray border");
            window.SetExportBorder(false);
            await window.CopyInitialCaptureAsync();
            Assert(Clipboard.GetImage()?.PixelWidth == 200, "Turning border off applies to an existing window");
        }
        finally { window.Close(); }
        using var quickDoc = new ImageDocument(White(123, 87));
        var quick = new CaptureWindow(quickDoc, new Int32Rect(100, 100, 200, 100)); quick.Show();
        var copying = quick.CopyInitialCaptureAsync(); quick.Close(); await copying;
        Assert(Clipboard.GetImage()?.PixelWidth == 125, "Immediate close still copies captured image with border");
        passed++; Console.WriteLine("PASS Automatic original copy / stays open / copy survives immediate close");
    }
    private static void History()
    {
        using var doc = new ImageDocument(White());
        doc.Add(Stroke()); doc.Add(Stroke(20)); Assert(doc.Annotations.Count == 2, "Add");
        doc.Undo(); Assert(doc.Annotations.Count == 1 && doc.History.CanRedo, "Undo");
        doc.Redo(); Assert(doc.Annotations.Count == 2, "Redo");
        doc.Clear(); Assert(doc.Annotations.Count == 0, "Clear");
        doc.Undo(); Assert(doc.Annotations.Count == 2, "Undo clear");
        doc.Redo(); Assert(doc.Annotations.Count == 0, "Redo clear");
        doc.Undo(); doc.Undo(); doc.Add(Stroke(30)); Assert(!doc.History.CanRedo, "Branch clears redo");
        for (int i = 0; i < 120; i++) doc.Add(Stroke());
        int count = 0; while (doc.History.CanUndo) { doc.Undo(); count++; } Assert(count == 100, "Bounded history");
    }
    private static void Rendering()
    {
        using var doc = new ImageDocument(White()); doc.Add(Stroke());
        var bitmap = new ImageExportService().Compose(doc);
        Assert(bitmap.PixelWidth == 200 && bitmap.PixelHeight == 100, "Export original resolution");
        var pixels = new byte[200 * 100 * 4]; bitmap.CopyPixels(pixels, 800, 0);
        int center = (50 * 200 + 100) * 4;
        Assert(pixels[center] is >= 138 and <= 142 && pixels[center + 1] == 255 && pixels[center + 2] == 255, "45% yellow blend");
        Assert(pixels[0] == 255, "Outside stroke untouched");
        Assert(ReferenceEquals(new ImageExportService().Compose(doc, false), doc.Image), "Copy original setting");
        using var encoded = new MemoryStream(); ImageExportService.WritePng(bitmap, encoded); encoded.Position = 0;
        var decoded = BitmapFrame.Create(encoded, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Assert(decoded.PixelWidth == 200 && decoded.PixelHeight == 100, "PNG decode dimensions");
        var tool = new HighlighterTool(); tool.Begin(new Point(1, 1)); tool.Add(new Point(10, 10), 0.1); Assert(tool.Finish() != null && !tool.IsDrawing, "Stroke commits");
        tool.Begin(new Point(2, 2)); tool.Cancel(); Assert(tool.Preview == null, "Draft cancel");
    }
    private static void ExportBorder()
    {
        using var doc = new ImageDocument(White());
        var exporter = new ImageExportService();
        var framed = exporter.Compose(doc, includeBorder: true);
        Assert(framed.PixelWidth == 202 && framed.PixelHeight == 102 && framed.IsFrozen, "Border adds exactly one pixel on every side");
        var pixel = new byte[4];
        foreach (var point in new[] { new Point(0, 0), new Point(201, 101), new Point(0, 50), new Point(100, 101) })
        {
            framed.CopyPixels(new Int32Rect((int)point.X, (int)point.Y, 1, 1), pixel, 4, 0);
            Assert(pixel[0] == 200 && pixel[1] == 200 && pixel[2] == 200 && pixel[3] == 255, "Outer edge is opaque light gray");
        }
        var original = new byte[200 * 100 * 4]; var interior = new byte[original.Length];
        doc.Image!.CopyPixels(original, 800, 0);
        framed.CopyPixels(new Int32Rect(1, 1, 200, 100), interior, 800, 0);
        Assert(original.SequenceEqual(interior), "Original pixels including edges are preserved exactly");
        Assert(ReferenceEquals(exporter.Compose(doc, includeBorder: false), doc.Image), "Opt-out retains original dimensions without an unnecessary copy");
        doc.Add(Stroke());
        var annotated = exporter.Compose(doc, includeBorder: true);
        annotated.CopyPixels(new Int32Rect(101, 51, 1, 1), pixel, 4, 0);
        Assert(pixel[0] is >= 138 and <= 142 && pixel[1] == 255, "Annotation uses original image coordinates inside border");
        string path = Path.Combine(root, "artifacts", "border-test.png");
        exporter.SavePng(doc, path, includeBorder: true);
        using (var stream = File.OpenRead(path))
        {
            var saved = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Assert(saved.PixelWidth == 202 && saved.PixelHeight == 102, "PNG includes border");
            saved.CopyPixels(new Int32Rect(201, 101, 1, 1), pixel, 4, 0);
            Assert(pixel[0] == 200, "PNG preserves gray edge");
        }
        File.Delete(path);
    }
    /// Pixels in <paramref name="area"/> that differ from the recorded blue band.
    private static int Ink(BitmapSource image, Int32Rect area)
    {
        var pixels = new byte[area.Width * area.Height * 4];
        image.CopyPixels(area, pixels, area.Width * 4, 0);
        int count = 0;
        for (int i = 0; i < pixels.Length; i += 4)
            if (pixels[i] != 118 || pixels[i + 1] != 62 || pixels[i + 2] != 21) count++;
        return count;
    }
    private static void ExportHeader()
    {
        var captured = new DateTimeOffset(2026, 9, 9, 12, 34, 56, TimeSpan.FromHours(9));
        using var doc = new ImageDocument(White(640, 200), captured);
        var exporter = new ImageExportService();
        var info = new CaptureHeaderInfo(2, 3, captured);
        int band = (int)CaptureHeader.Height;
        var recorded = exporter.Compose(doc, includeBorder: false, header: info);
        Assert(recorded.PixelWidth == 640 && recorded.PixelHeight == 200 + band && recorded.IsFrozen, "Recorded image adds the header band above the capture");
        var original = new byte[640 * 200 * 4]; var below = new byte[original.Length];
        doc.Image!.CopyPixels(original, 2560, 0);
        recorded.CopyPixels(new Int32Rect(0, band, 640, 200), below, 2560, 0);
        Assert(original.SequenceEqual(below), "Original pixels stay intact below the band");
        Assert(Ink(recorded, new Int32Rect(0, 0, 44, band)) > 20, "Band carries the capture number");
        Assert(Ink(recorded, new Int32Rect(280, 0, 80, band)) > 20, "Band carries the capture time");
        Assert(Ink(recorded, new Int32Rect(550, 0, 80, band)) > 20, "Band carries the capture date");
        Assert(Ink(recorded, new Int32Rect(631, 0, 9, band)) == 0, "Recorded band leaves out the close button");
        var framed = exporter.Compose(doc, includeBorder: true, header: info);
        Assert(framed.PixelWidth == 642 && framed.PixelHeight == 202 + band, "Header band and gray border combine");
        var pixel = new byte[4];
        framed.CopyPixels(new Int32Rect(0, band, 1, 1), pixel, 4, 0);
        Assert(pixel[0] == 200 && pixel[1] == 200 && pixel[2] == 200, "Gray border starts below the band");
        framed.CopyPixels(new Int32Rect(639, 0, 1, 1), pixel, 4, 0);
        Assert(pixel[0] == 118 && pixel[1] == 62 && pixel[2] == 21 && pixel[3] == 255, "Band spans the full width in the deep blue recording color");
        Assert(ReferenceEquals(exporter.Compose(doc, includeBorder: false, header: null), doc.Image), "Opting out keeps the untouched original");
        Assert(ReferenceEquals(ImageExportService.Decorate(doc.Image!, false, null), doc.Image), "Capture-time copy without decorations is the original");
        Assert(ImageExportService.Decorate(doc.Image!, true, info).PixelHeight == 202 + band, "Capture-time copy records the band as well");
        Directory.CreateDirectory(Path.Combine(root, "artifacts"));
        string path = Path.Combine(root, "artifacts", "header-test.png");
        exporter.SavePng(doc, path, includeBorder: false, header: info);
        using (var stream = File.OpenRead(path))
        {
            var saved = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Assert(saved.PixelWidth == 640 && saved.PixelHeight == 200 + band, "PNG keeps the header band");
        }
        File.Delete(path);
    }
    private static async Task HeaderCloseButton()
    {
        var captured = new DateTimeOffset(2026, 9, 9, 12, 34, 56, TimeSpan.FromHours(9));
        using var doc = new ImageDocument(White(400, 240), captured);
        var window = new CaptureWindow(doc, new Int32Rect(200, 200, 400, 240));
        bool closed = false; window.Closed += (_, _) => closed = true;
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var frame = (CaptureFrame)window.Content;
            var button = frame.CloseButton;
            Assert(button.Top == 0 && Math.Abs(button.Height - frame.BandHeight) < 0.01 && Math.Abs(button.Right - frame.ActualWidth) < 0.5, "Close button occupies the top-right corner of the header");
            var corner = new Point(button.X + button.Width / 2, button.Height / 2);
            Assert(frame.IsOverCloseButton(corner) && !frame.IsOverCloseButton(new Point(12, corner.Y)), "Only the corner belongs to the close button");
            Assert(!frame.IsOverCloseButton(new Point(corner.X, frame.BandHeight + 6)), "The image area below the header is never the close button");
            double scaling = VisualTreeHelper.GetDpi(frame).DpiScaleX;
            Near(frame.BandHeight * scaling, CaptureFrame.HeaderHeight, "Shown band matches the exported band in pixels");
            Near(frame.BorderThickness.Left * scaling, CaptureFrame.Thickness, "Shown border keeps its pixel width");
            Assert(!frame.BeginClose(new Point(12, corner.Y)), "Pressing the header elsewhere still moves the window");
            Assert(frame.BeginClose(corner) && !frame.CompleteClose(new Point(12, corner.Y)) && !closed, "Releasing away from the button cancels the close");
            Assert(frame.BeginClose(corner) && frame.CompleteClose(corner), "Clicking the button closes the window");
            Assert(closed && doc.Image == null, "Close button releases the image like Delete");
        }
        finally { window.Close(); }
        passed++; Console.WriteLine("PASS Header close button / corner hit area / cancelled press / releases image");
    }
    private static async Task RecordHeaderMode(bool useClipboard)
    {
        var preferences = new AppSettings();
        var opened = new List<CaptureWindow>();
        CaptureWindow Create()
        {
            var window = new CaptureWindow(new ImageDocument(White()), new Int32Rect(200, 200, 200, 100), preferences with { }, null,
                enabled =>
                {
                    preferences = preferences with { ExportHeaderEnabled = enabled };
                    foreach (var open in opened) open.SetExportHeader(enabled);
                });
            opened.Add(window); window.Show(); return window;
        }
        MenuItem Mode(CaptureWindow window)
        {
            window.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            return window.ContextMenu.Items.OfType<MenuItem>().Single(item => (item.Header as string) == "コピー・保存に上部バーを含める");
        }
        void SetMode(CaptureWindow window, bool enabled)
        {
            var item = Mode(window); item.IsChecked = enabled;
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        }
        int band = (int)CaptureHeader.Height;
        try
        {
            var original = Create(); var peer = Create();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert(!((CaptureFrame)original.Content).RecordHeader, "Images start without the recording band");
            SetMode(original, true);
            Assert(preferences.ExportHeaderEnabled && Mode(peer).IsChecked, "Right-click switching is shared by the open images");
            Assert(((CaptureFrame)original.Content).RecordHeader && ((CaptureFrame)peer.Content).RecordHeader, "Every open image marks the recording band");
            var next = Create();
            Assert(((CaptureFrame)next.Content).RecordHeader && Mode(next).IsChecked, "Later captures continue recording the band");
            if (useClipboard)
            {
                await Task.Delay(300);
                Assert(Clipboard.GetImage()?.PixelHeight == 102 + band, "Switching on copies the image again with the band");
            }
            SetMode(next, false);
            Assert(!preferences.ExportHeaderEnabled && !((CaptureFrame)original.Content).RecordHeader, "Switching off reaches every open image");
            if (useClipboard)
            {
                await Task.Delay(300);
                Assert(Clipboard.GetImage()?.PixelHeight == 102, "Switching off copies the plain image again");
            }
            Assert(opened.TrueForAll(window => window.IsVisible), "Re-copying on switch never closes the image");
        }
        finally { foreach (var window in opened) window.Close(); }
        passed++; Console.WriteLine("PASS Right-click recording mode / shared by open images / later captures / immediate re-copy");
    }
    private static void Straightening()
    {
        var horizontal = StrokeStraightener.Snap(new[] { new Point(10, 20), new Point(60, 23), new Point(110, 26) });
        Assert(horizontal.Count == 2 && horizontal[0] == new Point(10, 20) && horizontal[1] == new Point(110, 20), "Nearly horizontal snaps to start height");
        var vertical = StrokeStraightener.Snap(new[] { new Point(30, 120), new Point(33, 70), new Point(35, 20) });
        Assert(vertical.Count == 2 && vertical[1] == new Point(30, 20), "Upward vertical snaps to start X");
        var reversed = StrokeStraightener.Snap(new[] { new Point(110, 20), new Point(60, 18), new Point(10, 16) });
        Assert(reversed[1] == new Point(10, 20), "Reverse horizontal");
        foreach (var unchanged in new[]
        {
            new[] { new Point(0, 0), new Point(50, 40), new Point(100, 80) },
            new[] { new Point(0, 0), new Point(50, 45), new Point(100, 0) },
            new[] { new Point(0, 0), new Point(100, 0), new Point(0, 0), new Point(100, 0) },
            new[] { new Point(0, 0), new Point(5, 0.5) },
            new[] { new Point(20, 20) }
        }) Assert(ReferenceEquals(StrokeStraightener.Snap(unchanged), unchanged), "Diagonals, curves, retraced strokes and dots preserved");
        var tool = new HighlighterTool { Width = 4 };
        Assert(tool.Color == (Color)ColorConverter.ConvertFromString("#68E675") && new AppSettings().HighlighterColor == "#68E675", "Default highlighter is green");
        tool.Begin(new Point(20, 40)); tool.Add(new Point(75, 42), 0.1); tool.Add(new Point(140, 44), 0.1);
        using var doc = new ImageDocument(White(160, 100)); doc.Add(tool.Finish()!);
        var exporter = new ImageExportService(); var corrected = exporter.Compose(doc); var pixel = new byte[4];
        corrected.CopyPixels(new Int32Rect(135, 40, 1, 1), pixel, 4, 0); Assert(pixel[0] < 240 && pixel[2] < 240, "Corrected horizontal row rendered green");
        corrected.CopyPixels(new Int32Rect(135, 45, 1, 1), pixel, 4, 0); Assert(pixel[0] == 255, "Uncorrected endpoint row is blank");
        doc.Undo(); Assert(doc.Annotations.Count == 0, "Corrected stroke undoes as one action");
        doc.Redo(); var redo = exporter.Compose(doc); redo.CopyPixels(new Int32Rect(135, 40, 1, 1), pixel, 4, 0); Assert(pixel[0] < 240, "Redo restores corrected stroke");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (BitmapSource Crop, WeakReference Desktop) Cropped()
    {
        var desktop = White(1920, 1080); return (new CaptureService().Crop(desktop, new Int32Rect(100, 200, 120, 80)), new WeakReference(desktop));
    }
    private static void Crop()
    {
        var result = Cropped(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert(!result.Desktop.IsAlive, "Crop must not retain desktop"); Assert(result.Crop.PixelWidth == 120 && result.Crop.IsFrozen, "Frozen cropped bitmap");
        var spatial = new byte[64 * 64 * 4];
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) { int i = (y * 64 + x) * 4; spatial[i] = (byte)x; spatial[i + 1] = (byte)y; spatial[i + 3] = 255; }
        var source = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, spatial, 256);
        var crop = new CaptureService().Crop(source, new Int32Rect(21, 17, 13, 11)); var bytes = new byte[13 * 11 * 4]; crop.CopyPixels(bytes, 52, 0);
        Assert(bytes[0] == 21 && bytes[1] == 17 && bytes[^4] == 33 && bytes[^3] == 27, "Crop exact source offsets and stride");
    }
    private static void Settings()
    {
        string path = Path.Combine(root, "artifacts", "test-settings.json"); var service = new SettingsService(path);
        var settings = new AppSettings { GlobalShortcut = "Ctrl+Alt+Q", HighlighterWidth = 34, CloseAfterCopy = true, AutoCloseCaptures = true, ExportBorderEnabled = false, ExportHeaderEnabled = true };
        service.Save(settings); Assert(service.Load() == settings, "Settings roundtrip");
        File.WriteAllText(path, "{}"); Assert(!service.Load().AutoCloseCaptures, "Older settings default to retaining captures");
        Assert(service.Load().ExportBorderEnabled && !service.Load().ExportHeaderEnabled, "Older settings enable the export border and record no header band");
        File.WriteAllText(path, "{bad"); Assert(service.Load().GlobalShortcut == "Ctrl+Shift+R" && service.LoadWarning != null, "Corrupt JSON fallback");
        bool invalid = false; try { AppSettings.ParseShortcut("R"); } catch (ArgumentException) { invalid = true; } Assert(invalid, "Unmodified key rejected");
        File.Delete(path);
    }
    private static void Hotkey()
    {
        using (var first = new GlobalHotkeyService())
        using (var second = new GlobalHotkeyService())
        {
            first.Register(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, Key.F11);
            bool conflict = false; try { second.Register(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, Key.F11); } catch (System.ComponentModel.Win32Exception) { conflict = true; }
            Assert(conflict, "Hotkey conflict reported");
        }
        using var again = new GlobalHotkeyService(); again.Register(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, Key.F11);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference OpenClose()
    {
        var image = White(1000, 800); var weak = new WeakReference(image);
        var doc = new ImageDocument(image); var window = new CaptureWindow(doc, new Int32Rect(100, 100, 500, 300));
        window.Show(); window.Close(); Assert(doc.Image == null && doc.Annotations.Count == 0, "Closed document released"); return weak;
    }
    private static async Task WindowsAndMemory()
    {
        var first = new CaptureWindow(new ImageDocument(White()), new Int32Rect(80, 80, 300, 200));
        var second = new CaptureWindow(new ImageDocument(White()), new Int32Rect(440, 80, 300, 200));
        first.Show(); second.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert(first.IsVisible && second.IsVisible && first.Topmost && second.Topmost, "Independent topmost windows");
        var firstView = ((Grid)((Border)first.Content).Child).Children.OfType<ImageViewport>().Single();
        var center = firstView.Zoom.ToViewport(new Point(100, 50));
        Near(center.X, firstView.ActualWidth / 2, "Initial image centered X"); Near(center.Y, firstView.ActualHeight / 2, "Initial image centered Y");
        first.Width = 400; first.Height = 300; first.UpdateLayout(); Assert(first.ActualWidth == 400, "Resize");
        center = firstView.Zoom.ToViewport(new Point(100, 50)); Near(center.X, firstView.ActualWidth / 2, "Resize keeps image centered");
        first.Close(); second.Close();
        var refs = Enumerable.Range(0, 12).Select(_ => OpenClose()).ToArray();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert(refs.All(r => !r.IsAlive), "Closed screenshots collectible"); passed++; Console.WriteLine("PASS Multiple windows / resize / 12 closed image references collected");
    }
    private static async Task ClipboardRoundtrip()
    {
        using var doc = new ImageDocument(White()); doc.Add(Stroke());
        await new ClipboardService().CopyAsync(new ImageExportService().Compose(doc));
        var image = Clipboard.GetImage(); Assert(image != null && image.PixelWidth == 200 && image.PixelHeight == 100, "Clipboard bitmap roundtrip");
        Assert(Clipboard.ContainsData("PNG"), "PNG clipboard format"); passed++; Console.WriteLine("PASS Windows clipboard Bitmap + PNG");
    }
    private static bool GetWindowRect(IntPtr window, out NativeMethods.RECT rect) => NativeMethods.GetWindowRect(window, out rect);
    private static async Task OverlayGeometry()
    {
        var monitors = NativeMethods.Monitors(); var bounds = PixelGeometry.Union(monitors);
        var desktop = White(bounds.Width, bounds.Height);
        foreach (var monitor in monitors)
        {
            var overlay = new CaptureOverlayWindow(desktop, bounds, monitor); overlay.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var hwnd = new WindowInteropHelper(overlay).Handle; GetWindowRect(hwnd, out var rect);
            Console.WriteLine($"Monitor: {monitor}; overlay: {rect.Pixels}; DPI: {NativeMethods.GetDpiForWindow(hwnd)}");
            overlay.ReleaseImage(); overlay.Close(); Assert(rect.Pixels == monitor, "Overlay covers native monitor pixels");
        }
        passed++; Console.WriteLine("PASS Native overlay positioning on connected monitors");
    }
    private static async Task Benchmark()
    {
        var samples = new List<double>();
        var desktop = White(3840, 2160); var service = new CaptureService();
        for (int i = 0; i < 8; i++)
        {
            var clock = Stopwatch.StartNew();
            var cropped = service.Crop(desktop, new Int32Rect(100, 100, 850, 420));
            var window = new CaptureWindow(new ImageDocument(cropped), new Int32Rect(100, 100, 850, 420));
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); clock.Stop();
            samples.Add(clock.Elapsed.TotalMilliseconds); window.Close();
        }
        Console.WriteLine($"BENCH crop+window+dispatcher idle 850x420: {string.Join(", ", samples.Select(s => s.ToString("F1")))} ms; median={samples.Order().ElementAt(4):F1} ms (not compositor latency)");
    }
    [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process, uint flags);
    private static async Task NativeCapture()
    {
        using var doc = new ImageDocument(White(400, 240));
        doc.Add(new HighlighterStroke(new[] { new Point(80, 120), new Point(320, 120) }, Colors.Yellow, 40, 0.45));
        var window = new CaptureWindow(doc, new Int32Rect(200, 200, 400, 240)); window.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(180);
        GetWindowRect(new WindowInteropHelper(window).Handle, out var rect);
        var viewport = ((Grid)((Border)window.Content).Child).Children.OfType<ImageViewport>().Single();
        var location = viewport.PointToScreen(viewport.Zoom.ToViewport(new Point(200, 120)));
        Console.WriteLine($"Native test window={rect.Pixels}; viewport={viewport.RenderSize}; sample={location}");
        var sample = new Int32Rect((int)location.X, (int)location.Y, 2, 2);
        var capture = new CaptureService(); var image = capture.Capture(sample); var bytes = new byte[16]; image.CopyPixels(bytes, 8, 0);
        Assert(bytes[0] is >= 130 and <= 150 && bytes[1] >= 245 && bytes[2] >= 245, $"Native captured highlighter pixels: BGR={bytes[0]},{bytes[1]},{bytes[2]}");
        uint before = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
        for (int i = 0; i < 40; i++) _ = capture.Capture(sample);
        uint after = GetGuiResources(Process.GetCurrentProcess().Handle, 0); Assert(after <= before + 2, $"GDI handles grew: {before} -> {after}");
        window.Close(); passed++; Console.WriteLine($"PASS GDI screen capture pixels / handles {before}->{after} after 40 captures");
    }
    private static async Task Screenshot()
    {
        using var document = DemoImage.Create();
        var window = new CaptureWindow(document, new Int32Rect(120, 120, 900, 620)); window.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var border = (Border)window.Content; var grid = (Grid)border.Child;
        var toolbar = grid.Children.OfType<CaptureToolbar>().Single(); toolbar.Visibility = Visibility.Visible;
        var viewport = grid.Children.OfType<ImageViewport>().Single(); viewport.Fit();
        window.UpdateLayout();
        var output = new RenderTargetBitmap((int)Math.Ceiling(border.ActualWidth), (int)Math.Ceiling(border.ActualHeight), 96, 96, PixelFormats.Pbgra32); output.Render(border);
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        using (var stream = File.Create(Path.Combine(root, "docs", "screenshot.png"))) ImageExportService.WritePng(output, stream);
        window.SetAutoClose(true);
        await Dispatcher.Yield(DispatcherPriority.Render);
        var temporary = new RenderTargetBitmap((int)Math.Ceiling(border.ActualWidth), (int)Math.Ceiling(border.ActualHeight), 96, 96, PixelFormats.Pbgra32); temporary.Render(border);
        using (var stream = File.Create(Path.Combine(root, "docs", "auto-close.png"))) ImageExportService.WritePng(temporary, stream);
        window.Close(); Console.WriteLine("Wrote docs/screenshot.png (actual WPF UI, synthetic reference image)");
    }
    private static async Task MonitorTransition()
    {
        var monitors = NativeMethods.Monitors();
        using var doc = new ImageDocument(White(400, 240)); doc.Add(Stroke(100));
        var window = new CaptureWindow(doc, new Int32Rect(200, 200, 400, 240)); window.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var frame = (CaptureFrame)window.Content;
        var view = ((Grid)frame.Child).Children.OfType<ImageViewport>().Single();
        view.ActualSize();
        var hwnd = new WindowInteropHelper(window).Handle;
        foreach (var monitor in monitors)
        {
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, monitor.X + 40, monitor.Y + 40, 600, 400, 0x0004 | 0x0010);
            await Task.Delay(80); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            // WPF answers the scaling change by growing the window; the pixel size must survive.
            GetWindowRect(hwnd, out var moved);
            Assert(moved.Pixels.Width == 600 && moved.Pixels.Height == 400, $"Monitor change keeps the window pixel size: {moved.Pixels.Width} x {moved.Pixels.Height}");
            double dpi = NativeMethods.GetDpiForWindow(hwnd) / 96d;
            Near(view.Zoom.DpiScale, dpi, "Moved window DPI"); Near(view.Zoom.ViewScale * dpi, 1, "Moved window physical 100%");
            // A monitor with another scaling must not change how the frame looks.
            double scaling = VisualTreeHelper.GetDpi(frame).DpiScaleX;
            Near(frame.BorderThickness.Left * scaling, CaptureFrame.Thickness, "Moved window border pixels");
            Near(frame.BandHeight * scaling, CaptureFrame.HeaderHeight, "Moved window band pixels");
            var p = new Point(130, 100); var back = view.Zoom.ToImage(view.Zoom.ToViewport(p)); Near(back.X, p.X, "Annotation inverse after move");
        }
        window.Close();
        using var largeDoc = new ImageDocument(White(3840, 2160));
        var large = new CaptureWindow(largeDoc, new Int32Rect(0, 0, 3840, 2160)); large.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var largeView = ((Grid)((Border)large.Content).Child).Children.OfType<ImageViewport>().Single();
        Assert(largeView.Zoom.ViewScale * 3840 <= largeView.ActualWidth + 1 && largeView.Zoom.ViewScale * 2160 <= largeView.ActualHeight + 1, "4K image initially fits available window");
        large.Close(); passed++; Console.WriteLine("PASS Actual monitor transition / physical 100% / 4K initial fit");
    }
}
