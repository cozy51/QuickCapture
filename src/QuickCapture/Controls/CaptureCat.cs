using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace QuickCapture.Controls;

// A tiny retained vector mascot: no bitmap assets, timers or idle animation.
internal sealed class CaptureCat : FrameworkElement
{
    private static readonly DrawingGroup Cat = CreateCat();
    internal CaptureCat() { Width = 58; Height = 58; IsHitTestVisible = false; }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawDrawing(Cat);
        dc.DrawRoundedRectangle(Brushes.White, new Pen(Brushes.SlateGray, 0.7), new Rect(0.5, 42, 57, 15), 6, 6);
        dc.DrawText(new FormattedText("Capture", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 10, Brushes.DarkSlateGray, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(10, 42));
    }
    private static DrawingGroup CreateCat()
    {
        var group = new DrawingGroup();
        var fur = new SolidColorBrush(Color.FromRgb(255, 211, 125));
        var outline = new Pen(new SolidColorBrush(Color.FromRgb(83, 61, 51)), 1.4) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        using (var dc = group.Open())
        {
            dc.DrawGeometry(null, new Pen(fur, 6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Geometry.Parse("M 39,34 C 53,37 55,25 49,24"));
            dc.DrawEllipse(fur, outline, new Point(28, 30), 12, 10);
            dc.DrawEllipse(Brushes.Cornsilk, null, new Point(28, 32), 7, 6);
            dc.DrawGeometry(fur, outline, Geometry.Parse("M 12,17 L 12,4 Q 12,2 15,4 L 23,9 Q 28,7 33,9 L 41,4 Q 44,2 44,5 L 43,18 C 46,33 9,33 12,17 Z"));
            dc.DrawGeometry(Brushes.LightCoral, null, Geometry.Parse("M 15,7 L 15,14 L 21,10 Z M 40,7 L 35,10 L 41,14 Z"));
            dc.DrawEllipse(outline.Brush, null, new Point(21, 18), 1.5, 2);
            dc.DrawEllipse(outline.Brush, null, new Point(35, 18), 1.5, 2);
            dc.DrawGeometry(Brushes.IndianRed, null, Geometry.Parse("M 26,21 L 30,21 L 28,23 Z"));
            dc.DrawGeometry(null, new Pen(outline.Brush, 0.9), Geometry.Parse("M 28,23 Q 25,27 23,23 M 28,23 Q 31,27 33,23 M 17,21 L 8,19 M 17,24 L 8,25 M 39,21 L 48,19 M 39,24 L 48,25"));
            dc.DrawEllipse(fur, outline, new Point(21, 38), 5, 2.5);
            dc.DrawEllipse(fur, outline, new Point(35, 38), 5, 2.5);
        }
        group.Freeze(); return group;
    }
}
