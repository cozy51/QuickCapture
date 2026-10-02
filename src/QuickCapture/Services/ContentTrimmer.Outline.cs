using System;
using System.Collections.Generic;
using System.Linq;

namespace QuickCapture.Services;

/// The pixel work behind the trim, kept free of WPF types: finds the outline of the
/// page, book or card in a capture as four corners with a confidence score.
///
/// Two stages. The normal stage looks for edges in Lab colour on the image as it
/// is. Only when that gives no outline it can trust does a second stage raise the
/// local contrast (CLAHE on L, stronger chroma) and lower the edge thresholds, for
/// a green book on a grey desk and the like. Each stage collects four-sided
/// candidates from Canny edges closed with a morphological close (contours reduced
/// with approxPolyDP), from the region that differs in colour from the margin, and
/// from pairs of long Hough lines, then scores them: large, near the centre, sides
/// that run along real edges, corners near 90 degrees and a sensible share of the
/// image.
public static partial class ContentTrimmer
{
    /// A corner of the outline in image pixels, measured on pixel boundaries
    /// (0 is the left edge of the first pixel, the width the right edge of the last).
    public readonly record struct Corner(double X, double Y);
    /// The four corners in the order top-left, top-right, bottom-right, bottom-left.
    public sealed record Outline(Corner[] Corners, double Confidence, string Method);
    /// Left, top, right and bottom as pixel boundaries (exclusive right and bottom).
    internal readonly record struct Box(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    /// An outline at or above this score is taken from the normal stage as it is.
    public const double AcceptConfidence = 0.7;
    /// Below this the outline is not trusted at all and the trim is left to the user.
    public const double MinConfidence = 0.5;
    /// Lab colour difference outside the high-contrast part that shows a larger object around it.
    private const double ModerateDifference = 18;
    /// Longest side of the reduced image the search runs on.
    private const int WorkSize = 480;

    private sealed record Stage(string Name, bool Clahe, double ClipLimit, double ChromaGain, double CannyFloor,
        double CannyPercentile, double CannyFactor, int Close, double MinLineShare, double MaskFloor);
    private static readonly Stage Normal = new("通常", false, 0, 1.0, 3.0, 0.90, 1.0, 1, 0.30, 10);
    private static readonly Stage Enhanced = new("低コントラスト強化", true, 3.0, 2.0, 0.8, 0.80, 0.6, 2, 0.18, 4);

    /// The outline of the object in a BGRA image, with its confidence; null when the
    /// image has nothing that stands out at all.
    internal static Outline? DetectOutline(byte[] pixels, int width, int height)
    {
        if (width < 3 || height < 3) return null;
        var background = Background(pixels, width, height);
        var content = ThresholdBounds(pixels, width, height, background);
        var work = WorkImage.From(pixels, width, height);
        Box? workContent = content is Box c ? new Box(c.Left / work.Scale, c.Top / work.Scale,
            Math.Min(work.Width, (c.Right + work.Scale - 1) / work.Scale), Math.Min(work.Height, (c.Bottom + work.Scale - 1) / work.Scale)) : null;

        // Stage one; the enhanced stage runs only when it is not sure enough.
        var best = BestQuad(work, Normal, workContent);
        if (best == null || best.Value.Score < AcceptConfidence)
        {
            var enhanced = BestQuad(work, Enhanced, workContent);
            if (enhanced != null && (best == null || enhanced.Value.Score > best.Value.Score)) best = enhanced;
        }

        double clean = workContent is Box workBox ? Cleanliness(work, background, workBox) : 0;
        Outline? quad = null;
        if (best is { } found && found.Score >= MinConfidence)
        {
            var corners = found.Corners.Select(p => new Corner((p.X + 0.5) * work.Scale, (p.Y + 0.5) * work.Scale)).ToArray();
            corners = Refine(pixels, width, height, corners, work.Scale);
            quad = new Outline(corners, found.Score, found.Stage);
        }
        if (content is not Box contentBox)
            return quad;
        var threshold = new Outline(BoxCorners(contentBox), clean, "しきい値");
        if (quad == null) return threshold;
        // The quad and the old high-contrast box describe the same rectangle: keep the
        // box, which is exact to the pixel.
        int slack = Math.Max(2, work.Scale + 1);
        var bounds = BoundsOf(quad.Corners, width, height);
        if (!NeedsWarp(quad.Corners, width, height)
            && Math.Abs(bounds.Left - contentBox.Left) <= slack && Math.Abs(bounds.Top - contentBox.Top) <= slack
            && Math.Abs(bounds.Right - contentBox.Right) <= slack && Math.Abs(bounds.Bottom - contentBox.Bottom) <= slack)
            return threshold with { Confidence = Math.Max(clean, quad.Confidence), Method = quad.Method };
        // A plain margin around a box the quad does not enclose: the box is the content.
        if (clean >= AcceptConfidence && quad.Confidence < AcceptConfidence && Coverage(quad.Corners, contentBox) < 0.9)
            return threshold;
        return quad;
    }

    /// The rectangle the outline is cut to when it needs no perspective correction.
    internal static Box BoundsOf(Corner[] c, int width, int height)
    {
        int left = Math.Clamp((int)Math.Round((c[0].X + c[3].X) / 2), 0, width - 1);
        int right = Math.Clamp((int)Math.Round((c[1].X + c[2].X) / 2), left + 1, width);
        int top = Math.Clamp((int)Math.Round((c[0].Y + c[1].Y) / 2), 0, height - 1);
        int bottom = Math.Clamp((int)Math.Round((c[3].Y + c[2].Y) / 2), top + 1, height);
        return new Box(left, top, right, bottom);
    }
    /// Whether the outline leans or narrows enough that a plain crop would show it skewed.
    internal static bool NeedsWarp(Corner[] c, int width, int height)
    {
        var b = BoundsOf(c, width, height);
        double tolerance = Math.Max(2, 0.01 * Math.Min(b.Width, b.Height));
        var square = BoxCorners(b);
        for (int i = 0; i < 4; i++)
            if (Math.Abs(c[i].X - square[i].X) > tolerance || Math.Abs(c[i].Y - square[i].Y) > tolerance) return true;
        return false;
    }
    private static Corner[] BoxCorners(Box b) =>
        new[] { new Corner(b.Left, b.Top), new Corner(b.Right, b.Top), new Corner(b.Right, b.Bottom), new Corner(b.Left, b.Bottom) };

    // ---------------------------------------------------------------- the old trim

    /// The rectangle around the pixels that differ strongly from the margin colour.
    internal static Box? ThresholdBounds(byte[] pixels, int width, int height, (int B, int G, int R) background)
    {
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
        return new Box(left, top, right + 1, bottom + 1);
    }

    /// The median colour of a band a twentieth of the image wide along its edges:
    /// the margin around the content, even where the content reaches one of the
    /// edges or a thin line runs along them.
    internal static (int B, int G, int R) Background(byte[] pixels, int width, int height)
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

    /// 1 when the margin around the high-contrast box is plain, falling to 0 as more
    /// of it differs clearly from the margin colour: then the box is only part of a
    /// larger object, such as the title on a book cover.
    private static double Cleanliness(WorkImage work, (int B, int G, int R) background, Box content)
    {
        var bg = Lab(background.R, background.G, background.B);
        int outside = 0, different = 0;
        for (int y = 0; y < work.Height; y++)
            for (int x = 0; x < work.Width; x++)
            {
                if (x >= content.Left - 2 && x < content.Right + 2 && y >= content.Top - 2 && y < content.Bottom + 2) continue;
                int i = y * work.Width + x; outside++;
                double dl = work.L[i] - bg.L, da = work.A[i] - bg.A, db = work.B[i] - bg.B;
                if (dl * dl + da * da + db * db >= ModerateDifference * ModerateDifference) different++;
            }
        if (outside < work.Width * work.Height / 50) return 1;
        return 1 - Math.Min(1, different / (0.03 * outside));
    }

    // ---------------------------------------------------------------- colour

    private static readonly double[] Linear = Enumerable.Range(0, 256).Select(v =>
    {
        double c = v / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }).ToArray();
    private static (double L, double A, double B) Lab(double r, double g, double b)
    {
        double R = LinearOf(r), G = LinearOf(g), B = LinearOf(b);
        double x = (0.4124564 * R + 0.3575761 * G + 0.1804375 * B) / 0.95047;
        double y = 0.2126729 * R + 0.7151522 * G + 0.0721750 * B;
        double z = (0.0193339 * R + 0.1191920 * G + 0.9503041 * B) / 1.08883;
        double fx = F(x), fy = F(y), fz = F(z);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116;
    }
    private static double LinearOf(double v)
    {
        int i = Math.Clamp((int)v, 0, 255);
        if (i == 255 || v == i) return Linear[i];
        return Linear[i] + (Linear[i + 1] - Linear[i]) * (v - i);
    }
    private static double DeltaE(byte[] pixels, int width, int height, int x, int y, int x2, int y2)
    {
        x = Math.Clamp(x, 0, width - 1); y = Math.Clamp(y, 0, height - 1);
        x2 = Math.Clamp(x2, 0, width - 1); y2 = Math.Clamp(y2, 0, height - 1);
        int p = (y * width + x) * 4, q = (y2 * width + x2) * 4;
        var a = Lab(pixels[p + 2], pixels[p + 1], pixels[p]); var b = Lab(pixels[q + 2], pixels[q + 1], pixels[q]);
        return Math.Sqrt((a.L - b.L) * (a.L - b.L) + (a.A - b.A) * (a.A - b.A) + (a.B - b.B) * (a.B - b.B));
    }

    /// The capture reduced by a whole factor to at most WorkSize on its longer side, in Lab.
    private sealed class WorkImage
    {
        public int Width, Height, Scale;
        public float[] L = Array.Empty<float>(), A = Array.Empty<float>(), B = Array.Empty<float>();
        public static WorkImage From(byte[] pixels, int width, int height)
        {
            int scale = Math.Max(1, (Math.Max(width, height) + WorkSize - 1) / WorkSize);
            int w = Math.Max(1, width / scale), h = Math.Max(1, height / scale);
            var work = new WorkImage { Width = w, Height = h, Scale = scale, L = new float[w * h], A = new float[w * h], B = new float[w * h] };
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double r = 0, g = 0, b = 0;
                    for (int dy = 0; dy < scale; dy++)
                        for (int dx = 0, p = ((y * scale + dy) * width + x * scale) * 4; dx < scale; dx++, p += 4)
                        { b += pixels[p]; g += pixels[p + 1]; r += pixels[p + 2]; }
                    double n = scale * scale;
                    var lab = Lab(r / n, g / n, b / n);
                    int i = y * w + x;
                    work.L[i] = (float)lab.L; work.A[i] = (float)lab.A; work.B[i] = (float)lab.B;
                }
            return work;
        }
    }

    /// Contrast Limited Adaptive Histogram Equalization of L (0–100) on an 8 × 8 grid of tiles.
    private static float[] Clahe(float[] l, int width, int height, double clipLimit)
    {
        const int Bins = 256, Grid = 8;
        int tilesX = Math.Min(Grid, Math.Max(1, width / 8)), tilesY = Math.Min(Grid, Math.Max(1, height / 8));
        double tileW = (double)width / tilesX, tileH = (double)height / tilesY;
        var maps = new float[tilesX * tilesY][];
        for (int ty = 0; ty < tilesY; ty++)
            for (int tx = 0; tx < tilesX; tx++)
            {
                int x0 = (int)(tx * tileW), x1 = (int)((tx + 1) * tileW), y0 = (int)(ty * tileH), y1 = (int)((ty + 1) * tileH);
                var histogram = new double[Bins]; int count = 0;
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++) { histogram[Bin(l[y * width + x])]++; count++; }
                double limit = Math.Max(1, clipLimit * count / Bins), excess = 0;
                for (int i = 0; i < Bins; i++) if (histogram[i] > limit) { excess += histogram[i] - limit; histogram[i] = limit; }
                var map = new float[Bins]; double sum = 0;
                for (int i = 0; i < Bins; i++) { sum += histogram[i] + excess / Bins; map[i] = (float)(100 * sum / Math.Max(1, count)); }
                maps[ty * tilesX + tx] = map;
            }
        var result = new float[l.Length];
        for (int y = 0; y < height; y++)
        {
            double fy = (y + 0.5) / tileH - 0.5; int ty0 = Math.Clamp((int)Math.Floor(fy), 0, tilesY - 1), ty1 = Math.Min(tilesY - 1, ty0 + 1);
            double wy = Math.Clamp(fy - ty0, 0, 1);
            for (int x = 0; x < width; x++)
            {
                double fx = (x + 0.5) / tileW - 0.5; int tx0 = Math.Clamp((int)Math.Floor(fx), 0, tilesX - 1), tx1 = Math.Min(tilesX - 1, tx0 + 1);
                double wx = Math.Clamp(fx - tx0, 0, 1);
                int bin = Bin(l[y * width + x]);
                double top = maps[ty0 * tilesX + tx0][bin] * (1 - wx) + maps[ty0 * tilesX + tx1][bin] * wx;
                double bottom = maps[ty1 * tilesX + tx0][bin] * (1 - wx) + maps[ty1 * tilesX + tx1][bin] * wx;
                result[y * width + x] = (float)(top * (1 - wy) + bottom * wy);
            }
        }
        return result;
        static int Bin(float v) => Math.Clamp((int)(v * 2.55f), 0, 255);
    }

    private static float[] Blur(float[] source, int width, int height)
    {
        float[] kernel = { 0.0545f, 0.2442f, 0.4026f, 0.2442f, 0.0545f }; // Gaussian, sigma 1
        var temp = new float[source.Length]; var result = new float[source.Length];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float sum = 0;
                for (int k = -2; k <= 2; k++) sum += kernel[k + 2] * source[y * width + Math.Clamp(x + k, 0, width - 1)];
                temp[y * width + x] = sum;
            }
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float sum = 0;
                for (int k = -2; k <= 2; k++) sum += kernel[k + 2] * temp[Math.Clamp(y + k, 0, height - 1) * width + x];
                result[y * width + x] = sum;
            }
        return result;
    }

    // ---------------------------------------------------------------- edges

    /// Canny on the Lab colour gradient: the strongest channel gives the direction,
    /// all three together the strength.
    private static bool[] Canny(float[][] channels, int width, int height, Stage stage)
    {
        int n = width * height;
        var magnitude = new float[n]; var gxBest = new float[n]; var gyBest = new float[n];
        foreach (var c in channels)
            for (int y = 1; y < height - 1; y++)
                for (int x = 1; x < width - 1; x++)
                {
                    int i = y * width + x;
                    float gx = (c[i - width + 1] + 2 * c[i + 1] + c[i + width + 1] - c[i - width - 1] - 2 * c[i - 1] - c[i + width - 1]) / 8;
                    float gy = (c[i + width - 1] + 2 * c[i + width] + c[i + width + 1] - c[i - width - 1] - 2 * c[i - width] - c[i - width + 1]) / 8;
                    float m = gx * gx + gy * gy;
                    if (m > gxBest[i] * gxBest[i] + gyBest[i] * gyBest[i]) { gxBest[i] = gx; gyBest[i] = gy; }
                    magnitude[i] += m;
                }
        for (int i = 0; i < n; i++) magnitude[i] = MathF.Sqrt(magnitude[i]);

        var sorted = (float[])magnitude.Clone(); Array.Sort(sorted);
        double high = Math.Max(stage.CannyFloor, sorted[(int)(stage.CannyPercentile * (n - 1))] * stage.CannyFactor), low = high * 0.4;

        // Thin to the ridge along the gradient direction.
        var thin = new float[n];
        for (int y = 1; y < height - 1; y++)
            for (int x = 1; x < width - 1; x++)
            {
                int i = y * width + x; float m = magnitude[i];
                if (m < low) continue;
                double angle = Math.Atan2(gyBest[i], gxBest[i]) * 180 / Math.PI; if (angle < 0) angle += 180;
                int step = angle < 22.5 || angle >= 157.5 ? 1 : angle < 67.5 ? width + 1 : angle < 112.5 ? width : width - 1;
                if (m >= magnitude[i - step] && m >= magnitude[i + step]) thin[i] = m;
            }
        // Hysteresis: weak edge pixels are kept only when joined to strong ones.
        var edges = new bool[n]; var stack = new Stack<int>();
        for (int i = 0; i < n; i++)
            if (thin[i] >= high && !edges[i]) { edges[i] = true; stack.Push(i); }
        while (stack.Count > 0)
        {
            int i = stack.Pop(), x = i % width, y = i / width;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    int j = ny * width + nx;
                    if (!edges[j] && thin[j] >= low) { edges[j] = true; stack.Push(j); }
                }
        }
        return edges;
    }

    private static bool[] Dilate(bool[] source, int width, int height, int radius)
    {
        var result = source;
        for (int r = 0; r < radius; r++) result = Step(result);
        return result;
        bool[] Step(bool[] s)
        {
            var o = new bool[s.Length];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    bool any = false;
                    for (int dy = -1; dy <= 1 && !any; dy++)
                        for (int dx = -1; dx <= 1 && !any; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            any = nx >= 0 && ny >= 0 && nx < width && ny < height && s[ny * width + nx];
                        }
                    o[y * width + x] = any;
                }
            return o;
        }
    }
    private static bool[] Erode(bool[] source, int width, int height, int radius)
    {
        var inverted = source.Select(v => !v).ToArray();
        return Dilate(inverted, width, height, radius).Select(v => !v).ToArray();
    }
    /// Morphological close: joins edge pieces broken by a gap of a pixel or two.
    private static bool[] Close(bool[] source, int width, int height, int radius) =>
        Erode(Dilate(source, width, height, radius), width, height, radius);

    // ---------------------------------------------------------------- candidates

    private readonly record struct Point2(double X, double Y);
    private readonly record struct Scored(Point2[] Corners, double Score, string Stage);

    private static Scored? BestQuad(WorkImage work, Stage stage, Box? content)
    {
        int w = work.Width, h = work.Height;
        if (w < 8 || h < 8) return null;
        var l = stage.Clahe ? Clahe(work.L, w, h, stage.ClipLimit) : work.L;
        var a = work.A.Select(v => (float)(v * stage.ChromaGain)).ToArray();
        var b = work.B.Select(v => (float)(v * stage.ChromaGain)).ToArray();
        var channels = new[] { Blur(l, w, h), Blur(a, w, h), Blur(b, w, h) };

        var edges = Canny(channels, w, h, stage);
        var support = Dilate(edges, w, h, 1);
        var candidates = new List<Point2[]>();
        candidates.AddRange(ContourQuads(Close(edges, w, h, stage.Close), w, h));
        candidates.AddRange(ContourQuads(ColourMask(channels, w, h, stage), w, h));
        candidates.AddRange(HoughQuads(edges, w, h, stage));

        // Lines through a busy or noisy image touch edges by chance this often.
        double chance = support.Count(v => v) / (double)support.Length;
        var scored = new List<Scored>();
        foreach (var quad in candidates)
        {
            double score = Score(quad, support, chance, channels, w, h, content);
            if (score > 0) scored.Add(new Scored(Order(quad), score, stage.Name));
        }
        if (scored.Count == 0) return null;
        var best = scored.MaxBy(s => s.Score);
        // A frame printed on a cover, or a picture on a page, is a rectangle inside the
        // object: when a trustworthy rectangle with real edges on all four sides holds
        // the best one, the outer one is the object. Step outwards as far as that goes.
        for (bool widened = true; widened;)
        {
            widened = false;
            double area = Area(best.Corners);
            var outer = scored
                .Where(s => s.Score >= Math.Max(MinConfidence + 0.1, 0.7 * best.Score) && !OnBorder(s.Corners, w, h)
                    && Area(s.Corners) >= 1.03 * area && Encloses(s.Corners, best.Corners))
                .OrderByDescending(s => Area(s.Corners)).FirstOrDefault();
            if (outer.Corners != null) { best = outer; widened = true; }
        }
        return best;
    }

    private static double Area(Point2[] q)
    {
        double area = 0;
        for (int i = 0; i < 4; i++) area += q[i].X * q[(i + 1) % 4].Y - q[(i + 1) % 4].X * q[i].Y;
        return Math.Abs(area) / 2;
    }
    /// Whether every corner of the inner quad lies inside the outer one (give or take a pixel).
    private static bool Encloses(Point2[] outer, Point2[] inner) =>
        inner.All(p => Enumerable.Range(0, 4).All(k => Cross(outer[k], outer[(k + 1) % 4], p) / Math.Max(1e-9, Distance(outer[k], outer[(k + 1) % 4])) >= -1.5));
    /// Whether any side of the quad runs along the edge of the image.
    private static bool OnBorder(Point2[] q, int width, int height) => Enumerable.Range(0, 4).Any(i =>
    {
        Point2 a = q[i], b = q[(i + 1) % 4];
        return (Math.Abs(a.X) <= 1.5 && Math.Abs(b.X) <= 1.5) || (Math.Abs(a.Y) <= 1.5 && Math.Abs(b.Y) <= 1.5)
            || (Math.Abs(a.X - (width - 1)) <= 1.5 && Math.Abs(b.X - (width - 1)) <= 1.5) || (Math.Abs(a.Y - (height - 1)) <= 1.5 && Math.Abs(b.Y - (height - 1)) <= 1.5);
    });

    /// The part of the image that differs in Lab colour from the margin, split from it
    /// at the Otsu threshold of the colour difference, with small gaps closed.
    private static bool[] ColourMask(float[][] channels, int width, int height, Stage stage)
    {
        int n = width * height, band = Math.Max(1, Math.Min(width, height) / 20);
        var background = new double[3];
        for (int c = 0; c < 3; c++)
        {
            var values = new List<float>();
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    if (y < band || y >= height - band || x < band || x >= width - band) values.Add(channels[c][y * width + x]);
            values.Sort(); background[c] = values[values.Count / 2];
        }
        var difference = new float[n]; float largest = 0;
        for (int i = 0; i < n; i++)
        {
            double d0 = channels[0][i] - background[0], d1 = channels[1][i] - background[1], d2 = channels[2][i] - background[2];
            difference[i] = (float)Math.Sqrt(d0 * d0 + d1 * d1 + d2 * d2); largest = Math.Max(largest, difference[i]);
        }
        if (largest < stage.MaskFloor) return new bool[n];
        // Otsu on a 256-bin histogram of the difference.
        var histogram = new double[256];
        foreach (var d in difference) histogram[Math.Min(255, (int)(d / largest * 255))]++;
        double total = 0; for (int i = 0; i < 256; i++) total += i * histogram[i];
        double sumB = 0, weightB = 0, bestVariance = -1; int bestBin = 0;
        for (int i = 0; i < 256; i++)
        {
            weightB += histogram[i]; if (weightB == 0) continue;
            double weightF = n - weightB; if (weightF == 0) break;
            sumB += i * histogram[i];
            double meanB = sumB / weightB, meanF = (total - sumB) / weightF, variance = weightB * weightF * (meanB - meanF) * (meanB - meanF);
            if (variance > bestVariance) { bestVariance = variance; bestBin = i; }
        }
        double threshold = Math.Max(stage.MaskFloor, (bestBin + 0.5) / 255 * largest);
        var mask = difference.Select(d => d >= threshold).ToArray();
        return Close(mask, width, height, stage.Close + 1);
    }

    /// Four-sided outlines of the large connected shapes: each shape's outer contour
    /// (its convex hull) reduced with approxPolyDP until four corners remain.
    private static IEnumerable<Point2[]> ContourQuads(bool[] map, int width, int height)
    {
        var seen = new bool[map.Length]; var stack = new Stack<int>(); var shapes = new List<(int Size, List<Point2> Outline)>();
        for (int start = 0; start < map.Length; start++)
        {
            if (!map[start] || seen[start]) continue;
            var rowLeft = new Dictionary<int, int>(); var rowRight = new Dictionary<int, int>();
            int size = 0, minX = width, maxX = 0, minY = height, maxY = 0;
            seen[start] = true; stack.Push(start);
            while (stack.Count > 0)
            {
                int i = stack.Pop(), x = i % width, y = i / width; size++;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                rowLeft[y] = rowLeft.TryGetValue(y, out var lx) ? Math.Min(lx, x) : x;
                rowRight[y] = rowRight.TryGetValue(y, out var rx) ? Math.Max(rx, x) : x;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                        int j = ny * width + nx;
                        if (map[j] && !seen[j]) { seen[j] = true; stack.Push(j); }
                    }
            }
            if (maxX - minX < width * 0.15 || maxY - minY < height * 0.15) continue;
            var outline = new List<Point2>();
            foreach (var (y, x) in rowLeft) outline.Add(new Point2(x, y));
            foreach (var (y, x) in rowRight) outline.Add(new Point2(x, y));
            shapes.Add((size, outline));
        }
        foreach (var shape in shapes.OrderByDescending(s => s.Size).Take(12))
        {
            var hull = ConvexHull(shape.Outline);
            if (hull.Count < 4) continue;
            double perimeter = 0;
            for (int i = 0; i < hull.Count; i++) perimeter += Distance(hull[i], hull[(i + 1) % hull.Count]);
            for (double epsilon = 0.01 * perimeter; epsilon < 0.2 * perimeter; epsilon *= 1.25)
            {
                var approx = ApproxPolyDP(hull, epsilon);
                if (approx.Count == 4) { yield return approx.ToArray(); break; }
                if (approx.Count < 4) break;
            }
        }
    }

    private static List<Point2> ConvexHull(List<Point2> points)
    {
        var p = points.Distinct().OrderBy(q => q.X).ThenBy(q => q.Y).ToList();
        if (p.Count < 3) return p;
        var hull = new List<Point2>();
        for (int pass = 0; pass < 2; pass++)
        {
            int start = hull.Count;
            foreach (var q in p)
            {
                while (hull.Count >= start + 2 && Cross(hull[^2], hull[^1], q) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(q);
            }
            hull.RemoveAt(hull.Count - 1);
            p.Reverse();
        }
        return hull;
    }
    private static double Cross(Point2 o, Point2 a, Point2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
    private static double Distance(Point2 a, Point2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// Douglas–Peucker on a closed polygon, split at the vertex farthest from the first.
    private static List<Point2> ApproxPolyDP(List<Point2> polygon, double epsilon)
    {
        int far = 0; double farthest = -1;
        for (int i = 1; i < polygon.Count; i++) { double d = Distance(polygon[0], polygon[i]); if (d > farthest) { farthest = d; far = i; } }
        var first = polygon.GetRange(0, far + 1);
        var second = polygon.GetRange(far, polygon.Count - far); second.Add(polygon[0]);
        var result = Simplify(first, epsilon); result.RemoveAt(result.Count - 1);
        var rest = Simplify(second, epsilon); rest.RemoveAt(rest.Count - 1);
        result.AddRange(rest);
        return result;

        static List<Point2> Simplify(List<Point2> chain, double epsilon)
        {
            if (chain.Count < 3) return new List<Point2>(chain);
            Point2 a = chain[0], b = chain[^1]; double length = Math.Max(1e-9, Distance(a, b));
            int index = 0; double largest = 0;
            for (int i = 1; i < chain.Count - 1; i++)
            {
                double d = Math.Abs(Cross(a, b, chain[i])) / length;
                if (d > largest) { largest = d; index = i; }
            }
            if (largest <= epsilon) return new List<Point2> { a, b };
            var left = Simplify(chain.GetRange(0, index + 1), epsilon);
            var right = Simplify(chain.GetRange(index, chain.Count - index), epsilon);
            left.RemoveAt(left.Count - 1); left.AddRange(right);
            return left;
        }
    }

    /// Rectangles made from two near-vertical and two near-horizontal long lines of
    /// the Hough transform; the image edges count as lines too, for an object cut by them.
    private static IEnumerable<Point2[]> HoughQuads(bool[] edges, int width, int height, Stage stage)
    {
        const int Spread = 30;
        int diagonal = (int)Math.Ceiling(Math.Sqrt(width * width + height * height));
        int rhos = 2 * diagonal + 1, thetas = 2 * (2 * Spread + 1);
        var angles = new double[thetas];
        for (int t = 0; t <= 2 * Spread; t++) { angles[t] = (t - Spread) * Math.PI / 180; angles[t + 2 * Spread + 1] = (90 + t - Spread) * Math.PI / 180; }
        var cos = angles.Select(Math.Cos).ToArray(); var sin = angles.Select(Math.Sin).ToArray();
        var votes = new int[thetas * rhos];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (edges[y * width + x])
                    for (int t = 0; t < thetas; t++)
                        votes[t * rhos + (int)Math.Round(x * cos[t] + y * sin[t]) + diagonal]++;

        var vertical = Peaks(0, Math.Max(8, (int)(height * stage.MinLineShare)));
        var horizontal = Peaks(2 * Spread + 1, Math.Max(8, (int)(width * stage.MinLineShare)));
        vertical.Add((0, 0)); vertical.Add((0, width - 1));
        horizontal.Add((Math.PI / 2, 0)); horizontal.Add((Math.PI / 2, height - 1));

        double Across(double theta, double rho, bool isVertical) => isVertical
            ? (rho - height / 2.0 * Math.Sin(theta)) / Math.Cos(theta)
            : (rho - width / 2.0 * Math.Cos(theta)) / Math.Sin(theta);
        var vs = vertical.Select(v => (v.Theta, v.Rho, At: Across(v.Theta, v.Rho, true))).OrderBy(v => v.At).ToList();
        var hs = horizontal.Select(v => (v.Theta, v.Rho, At: Across(v.Theta, v.Rho, false))).OrderBy(v => v.At).ToList();
        for (int i = 0; i < vs.Count; i++)
            for (int j = i + 1; j < vs.Count; j++)
            {
                if (vs[j].At - vs[i].At < width * 0.15) continue;
                for (int k = 0; k < hs.Count; k++)
                    for (int m = k + 1; m < hs.Count; m++)
                    {
                        if (hs[m].At - hs[k].At < height * 0.15) continue;
                        var tl = Intersect(vs[i], hs[k]); var tr = Intersect(vs[j], hs[k]);
                        var br = Intersect(vs[j], hs[m]); var bl = Intersect(vs[i], hs[m]);
                        if (tl is { } a && tr is { } b && br is { } c && bl is { } d) yield return new[] { a, b, c, d };
                    }
            }

        List<(double Theta, double Rho)> Peaks(int from, int minVotes)
        {
            var cells = new List<(int Votes, int T, int R)>();
            for (int t = from; t < from + 2 * Spread + 1; t++)
                for (int r = 0; r < rhos; r++)
                {
                    int v = votes[t * rhos + r];
                    if (v >= minVotes) cells.Add((v, t, r));
                }
            var chosen = new List<(int T, int R)>();
            foreach (var cell in cells.OrderByDescending(c => c.Votes))
            {
                if (chosen.Any(c => Math.Abs(c.T - cell.T) <= 3 && Math.Abs(c.R - cell.R) <= 2)) continue;
                chosen.Add((cell.T, cell.R));
                if (chosen.Count == 10) break;
            }
            return chosen.Select(c => (angles[c.T], (double)(c.R - diagonal))).ToList();
        }
        static Point2? Intersect((double Theta, double Rho, double At) p, (double Theta, double Rho, double At) q)
        {
            double a1 = Math.Cos(p.Theta), b1 = Math.Sin(p.Theta), a2 = Math.Cos(q.Theta), b2 = Math.Sin(q.Theta);
            double det = a1 * b2 - a2 * b1;
            if (Math.Abs(det) < 1e-6) return null;
            return new Point2((p.Rho * b2 - q.Rho * b1) / det, (a1 * q.Rho - a2 * p.Rho) / det);
        }
    }

    // ---------------------------------------------------------------- scoring

    private static Point2[] Order(Point2[] quad)
    {
        var tl = quad.OrderBy(p => p.X + p.Y).First(); var br = quad.OrderBy(p => p.X + p.Y).Last();
        var tr = quad.OrderBy(p => p.Y - p.X).First(); var bl = quad.OrderBy(p => p.Y - p.X).Last();
        return new[] { tl, tr, br, bl };
    }

    /// Confidence 0–1 that the quad is the outline of the object.
    private static double Score(Point2[] raw, bool[] support, double chance, float[][] channels, int width, int height, Box? content)
    {
        var q = Order(raw);
        if (q.Distinct().Count() < 4) return 0;
        // A convex shape inside (or barely past) the image.
        for (int i = 0; i < 4; i++)
        {
            if (Cross(q[i], q[(i + 1) % 4], q[(i + 2) % 4]) <= 0) return 0;
            if (q[i].X < -2 || q[i].Y < -2 || q[i].X > width + 1 || q[i].Y > height + 1) return 0;
        }
        // Corners near 90 degrees.
        double worstAngle = 0;
        for (int i = 0; i < 4; i++)
        {
            Point2 p = q[(i + 3) % 4], c = q[i], n = q[(i + 1) % 4];
            double ux = p.X - c.X, uy = p.Y - c.Y, vx = n.X - c.X, vy = n.Y - c.Y;
            double cos = (ux * vx + uy * vy) / Math.Max(1e-9, Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy)));
            worstAngle = Math.Max(worstAngle, Math.Abs(Math.Acos(Math.Clamp(cos, -1, 1)) * 180 / Math.PI - 90));
        }
        if (worstAngle > 35) return 0;
        double angleScore = 1 - worstAngle / 35;

        // Sides that run along edges in the image, beyond what chance gives; a side on
        // the image edge counts for little, and an outline that is mostly image edge is no outline.
        double sum = 0, weakest = 1, faintest = double.MaxValue; int borderSides = 0;
        for (int i = 0; i < 4; i++)
        {
            Point2 a = q[i], b = q[(i + 1) % 4];
            double s;
            bool onBorder = (Math.Abs(a.X) <= 1.5 && Math.Abs(b.X) <= 1.5) || (Math.Abs(a.Y) <= 1.5 && Math.Abs(b.Y) <= 1.5)
                || (Math.Abs(a.X - (width - 1)) <= 1.5 && Math.Abs(b.X - (width - 1)) <= 1.5) || (Math.Abs(a.Y - (height - 1)) <= 1.5 && Math.Abs(b.Y - (height - 1)) <= 1.5);
            if (onBorder) { s = 0.35; borderSides++; }
            else
            {
                int samples = Math.Max(20, (int)Distance(a, b)), hits = 0, count = 0;
                for (int k = 0; k <= samples; k++)
                {
                    double t = 0.05 + 0.9 * k / samples;
                    int x = (int)Math.Round(a.X + (b.X - a.X) * t), y = (int)Math.Round(a.Y + (b.Y - a.Y) * t);
                    if (x < 0 || y < 0 || x >= width || y >= height) continue;
                    count++; if (support[y * width + x]) hits++;
                }
                s = count == 0 ? 0 : Math.Max(0, ((double)hits / count - chance) / (1 - chance));
                faintest = Math.Min(faintest, ColourStep(q, i, channels, width, height));
            }
            sum += s; weakest = Math.Min(weakest, s);
        }
        if (borderSides > 2) return 0;
        double straightScore = 0.5 * sum / 4 + 0.5 * weakest;

        double area = 0;
        for (int i = 0; i < 4; i++) area += q[i].X * q[(i + 1) % 4].Y - q[(i + 1) % 4].X * q[i].Y;
        double ratio = Math.Abs(area) / 2 / ((double)width * height);
        if (ratio < 0.04) return 0;
        double areaScore = ratio < 0.2 ? (ratio - 0.04) / 0.16 : ratio <= 0.95 ? 1 : 1 - 0.7 * Math.Min(1, (ratio - 0.95) / 0.05);
        double sizeScore = Math.Min(1, ratio / 0.5);

        double cx = q.Average(p => p.X), cy = q.Average(p => p.Y);
        double centreScore = 1 - Math.Min(1, Math.Sqrt((cx - width / 2.0) * (cx - width / 2.0) + (cy - height / 2.0) * (cy - height / 2.0))
            / (0.5 * Math.Sqrt((double)width * width + (double)height * height)));

        // The object holds the strongly contrasting content, such as the title of a book.
        if (content is Box box && CoverageOf(q, box) < 0.6) return 0;

        double score = 0.45 * straightScore + 0.15 * angleScore + 0.15 * areaScore + 0.10 * centreScore + 0.15 * sizeScore;
        // Without edges along its sides, or without a colour change across them, a quad
        // is only a guess, whatever its shape.
        double step = faintest == double.MaxValue ? 1 : Math.Min(1, faintest / 4);
        return score * Math.Min(1, straightScore / 0.6) * step;
    }

    /// The Lab difference between the average colour just inside a side and just outside it.
    private static double ColourStep(Point2[] q, int side, float[][] channels, int width, int height)
    {
        Point2 a = q[side], b = q[(side + 1) % 4];
        double cx = q.Average(p => p.X), cy = q.Average(p => p.Y);
        double dx = b.X - a.X, dy = b.Y - a.Y, length = Math.Max(1e-9, Math.Sqrt(dx * dx + dy * dy)), nx = -dy / length, ny = dx / length;
        if (nx * ((a.X + b.X) / 2 - cx) + ny * ((a.Y + b.Y) / 2 - cy) < 0) { nx = -nx; ny = -ny; }
        var inside = new double[3]; var outside = new double[3]; int count = 0;
        for (int k = 0; k <= 20; k++)
        {
            double t = 0.1 + 0.8 * k / 20, px = a.X + dx * t, py = a.Y + dy * t;
            int ix = (int)Math.Round(px - 3 * nx), iy = (int)Math.Round(py - 3 * ny), ox = (int)Math.Round(px + 3 * nx), oy = (int)Math.Round(py + 3 * ny);
            if (ix < 0 || iy < 0 || ox < 0 || oy < 0 || ix >= width || ox >= width || iy >= height || oy >= height) continue;
            for (int c = 0; c < 3; c++) { inside[c] += channels[c][iy * width + ix]; outside[c] += channels[c][oy * width + ox]; }
            count++;
        }
        if (count < 5) return double.MaxValue;
        double sum = 0;
        for (int c = 0; c < 3; c++) { double d = (inside[c] - outside[c]) / count; sum += d * d; }
        return Math.Sqrt(sum);
    }

    private static double Coverage(Corner[] corners, Box box) =>
        CoverageOf(corners.Select(c => new Point2(c.X, c.Y)).ToArray(), box);
    /// The share of the box that lies inside the quad, on a 10 × 10 grid of points.
    private static double CoverageOf(Point2[] q, Box box)
    {
        int inside = 0;
        for (int j = 0; j < 10; j++)
            for (int i = 0; i < 10; i++)
            {
                var p = new Point2(box.Left + (i + 0.5) * box.Width / 10, box.Top + (j + 0.5) * box.Height / 10);
                bool all = true;
                for (int k = 0; k < 4 && all; k++) all = Cross(q[k], q[(k + 1) % 4], p) >= -1e-6 * Math.Max(1, box.Width * box.Height);
                if (all) inside++;
            }
        return inside / 100.0;
    }

    /// Moves each side of a quad found on the reduced image onto the strongest
    /// colour step near it in the full image, then rebuilds the corners.
    private static Corner[] Refine(byte[] pixels, int width, int height, Corner[] corners, int scale)
    {
        int reach = 2 * scale + 1;
        var lines = new (double X, double Y, double DX, double DY)[4];
        double cx = corners.Average(c => c.X), cy = corners.Average(c => c.Y);
        for (int s = 0; s < 4; s++)
        {
            Corner a = corners[s], b = corners[(s + 1) % 4];
            double dx = b.X - a.X, dy = b.Y - a.Y, length = Math.Max(1e-9, Math.Sqrt(dx * dx + dy * dy));
            double nx = -dy / length, ny = dx / length;
            // Normal pointing out of the quad.
            if (nx * ((a.X + b.X) / 2 - cx) + ny * ((a.Y + b.Y) / 2 - cy) < 0) { nx = -nx; ny = -ny; }
            var offsets = new List<double>();
            for (int k = 0; k < 40; k++)
            {
                double t = 0.1 + 0.8 * k / 39, px = a.X + dx * t, py = a.Y + dy * t;
                // The object's own colour a little further in: a step only counts when it
                // leaves that colour, not one between two shades of the margin.
                int rx = (int)Math.Floor(px - (reach + 3) * nx), ry = (int)Math.Floor(py - (reach + 3) * ny);
                double bestStep = 0, bestOffset = 0;
                for (int o = -reach; o <= reach; o++)
                {
                    double qx = px + o * nx, qy = py + o * ny;
                    int ix = (int)Math.Floor(qx - 0.5 * nx), iy = (int)Math.Floor(qy - 0.5 * ny), ox = (int)Math.Floor(qx + 0.5 * nx), oy = (int)Math.Floor(qy + 0.5 * ny);
                    double inner = DeltaE(pixels, width, height, ix, iy, rx, ry), outer = DeltaE(pixels, width, height, ox, oy, rx, ry);
                    double step = DeltaE(pixels, width, height, ix, iy, ox, oy) * Math.Clamp((outer - inner) / Math.Max(1, outer), 0, 1);
                    // The boundary between the two pixels, measured along the normal.
                    if (step > bestStep) { bestStep = step; bestOffset = ((ix + ox + 1) / 2.0 - px) * nx + ((iy + oy + 1) / 2.0 - py) * ny; }
                }
                if (bestStep >= 3) offsets.Add(bestOffset);
            }
            double shift = 0;
            if (offsets.Count >= 10) { offsets.Sort(); shift = offsets[offsets.Count / 2]; }
            lines[s] = (a.X + shift * nx, a.Y + shift * ny, dx, dy);
        }
        var refined = new Corner[4];
        for (int i = 0; i < 4; i++)
        {
            var p = lines[(i + 3) % 4]; var q = lines[i];
            double det = p.DX * q.DY - p.DY * q.DX;
            if (Math.Abs(det) < 1e-9) return corners;
            double t = ((q.X - p.X) * q.DY - (q.Y - p.Y) * q.DX) / det;
            refined[i] = new Corner(Math.Clamp(p.X + p.DX * t, 0, width), Math.Clamp(p.Y + p.DY * t, 0, height));
        }
        return refined;
    }

    // ---------------------------------------------------------------- perspective

    /// The quad mapped onto an upright rectangle (perspective transform, bilinear).
    internal static byte[] WarpBgra(byte[] pixels, int width, int height, Corner[] c, out int outWidth, out int outHeight)
    {
        outWidth = Math.Max(1, (int)Math.Round(Math.Max(Length(c[0], c[1]), Length(c[3], c[2]))));
        outHeight = Math.Max(1, (int)Math.Round(Math.Max(Length(c[0], c[3]), Length(c[1], c[2]))));
        var h = Homography(BoxCorners(new Box(0, 0, outWidth, outHeight)), c);
        var result = new byte[outWidth * outHeight * 4];
        for (int v = 0; v < outHeight; v++)
            for (int u = 0; u < outWidth; u++)
            {
                double x = u + 0.5, y = v + 0.5, w = h[6] * x + h[7] * y + 1;
                double sx = (h[0] * x + h[1] * y + h[2]) / w - 0.5, sy = (h[3] * x + h[4] * y + h[5]) / w - 0.5;
                int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy); double fx = sx - x0, fy = sy - y0;
                for (int ch = 0; ch < 4; ch++)
                {
                    double value = Sample(x0, y0, ch) * (1 - fx) * (1 - fy) + Sample(x0 + 1, y0, ch) * fx * (1 - fy)
                        + Sample(x0, y0 + 1, ch) * (1 - fx) * fy + Sample(x0 + 1, y0 + 1, ch) * fx * fy;
                    result[(v * outWidth + u) * 4 + ch] = (byte)Math.Clamp(Math.Round(value), 0, 255);
                }
            }
        return result;
        double Sample(int x, int y, int ch) => pixels[(Math.Clamp(y, 0, height - 1) * width + Math.Clamp(x, 0, width - 1)) * 4 + ch];
        static double Length(Corner a, Corner b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    }

    /// The projective map taking the four points of from onto those of to, as
    /// h0..h7 of x' = (h0 x + h1 y + h2) / (h6 x + h7 y + 1), y' = (h3 x + h4 y + h5) / (…).
    internal static double[] Homography(Corner[] from, Corner[] to)
    {
        var m = new double[8, 9];
        for (int i = 0; i < 4; i++)
        {
            double x = from[i].X, y = from[i].Y, u = to[i].X, v = to[i].Y;
            double[] r1 = { x, y, 1, 0, 0, 0, -u * x, -u * y, u }, r2 = { 0, 0, 0, x, y, 1, -v * x, -v * y, v };
            for (int k = 0; k < 9; k++) { m[2 * i, k] = r1[k]; m[2 * i + 1, k] = r2[k]; }
        }
        for (int col = 0; col < 8; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < 8; r++) if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col])) pivot = r;
            for (int k = 0; k < 9; k++) (m[col, k], m[pivot, k]) = (m[pivot, k], m[col, k]);
            double d = m[col, col];
            if (Math.Abs(d) < 1e-12) throw new InvalidOperationException("The outline has no area.");
            for (int k = 0; k < 9; k++) m[col, k] /= d;
            for (int r = 0; r < 8; r++)
            {
                if (r == col || m[r, col] == 0) continue;
                double f = m[r, col];
                for (int k = 0; k < 9; k++) m[r, k] -= f * m[col, k];
            }
        }
        var h = new double[8];
        for (int i = 0; i < 8; i++) h[i] = m[i, 8];
        return h;
    }
}
