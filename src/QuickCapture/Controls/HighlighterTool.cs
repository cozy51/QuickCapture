using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using QuickCapture.Models;

namespace QuickCapture.Controls;

public sealed class HighlighterTool
{
    public static readonly (string Name, string Hex)[] Palette =
    {
        ("黄色", "#FFE338"), ("赤", "#FF5555"), ("緑", "#68E675"), ("水色", "#51D9FF"), ("ピンク", "#FF83C9")
    };
    private readonly List<Point> points = new();
    private HighlighterStroke? preview;
    public Color Color { get; set; } = (Color)ColorConverter.ConvertFromString(AppSettings.DefaultHighlighterColor);
    public double Width { get; set; } = 20;
    public double Opacity { get; set; } = 0.45;
    public bool IsDrawing => points.Count > 0;
    public void Begin(Point point) { points.Clear(); points.Add(point); preview = null; }
    public void Add(Point point, double minimumDistance)
    {
        if (points.Count == 0 || (points[^1] - point).Length < minimumDistance) return;
        points.Add(point); preview = null;
    }
    public HighlighterStroke? Preview => !IsDrawing ? null : preview ??= new HighlighterStroke(points, Color, Width, Opacity);
    public HighlighterStroke? Finish()
    {
        if (!IsDrawing) return null;
        var corrected = StrokeStraightener.Snap(points);
        var stroke = ReferenceEquals(corrected, points) ? Preview : new HighlighterStroke(corrected, Color, Width, Opacity);
        Cancel(); return stroke;
    }
    public void Cancel() { points.Clear(); preview = null; }
    public void ChangeWidth(int direction) { if (!IsDrawing) Width = Math.Clamp(Width + direction * 2, 2, 200); }
}
