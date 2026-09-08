using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickCapture.Models;

namespace QuickCapture.Tests;

internal static class DemoImage
{
    internal static ImageDocument Create()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var ink = new SolidColorBrush(Color.FromRgb(40, 58, 77));
            var muted = new SolidColorBrush(Color.FromRgb(112, 125, 139));
            var line = new Pen(ink, 2);
            void Text(string value, double x, double y, double size = 14, Brush? color = null) => dc.DrawText(new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, color ?? ink, 1), new Point(x, y));
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 820, 510));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(246, 248, 250)), null, new Rect(0, 0, 820, 68));
            Text("BRACKET / A-204", 30, 20, 23); Text("DESIGN REFERENCE   ·   REV 03", 540, 28, 12, muted);
            for (int x = 30; x < 520; x += 20)
                for (int y = 98; y < 410; y += 20) dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(222, 228, 234)), null, new Point(x, y), 0.7, 0.7);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(239, 244, 248)), line, new Rect(100, 165, 330, 185), 18, 18);
            dc.DrawRectangle(Brushes.White, line, new Rect(220, 165, 90, 100));
            foreach (var p in new[] { new Point(140, 205), new Point(390, 205), new Point(140, 310), new Point(390, 310) })
            {
                dc.DrawEllipse(Brushes.White, line, p, 14, 14);
                dc.DrawLine(new Pen(muted, 1), new Point(p.X - 20, p.Y), new Point(p.X + 20, p.Y));
                dc.DrawLine(new Pen(muted, 1), new Point(p.X, p.Y - 20), new Point(p.X, p.Y + 20));
            }
            dc.DrawLine(new Pen(muted, 1), new Point(100, 142), new Point(430, 142));
            dc.DrawLine(new Pen(muted, 1), new Point(100, 130), new Point(100, 158));
            dc.DrawLine(new Pen(muted, 1), new Point(430, 130), new Point(430, 158));
            Text("120 ±0.1", 228, 116, 15);
            Text("4 × Ø8.5 THRU", 195, 377, 16);
            dc.DrawLine(new Pen(muted, 1), new Point(510, 100), new Point(510, 412));
            Text("CHECK POINTS", 545, 110, 13, muted);
            Text("Bolt clearance", 545, 160, 19);
            Text("M8 / 4 positions", 545, 190, 15, muted);
            Text("Edge distance ≥ 12 mm", 545, 244, 17);
            Text("Material   A6061-T6", 545, 294, 16);
            Text("Finish       Anodized", 545, 332, 16);
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(221, 227, 234)), 1), new Point(30, 438), new Point(790, 438));
            Text("QuickCapture", 30, 463, 18); Text("WHEEL  Zoom     H  Highlight     Ctrl+C  Copy", 358, 466, 14, muted);
        }
        var image = new RenderTargetBitmap(820, 510, 96, 96, PixelFormats.Pbgra32); image.Render(visual); image.Freeze();
        var document = new ImageDocument(image);
        document.Add(new HighlighterStroke(new[] { new Point(549, 180), new Point(610, 180), new Point(683, 180) }, Color.FromRgb(255, 227, 56), 23, 0.45));
        document.Add(new HighlighterStroke(new[] { new Point(207, 393), new Point(257, 393), new Point(315, 393) }, Color.FromRgb(81, 217, 255), 21, 0.4));
        return document;
    }
}
