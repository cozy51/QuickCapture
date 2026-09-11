using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace QuickCapture.Models;

// Image-pixel coordinates, independent of zoom, window size and monitor DPI.
public interface IAnnotation { void Render(DrawingContext context); }

/// The smoothed path through the sampled points, shared by both pens.
internal static class StrokePath
{
    internal static Geometry Build(IReadOnlyList<Point> points)
    {
        var path = new StreamGeometry();
        using (var ctx = path.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            // Quadratic midpoint interpolation smooths mouse samples without overshoot.
            for (int i = 1; i < points.Count - 1; i++)
            {
                var midpoint = new Point((points[i].X + points[i + 1].X) / 2, (points[i].Y + points[i + 1].Y) / 2);
                ctx.QuadraticBezierTo(points[i], midpoint, true, false);
            }
            if (points.Count > 1) ctx.LineTo(points[^1], true, false);
        }
        path.Freeze(); return path;
    }
}

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
        geometry = StrokePath.Build(points);
    }
    public void Render(DrawingContext context)
    {
        context.PushOpacity(Opacity);
        if (dot) context.DrawEllipse(brush, null, first, Width / 2, Width / 2);
        else context.DrawGeometry(null, pen, geometry);
        context.Pop();
    }
}

/// An opaque line: writing on the picture rather than marking it.
public sealed class PenStroke : IAnnotation
{
    private readonly Geometry geometry;
    private readonly Pen pen;
    private readonly Brush brush;
    private readonly Point first;
    private readonly bool dot;
    public double Width { get; }
    public PenStroke(IReadOnlyList<Point> points, Color color, double width)
    {
        if (points.Count == 0) throw new ArgumentException("A stroke needs a point.", nameof(points));
        Width = width; first = points[0]; dot = points.Count == 1;
        brush = new SolidColorBrush(color); brush.Freeze();
        pen = new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }; pen.Freeze();
        geometry = StrokePath.Build(points);
    }
    public void Render(DrawingContext context)
    {
        if (dot) context.DrawEllipse(brush, null, first, Width / 2, Width / 2);
        else context.DrawGeometry(null, pen, geometry);
    }
}

/// A label written on the picture. It keeps its text so it can be moved or
/// recoloured later, which replaces it with an equal one somewhere else.
public sealed class TextAnnotation : IAnnotation
{
    public const double DefaultSize = 24;
    private static readonly Typeface Face = new(new FontFamily("Yu Gothic UI, Meiryo, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private readonly Geometry glyphs;
    private readonly Brush brush;
    private readonly Pen halo;
    public string Text { get; }
    public Point Origin { get; }
    public Color Color { get; }
    public double FontSize { get; }
    public Size Size { get; }
    public TextAnnotation(string text, Point origin, Color color, double fontSize)
    {
        if (string.IsNullOrEmpty(text)) throw new ArgumentException("A label needs text.", nameof(text));
        Text = text; Origin = origin; Color = color; FontSize = fontSize;
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, fontSize, Brushes.Black, 1);
        Size = new Size(formatted.WidthIncludingTrailingWhitespace, formatted.Height);
        var geometry = formatted.BuildGeometry(origin); geometry.Freeze(); glyphs = geometry;
        brush = new SolidColorBrush(color); brush.Freeze();
        // A white edge keeps the label readable over a busy screenshot.
        halo = new Pen(Brushes.White, Math.Max(1.5, fontSize / 7)) { LineJoin = PenLineJoin.Round }; halo.Freeze();
    }
    public Rect Bounds => new(Origin, Size);
    public bool Contains(Point point) => Bounds.Contains(point);
    public TextAnnotation Moved(Vector delta) => new(Text, Origin + delta, Color, FontSize);
    public TextAnnotation Recoloured(Color color) => new(Text, Origin, color, FontSize);
    public TextAnnotation Resized(double fontSize) => new(Text, Origin, Color, fontSize);
    public void Render(DrawingContext context)
    {
        context.DrawGeometry(null, halo, glyphs);
        context.DrawGeometry(brush, null, glyphs);
    }
}
