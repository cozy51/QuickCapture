using System;
using System.Windows;
using System.Windows.Media;

namespace QuickCapture.Controls;

public sealed class ZoomController
{
    public const double Minimum = 0.05, Maximum = 16;
    public double Zoom { get; private set; } = 1;
    public double DpiScale { get; private set; } = 1;
    public Vector Offset { get; private set; }
    public bool IsFit { get; private set; }
    public double ViewScale => Zoom / DpiScale;
    public Matrix Matrix => new(ViewScale, 0, 0, ViewScale, Offset.X, Offset.Y);
    public Point ToImage(Point viewport) => new((viewport.X - Offset.X) / ViewScale, (viewport.Y - Offset.Y) / ViewScale);
    public Point ToViewport(Point image) => new(image.X * ViewScale + Offset.X, image.Y * ViewScale + Offset.Y);
    public void SetZoom(double zoom, Point anchor)
    {
        var imagePoint = ToImage(anchor);
        Zoom = Math.Clamp(zoom, Minimum, Maximum);
        Offset = (Vector)anchor - (Vector)imagePoint * ViewScale;
        IsFit = false;
    }
    public void Wheel(int delta, Point anchor) => SetZoom(Zoom * Math.Pow(1.15, delta / 120d), anchor);
    public void Pan(Vector delta) { Offset += delta; IsFit = false; }
    public void Fit(Size viewport, Size image)
    {
        if (viewport.Width <= 0 || viewport.Height <= 0 || image.Width <= 0 || image.Height <= 0) return;
        // Fit can go below the wheel limit for extremely large screenshots.
        Zoom = Math.Min(viewport.Width / image.Width, viewport.Height / image.Height) * DpiScale;
        Offset = new Vector((viewport.Width - image.Width * ViewScale) / 2, (viewport.Height - image.Height * ViewScale) / 2);
        IsFit = true;
    }
    public void ActualSize(Size viewport, Size image)
    {
        Zoom = 1;
        Offset = new Vector((viewport.Width - image.Width * ViewScale) / 2, (viewport.Height - image.Height * ViewScale) / 2);
        IsFit = false;
    }
    public void SetDpi(double dpi, Point anchor)
    {
        var imagePoint = ToImage(anchor);
        DpiScale = dpi > 0 ? dpi : 1;
        Offset = (Vector)anchor - (Vector)imagePoint * ViewScale;
    }
}
