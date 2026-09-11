using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using QuickCapture.Models;

namespace QuickCapture.Controls;

/// Collects the pointer samples of one stroke. The same tool serves the
/// highlighter and the plain pen; only the mark they leave differs.
public sealed class HighlighterTool
{
    public static readonly (string Name, string Hex)[] Palette = DrawingPalette.Colors;
    private readonly List<Point> points = new();
    private IAnnotation? preview;
    /// A plain pen writes an opaque line instead of a wash of colour.
    public bool Opaque { get; init; }
    public Color Color { get; set; } = DrawingPalette.Parse(AppSettings.DefaultHighlighterColor);
    public double Width { get; set; } = 20;
    public double Opacity { get; set; } = 0.45;
    public bool IsDrawing => points.Count > 0;
    public void Begin(Point point) { points.Clear(); points.Add(point); preview = null; }
    public void Add(Point point, double minimumDistance)
    {
        if (points.Count == 0 || (points[^1] - point).Length < minimumDistance) return;
        points.Add(point); preview = null;
    }
    public IAnnotation? Preview => !IsDrawing ? null : preview ??= Mark(points);
    public IAnnotation? Finish()
    {
        if (!IsDrawing) return null;
        var corrected = StrokeStraightener.Snap(points);
        var stroke = ReferenceEquals(corrected, points) ? Preview : Mark(corrected);
        Cancel(); return stroke;
    }
    public void Cancel() { points.Clear(); preview = null; }
    public void ChangeWidth(int direction)
    {
        if (IsDrawing) return;
        double step = Opaque ? 1 : 2;
        Width = Math.Clamp(Width + direction * step, step, Opaque ? 100 : 200);
    }
    private IAnnotation Mark(IReadOnlyList<Point> samples) =>
        Opaque ? new PenStroke(samples, Color, Width) : new HighlighterStroke(samples, Color, Width, Opacity);
}
