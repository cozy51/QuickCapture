using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickCapture.Services;

/// Finds the part of a capture that stands out from its surroundings: a page, a
/// drawing or a window taken with a generous margin of desktop around it. The
/// outline of the object is searched by its edges in Lab colour (see
/// ContentTrimmer.Outline.cs), which also finds a book whose colour is close to the
/// desk. Where no outline is found the margin is whatever colour most of the
/// image's outer band has, and only pixels far from that colour count as content,
/// so soft gradients and faint bands in the margin are left out.
public static partial class ContentTrimmer
{
    /// Colour differences (0–255 on any channel) below this never count as content.
    public const int MinContrast = 48;
    /// Share of the largest difference in the image that content has to reach.
    public const double RelativeContrast = 0.5;
    /// Pixels a column or row needs before it counts, so stray ones are ignored.
    public const int MinPixels = 3;

    /// What the trim found: the rectangle to cut, the four corners of the object
    /// (leaning ones are straightened with a perspective transform) and how sure it is.
    public sealed record Detection(Int32Rect Bounds, Point[] Corners, double Confidence, bool NeedsWarp, string Method)
    {
        public bool Reliable => Confidence >= MinConfidence;
    }

    /// The object in the image with its confidence, or null when nothing in the
    /// image stands out from its edge at all.
    public static Detection? Detect(BitmapSource image)
    {
        int width = image.PixelWidth, height = image.PixelHeight;
        if (width < 3 || height < 3) return null;
        var pixels = Pixels(image, out _);
        if (DetectOutline(pixels, width, height) is not Outline outline) return null;
        var box = BoundsOf(outline.Corners, width, height);
        return new Detection(new Int32Rect(box.Left, box.Top, box.Width, box.Height),
            outline.Corners.Select(c => new Point(c.X, c.Y)).ToArray(), outline.Confidence,
            NeedsWarp(outline.Corners, width, height), outline.Method);
    }

    /// The rectangle holding the content, or null when nothing in the image stands
    /// out from its edge or the outline found is not reliable enough to cut to.
    public static Int32Rect? FindContent(BitmapSource image) =>
        Detect(image) is { Reliable: true } detection ? detection.Bounds : null;

    /// The object straightened onto an upright rectangle by a perspective transform.
    public static BitmapSource Rectify(BitmapSource image, Detection detection)
    {
        int width = image.PixelWidth, height = image.PixelHeight;
        var corners = detection.Corners.Select(p => new Corner(p.X, p.Y)).ToArray();
        var result = WarpBgra(Pixels(image, out var format), width, height, corners, out int outWidth, out int outHeight);
        var bitmap = BitmapSource.Create(outWidth, outHeight, 96, 96, format, null, result, outWidth * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// The affine map closest to the perspective transform of Rectify, for marks
    /// drawn on the picture before it was straightened.
    public static Matrix RectifyTransform(Detection detection, int outWidth, int outHeight)
    {
        var from = detection.Corners;
        var to = new[] { new Point(0, 0), new Point(outWidth, 0), new Point(outWidth, outHeight), new Point(0, outHeight) };
        // Least squares over the four corners: x' = m11 x + m21 y + ox, y' = m12 x + m22 y + oy.
        var normal = new double[3, 3]; var rx = new double[3]; var ry = new double[3];
        for (int i = 0; i < 4; i++)
        {
            double[] row = { from[i].X, from[i].Y, 1 };
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++) normal[r, c] += row[r] * row[c];
                rx[r] += row[r] * to[i].X; ry[r] += row[r] * to[i].Y;
            }
        }
        var sx = Solve(normal, rx); var sy = Solve(normal, ry);
        return new Matrix(sx[0], sy[0], sx[1], sy[1], sx[2], sy[2]);

        static double[] Solve(double[,] m, double[] v)
        {
            double Det(double[,] a) => a[0, 0] * (a[1, 1] * a[2, 2] - a[1, 2] * a[2, 1]) - a[0, 1] * (a[1, 0] * a[2, 2] - a[1, 2] * a[2, 0]) + a[0, 2] * (a[1, 0] * a[2, 1] - a[1, 1] * a[2, 0]);
            double d = Det(m); var result = new double[3];
            for (int k = 0; k < 3; k++)
            {
                var a = (double[,])m.Clone();
                for (int r = 0; r < 3; r++) a[r, k] = v[r];
                result[k] = Det(a) / d;
            }
            return result;
        }
    }

    /// The image as four bytes a pixel, blue first, in the format they came in.
    private static byte[] Pixels(BitmapSource image, out PixelFormat format)
    {
        int width = image.PixelWidth, height = image.PixelHeight;
        var bgra = image.Format == PixelFormats.Bgra32 || image.Format == PixelFormats.Bgr32 || image.Format == PixelFormats.Pbgra32
            ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        format = bgra.Format;
        int stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        bgra.CopyPixels(pixels, stride, 0);
        return pixels;
    }
}
