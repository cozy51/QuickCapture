using System;
using System.Collections.Generic;
using System.Windows;

namespace QuickCapture.Controls;

internal static class StrokeStraightener
{
    // Snap only clear, near-axis gestures. Preserve curves, loops and short marks.
    internal static IReadOnlyList<Point> Snap(IReadOnlyList<Point> points)
    {
        if (points.Count < 2) return points;
        var start = points[0]; var end = points[^1];
        var delta = end - start;
        bool horizontal = Math.Abs(delta.X) >= Math.Abs(delta.Y);
        double major = Math.Abs(horizontal ? delta.X : delta.Y);
        double minor = Math.Abs(horizontal ? delta.Y : delta.X);
        if (major < 12 || minor > major * Math.Tan(Math.PI / 18)) return points; // 10 degrees

        double length = delta.Length;
        double deviationLimit = Math.Clamp(major * 0.06, 2, 12);
        double majorTravel = 0;
        for (int i = 1; i < points.Count; i++)
        {
            var relative = points[i] - start;
            double deviation = Math.Abs(delta.X * relative.Y - delta.Y * relative.X) / length;
            if (deviation > deviationLimit) return points;
            majorTravel += Math.Abs(horizontal ? points[i].X - points[i - 1].X : points[i].Y - points[i - 1].Y);
        }
        if (majorTravel > major * 1.25 + 2) return points;
        return new[] { start, horizontal ? new Point(end.X, start.Y) : new Point(start.X, end.Y) };
    }
}
