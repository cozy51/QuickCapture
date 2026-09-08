using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickCapture.Controls;

internal sealed class CaptureFrame : Border
{
    private static readonly Pen Outline = CreateOutline();
    private static readonly Pen TemporaryOutline = CreateTemporaryOutline();
    private static readonly Brush CloseHover = CreateBrush(Color.FromRgb(232, 17, 35));
    private static readonly Brush ClosePressed = CreateBrush(Color.FromRgb(241, 112, 122));
    private static readonly Pen CloseGlyph = CreateGlyph(Color.FromRgb(214, 220, 228));
    private static readonly Pen CloseGlyphActive = CreateGlyph(Colors.White);
    internal const double Thickness = 3;
    internal const double HeaderHeight = CaptureHeader.Height;
    internal const double CloseButtonWidth = CaptureHeader.CloseButtonWidth;
    internal bool AutoClose { get; set; }
    internal int CaptureNumber { get; set; }
    internal int CaptureCount { get; set; }
    internal DateTimeOffset CapturedAt { get; set; }
    internal CaptureHeaderInfo Header => new(CaptureNumber, CaptureCount, CapturedAt);
    internal event Action? CloseRequested;
    private bool closeHover, closePressed;
    internal CaptureFrame()
    {
        BorderThickness = new Thickness(Thickness, HeaderHeight + Thickness, Thickness, Thickness);
        BorderBrush = Brushes.Transparent;
    }
    /// The Windows-style close button in the top-right corner of the header.
    internal Rect CloseButton => new(Math.Max(0, ActualWidth - CloseButtonWidth), 0, Math.Min(ActualWidth, CloseButtonWidth), HeaderHeight);
    internal bool IsOverCloseButton(Point point) => CloseButton.Contains(point);
    // The mouse overrides only translate WPF events into these transitions, so the
    // button reacts identically however the pointer state is driven.
    internal void TrackPointer(Point point) => SetHover(closePressed || IsOverCloseButton(point));
    internal bool BeginClose(Point point)
    {
        if (!IsOverCloseButton(point)) return false;
        closePressed = true; closeHover = true; InvalidateVisual(); return true;
    }
    internal bool CompleteClose(Point point)
    {
        if (!closePressed) return false;
        closePressed = false; closeHover = IsOverCloseButton(point); InvalidateVisual();
        if (!closeHover) return false;
        CloseRequested?.Invoke(); return true;
    }
    internal void CancelPointer()
    {
        if (!closePressed && !closeHover) return;
        closePressed = false; closeHover = false; InvalidateVisual();
    }
    private void SetHover(bool value)
    {
        if (closeHover == value) return;
        closeHover = value; InvalidateVisual();
    }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); TrackPointer(e.GetPosition(this)); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); CancelPointer(); }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!BeginClose(e.GetPosition(this))) return;
        // Capture keeps the release on this frame even when the pointer moves onto
        // the image, and Handled stops the header drag in CaptureWindow.
        CaptureMouse(); e.Handled = true;
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!closePressed) return;
        e.Handled = true;
        var point = e.GetPosition(this);
        if (IsMouseCaptured) ReleaseMouseCapture();
        CompleteClose(point);
    }
    private static Brush CreateBrush(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    private static Pen CreateGlyph(Color color)
    {
        var pen = new Pen(CreateBrush(color), 1.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze(); return pen;
    }
    private static Pen CreateOutline()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(66, 221, 117)), Thickness)
        {
            DashStyle = new DashStyle(new double[] { 3, 2 }, 0),
            DashCap = PenLineCap.Flat,
            LineJoin = PenLineJoin.Miter
        };
        pen.Freeze(); return pen;
    }
    private static Pen CreateTemporaryOutline()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(255, 164, 62)), Thickness)
        {
            DashStyle = new DashStyle(new double[] { 0, 2.2 }, 0),
            DashCap = PenLineCap.Round
        };
        pen.Freeze(); return pen;
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        CaptureHeader.Render(dc, ActualWidth, dpi, Header, CloseButtonWidth, AutoClose ? "3秒で閉じる" : null, TemporaryOutline.Brush);
        RenderCloseButton(dc);
        double inset = Thickness / 2;
        dc.DrawRectangle(null, AutoClose ? TemporaryOutline : Outline, new Rect(inset, HeaderHeight + inset, Math.Max(0, ActualWidth - Thickness), Math.Max(0, ActualHeight - HeaderHeight - Thickness)));
    }
    private void RenderCloseButton(DrawingContext dc)
    {
        var bounds = CloseButton;
        if (bounds.Width < 16) return;
        if (closePressed || closeHover) dc.DrawRectangle(closePressed ? ClosePressed : CloseHover, null, bounds);
        var glyph = closePressed || closeHover ? CloseGlyphActive : CloseGlyph;
        double x = bounds.X + bounds.Width / 2, y = bounds.Y + bounds.Height / 2, arm = 5;
        dc.DrawLine(glyph, new Point(x - arm, y - arm), new Point(x + arm, y + arm));
        dc.DrawLine(glyph, new Point(x + arm, y - arm), new Point(x - arm, y + arm));
    }
}
