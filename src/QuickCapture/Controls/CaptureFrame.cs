using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QuickCapture.Controls;

internal sealed class CaptureFrame : Border
{
    private static readonly Pen Outline = CreateOutline();
    private static readonly Pen TemporaryOutline = CreateTemporaryOutline();
    internal const double Thickness = 3;
    internal const double HeaderHeight = 34;
    internal bool AutoClose { get; set; }
    internal int CaptureNumber { get; set; }
    internal int CaptureCount { get; set; }
    internal DateTimeOffset CapturedAt { get; set; }
    internal CaptureFrame()
    {
        BorderThickness = new Thickness(Thickness, HeaderHeight + Thickness, Thickness, Thickness);
        BorderBrush = Brushes.Transparent;
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
        var number = new FormattedText(CaptureNumber.ToString("00"), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 24, new SolidColorBrush(Color.FromRgb(120, 240, 171)), dpi);
        var total = new FormattedText($"/ {CaptureCount:00}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Brushes.Silver, dpi);
        var time = new FormattedText(CapturedAt.ToLocalTime().ToString("HH:mm:ss"), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Consolas"), 22, Brushes.Gainsboro, dpi);
        // Keep the time centered even in a narrow capture; reduce it only when
        // necessary to leave a clear gap beside the sequence number.
        double available = Math.Max(16, ActualWidth - 2 * (number.Width + 19));
        if (time.Width > available) time.SetFontSize(22 * available / time.Width);
        double timeX = (ActualWidth - time.Width) / 2;
        dc.DrawText(number, new Point(11, (HeaderHeight - number.Height) / 2));
        if (15 + number.Width + total.Width < timeX - 8)
            dc.DrawText(total, new Point(15 + number.Width, (HeaderHeight - total.Height) / 2));
        dc.DrawText(time, new Point(timeX, (HeaderHeight - time.Height) / 2));
        if (AutoClose)
        {
            var badge = new FormattedText("3秒で閉じる", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, TemporaryOutline.Brush, dpi);
            if (ActualWidth - badge.Width - 12 > timeX + time.Width + 10)
                dc.DrawText(badge, new Point(ActualWidth - badge.Width - 12, (HeaderHeight - badge.Height) / 2));
        }
        double inset = Thickness / 2;
        dc.DrawRectangle(null, AutoClose ? TemporaryOutline : Outline, new Rect(inset, HeaderHeight + inset, Math.Max(0, ActualWidth - Thickness), Math.Max(0, ActualHeight - HeaderHeight - Thickness)));
    }
}
