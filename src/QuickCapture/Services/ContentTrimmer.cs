using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickCapture.Services;

/// Finds the part of a capture that stands out from its surroundings: a page, a
/// drawing or a window taken with a generous margin of desktop around it. The
/// margin is whatever colour most of the image's outer band has; only pixels far
/// from that colour count as content, so soft gradients and faint bands in the
/// margin are left out.
public static class ContentTrimmer
{
    /// Colour differences (0–255 on any channel) below this never count as content.
    public const int MinContrast = 48;
    /// Share of the largest difference in the image that content has to reach.
    public const double RelativeContrast = 0.5;
    /// Pixels a column or row needs before it counts, so stray ones are ignored.
    public const int MinPixels = 3;

    /// The rectangle holding the high-contrast content, or null when nothing in
    /// the image stands out from its edge.
    public static Int32Rect? FindContent(BitmapSource image)
    {
        int width = image.PixelWidth, height = image.PixelHeight;
        if (width < 3 || height < 3) return null;
        var bgra = image.Format == PixelFormats.Bgra32 || image.Format == PixelFormats.Bgr32 || image.Format == PixelFormats.Pbgra32
            ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        bgra.CopyPixels(pixels, stride, 0);

        var background = Background(pixels, width, height);
        var distance = new int[width * height];
        int largest = 0;
        for (int i = 0, p = 0; i < distance.Length; i++, p += 4)
        {
            int d = Math.Max(Math.Abs(pixels[p] - background.B), Math.Max(Math.Abs(pixels[p + 1] - background.G), Math.Abs(pixels[p + 2] - background.R)));
            distance[i] = d; largest = Math.Max(largest, d);
        }
        if (largest < MinContrast) return null;
        int threshold = Math.Max(MinContrast, (int)Math.Ceiling(largest * RelativeContrast));

        var columns = new int[width]; var rows = new int[height];
        for (int y = 0; y < height; y++)
            for (int x = 0, i = y * width; x < width; x++, i++)
                if (distance[i] >= threshold) { columns[x]++; rows[y]++; }
        int left = Array.FindIndex(columns, n => n >= MinPixels), right = Array.FindLastIndex(columns, n => n >= MinPixels);
        int top = Array.FindIndex(rows, n => n >= MinPixels), bottom = Array.FindLastIndex(rows, n => n >= MinPixels);
        if (left < 0 || top < 0 || right - left < 1 || bottom - top < 1) return null;
        return new Int32Rect(left, top, right - left + 1, bottom - top + 1);
    }

    /// The median colour of a band a twentieth of the image wide along its edges:
    /// the margin around the content, even where the content reaches one of the
    /// edges or a thin line runs along them.
    private static (int B, int G, int R) Background(byte[] pixels, int width, int height)
    {
        var blue = new int[256]; var green = new int[256]; var red = new int[256];
        int band = Math.Max(1, Math.Min(width, height) / 20), count = 0;
        for (int y = 0; y < height; y++)
        {
            bool edgeRow = y < band || y >= height - band;
            for (int x = 0; x < width; x++)
            {
                if (!edgeRow && x >= band && x < width - band) { x = width - band - 1; continue; }
                int p = (y * width + x) * 4;
                blue[pixels[p]]++; green[pixels[p + 1]]++; red[pixels[p + 2]]++; count++;
            }
        }
        return (Median(blue, count), Median(green, count), Median(red, count));
    }
    private static int Median(int[] histogram, int count)
    {
        for (int value = 0, seen = 0; value < histogram.Length; value++)
            if ((seen += histogram[value]) * 2 >= count) return value;
        return 255;
    }
}
