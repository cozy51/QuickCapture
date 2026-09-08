using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace QuickCapture.Models;

// Image-pixel coordinates, independent of zoom, window size and monitor DPI.
public interface IAnnotation { void Render(DrawingContext context); }

public sealed class HighlighterStroke : IAnnotation
{
    private readonly Geometry geometry;
    private readonly Pen pen;
    private readonly Brush brush;
    private readonly Point first;
    private readonly bool dot;
    public double Width { get; }
    public double Opacity { get; }
    public HighlighterStroke(IReadOnlyList<Point> points, Color color, double width, double opacity)
    {
        if (points.Count == 0) throw new ArgumentException("A stroke needs a point.", nameof(points));
        Width = width; Opacity = opacity; first = points[0]; dot = points.Count == 1;
        brush = new SolidColorBrush(color); brush.Freeze();
        pen = new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }; pen.Freeze();
        var path = new StreamGeometry();
        using (var ctx = path.Open())
        {
            ctx.BeginFigure(first, false, false);
            // Quadratic midpoint interpolation smooths mouse samples without overshoot.
            for (int i = 1; i < points.Count - 1; i++)
            {
                var midpoint = new Point((points[i].X + points[i + 1].X) / 2, (points[i].Y + points[i + 1].Y) / 2);
                ctx.QuadraticBezierTo(points[i], midpoint, true, false);
            }
            if (points.Count > 1) ctx.LineTo(points[^1], true, false);
        }
        path.Freeze(); geometry = path;
    }
    public void Render(DrawingContext context)
    {
        context.PushOpacity(Opacity);
        if (dot) context.DrawEllipse(brush, null, first, Width / 2, Width / 2);
        else context.DrawGeometry(null, pen, geometry);
        context.Pop();
    }
}
