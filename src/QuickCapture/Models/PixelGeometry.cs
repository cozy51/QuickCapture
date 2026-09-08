using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace QuickCapture.Models;

public static class PixelGeometry
{
    public static Int32Rect Union(IReadOnlyList<Int32Rect> rectangles)
    {
        int x = rectangles.Min(r => r.X), y = rectangles.Min(r => r.Y);
        return new(x, y, rectangles.Max(r => r.X + r.Width) - x, rectangles.Max(r => r.Y + r.Height) - y);
    }
    public static Int32Rect Selection(Point a, Point b, Int32Rect bounds)
    {
        int left = Math.Clamp((int)Math.Min(a.X, b.X), bounds.X, bounds.X + bounds.Width);
        int top = Math.Clamp((int)Math.Min(a.Y, b.Y), bounds.Y, bounds.Y + bounds.Height);
        int right = Math.Clamp((int)Math.Max(a.X, b.X), bounds.X, bounds.X + bounds.Width);
        int bottom = Math.Clamp((int)Math.Max(a.Y, b.Y), bounds.Y, bounds.Y + bounds.Height);
        return new(left, top, right - left, bottom - top);
    }
}
