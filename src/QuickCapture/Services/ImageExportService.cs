using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickCapture.Controls;
using QuickCapture.Models;

namespace QuickCapture.Services;

public sealed class ImageExportService
{
    public BitmapSource Compose(ImageDocument document, bool includeAnnotations = true, bool includeBorder = false)
    {
        if (document.Image == null) throw new ObjectDisposedException(nameof(ImageDocument));
        if (!includeAnnotations || document.Annotations.Count == 0) return includeBorder ? AddBorder(document.Image) : document.Image;
        return RenderImage(document.Image, includeBorder, dc => DrawingLayer.Render(dc, document));
    }
    public static BitmapSource AddBorder(BitmapSource image) => RenderImage(image, true, null);
    private static BitmapSource RenderImage(BitmapSource image, bool includeBorder, Action<DrawingContext>? annotations)
    {
        int inset = includeBorder ? 1 : 0;
        int width = checked(image.PixelWidth + inset * 2), height = checked(image.PixelHeight + inset * 2);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (includeBorder)
            {
                var gray = new SolidColorBrush(Color.FromRgb(200, 200, 200));
                dc.DrawRectangle(gray, null, new Rect(0, 0, width, 1));
                dc.DrawRectangle(gray, null, new Rect(0, height - 1, width, 1));
                dc.DrawRectangle(gray, null, new Rect(0, 1, 1, height - 2));
                dc.DrawRectangle(gray, null, new Rect(width - 1, 1, 1, height - 2));
                dc.PushTransform(new TranslateTransform(1, 1));
            }
            dc.DrawImage(image, new Rect(0, 0, image.PixelWidth, image.PixelHeight));
            annotations?.Invoke(dc);
            if (includeBorder) dc.Pop();
        }
        var output = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        output.Render(visual); output.Freeze(); return output;
    }
    public static void WritePng(BitmapSource image, Stream target)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(target);
    }
    public void SavePng(ImageDocument document, string path, bool includeBorder = false)
    {
        var image = Compose(document, includeBorder: includeBorder);
        // Encode first so a failure does not truncate a previously saved file.
        using var encoded = new MemoryStream(); WritePng(image, encoded);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoded.Position = 0; encoded.CopyTo(stream);
    }
}
