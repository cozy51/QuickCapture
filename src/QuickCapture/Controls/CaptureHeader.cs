using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace QuickCapture.Controls;

/// <summary>Sequence number, capture date and capture time shown above an image.</summary>
public readonly record struct CaptureHeaderInfo(int Number, int Count, DateTimeOffset CapturedAt);

/// The dark band above the image. The window frame and the "include the header"
/// export share this drawing so a recorded image matches what the screen shows.
internal static class CaptureHeader
{
    internal const double Height = 34;
    internal const double CloseButtonWidth = 46;
    /// Symmetric text margin used when nothing occupies the right corner.
    internal const double Margin = 11;
    internal static readonly Brush Background = Frozen(Color.FromRgb(24, 27, 32));
    private static readonly Brush NumberBrush = Frozen(Color.FromRgb(120, 240, 171));
    private static readonly Brush DateBrush = Frozen(Color.FromRgb(176, 184, 196));
    private static readonly Typeface NumberFace = new("Segoe UI Semibold");
    private static readonly Typeface LabelFace = new("Segoe UI");
    private static readonly Typeface TimeFace = new("Consolas");
    private static Brush Frozen(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    /// <param name="reservedRight">Right corner kept free for the close button.</param>
    internal static void Render(DrawingContext dc, double width, double pixelsPerDip, CaptureHeaderInfo info, double reservedRight, string? badge = null, Brush? badgeBrush = null)
    {
        var local = info.CapturedAt.ToLocalTime();
        var number = Text(info.Number.ToString("00", CultureInfo.InvariantCulture), NumberFace, 24, NumberBrush, pixelsPerDip);
        var total = Text(string.Format(CultureInfo.InvariantCulture, "/ {0:00}", info.Count), LabelFace, 12, Brushes.Silver, pixelsPerDip);
        var time = Text(local.ToString("HH:mm:ss", CultureInfo.InvariantCulture), TimeFace, 22, Brushes.Gainsboro, pixelsPerDip);
        var date = Text(local.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture), LabelFace, 13, DateBrush, pixelsPerDip);
        // Keep the time centered even in a narrow capture; reduce it only when
        // necessary to leave a clear gap beside the number and the close button.
        double available = Math.Max(16, width - 2 * Math.Max(number.Width + 19, reservedRight + Margin));
        if (time.Width > available) time.SetFontSize(22 * available / time.Width);
        double timeX = (width - time.Width) / 2, timeEnd = timeX + time.Width + 10;
        dc.DrawText(number, new Point(Margin, (Height - number.Height) / 2));
        if (15 + number.Width + total.Width < timeX - 8)
            dc.DrawText(total, new Point(15 + number.Width, (Height - total.Height) / 2));
        dc.DrawText(time, new Point(timeX, (Height - time.Height) / 2));
        // Date first, then the auto-close badge beside it, both filling the right
        // side inwards and dropping out entirely rather than overlapping the time.
        double right = width - reservedRight - Margin;
        if (right - date.Width > timeEnd)
        {
            dc.DrawText(date, new Point(right - date.Width, (Height - date.Height) / 2));
            right -= date.Width + 12;
        }
        if (badge == null) return;
        var mark = new FormattedText(badge, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 11, badgeBrush ?? Brushes.Gainsboro, pixelsPerDip);
        if (right - mark.Width > timeEnd) dc.DrawText(mark, new Point(right - mark.Width, (Height - mark.Height) / 2));
    }
    private static FormattedText Text(string value, Typeface face, double size, Brush brush, double pixelsPerDip) =>
        new(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush, pixelsPerDip);
}
