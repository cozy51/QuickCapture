using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickCapture.Interop;
using QuickCapture.Controls;

namespace QuickCapture.Windows;

internal sealed class CaptureOverlayWindow : Window
{
    private readonly OverlaySurface surface;
    private readonly Canvas indicatorLayer = new() { IsHitTestVisible = false, ClipToBounds = true };
    private readonly CaptureCat cat = new() { Visibility = Visibility.Collapsed };
    private Point pointer;
    internal Int32Rect Bounds { get; }
    internal event Action<Point>? SelectionStarted;
    internal event Action<Point>? SelectionMoved;
    internal event Action<Point>? SelectionFinished;
    internal event Action<Point>? PointerMoved;
    internal event Action? Cancelled;
    private bool selecting;

    internal CaptureOverlayWindow(BitmapSource desktop, Int32Rect desktopBounds, Int32Rect monitor)
    {
        Bounds = monitor;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        Topmost = true; Background = Brushes.Black; Cursor = Cursors.Cross;
        surface = new OverlaySurface(desktop, desktopBounds, monitor);
        var grid = new Grid(); grid.Children.Add(surface); grid.Children.Add(indicatorLayer);
        indicatorLayer.Children.Add(cat); Content = grid;
        SizeChanged += (_, _) => SetPointer(pointer);
        SourceInitialized += (_, _) => NativeMethods.SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), monitor.X, monitor.Y, monitor.Width, monitor.Height, 0x0010);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Cancelled?.Invoke(); } };
        MouseLeftButtonDown += (_, e) => { selecting = true; CaptureMouse(); SelectionStarted?.Invoke(NativeMethods.CursorPosition()); e.Handled = true; };
        MouseMove += (_, _) =>
        {
            var point = NativeMethods.CursorPosition();
            PointerMoved?.Invoke(point);
            if (selecting) SelectionMoved?.Invoke(point);
        };
        MouseLeftButtonUp += (_, e) => { if (!selecting) return; selecting = false; ReleaseMouseCapture(); SelectionFinished?.Invoke(NativeMethods.CursorPosition()); e.Handled = true; };
        LostMouseCapture += (_, _) => { if (selecting) { selecting = false; Cancelled?.Invoke(); } };
    }
    internal void SetSelection(Int32Rect rect) { surface.Selection = rect; surface.InvalidateVisual(); }
    internal void SetPointer(Point point)
    {
        pointer = point;
        bool inside = point.X >= Bounds.X && point.X < Bounds.X + Bounds.Width && point.Y >= Bounds.Y && point.Y < Bounds.Y + Bounds.Height;
        cat.Visibility = inside ? Visibility.Visible : Visibility.Collapsed;
        if (!inside || indicatorLayer.ActualWidth <= 0 || indicatorLayer.ActualHeight <= 0) return;
        double x = (point.X - Bounds.X) * indicatorLayer.ActualWidth / Bounds.Width + 2;
        double y = (point.Y - Bounds.Y) * indicatorLayer.ActualHeight / Bounds.Height + 4;
        // Normally lower-right; stay visible if the pointer touches a monitor edge.
        Canvas.SetLeft(cat, Math.Clamp(x, 0, Math.Max(0, indicatorLayer.ActualWidth - cat.Width)));
        Canvas.SetTop(cat, Math.Clamp(y, 0, Math.Max(0, indicatorLayer.ActualHeight - cat.Height)));
    }
    internal void ReleaseImage() { surface.Desktop = null; Content = null; }

    private sealed class OverlaySurface(BitmapSource desktop, Int32Rect desktopBounds, Int32Rect monitor) : FrameworkElement
    {
        internal BitmapSource? Desktop = desktop;
        internal Int32Rect Selection;
        protected override void OnRender(DrawingContext dc)
        {
            if (Desktop == null || ActualWidth <= 0 || ActualHeight <= 0) return;
            double sx = ActualWidth / monitor.Width, sy = ActualHeight / monitor.Height;
            dc.PushClip(new RectangleGeometry(new Rect(RenderSize)));
            dc.DrawImage(Desktop, new Rect((desktopBounds.X - monitor.X) * sx, (desktopBounds.Y - monitor.Y) * sy, desktopBounds.Width * sx, desktopBounds.Height * sy));
            var selection = new Rect((Selection.X - monitor.X) * sx, (Selection.Y - monitor.Y) * sy, Selection.Width * sx, Selection.Height * sy);
            if (Selection.Width > 0 && Selection.Height > 0)
            {
                dc.DrawRectangle(null, new Pen(Brushes.Black, 3), selection);
                dc.DrawRectangle(null, new Pen(Brushes.White, 1), selection);
                var text = new FormattedText($"{Selection.Width} × {Selection.Height} px", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                double x = Math.Clamp(selection.Right - text.Width - 14, 8, Math.Max(8, ActualWidth - text.Width - 20));
                double y = Math.Clamp(selection.Bottom + 8, 8, Math.Max(8, ActualHeight - 34));
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(230, 24, 27, 32)), null, new Rect(x - 6, y - 4, text.Width + 12, text.Height + 8), 5, 5);
                dc.DrawText(text, new Point(x, y));
            }
            dc.Pop();
        }
    }
}
