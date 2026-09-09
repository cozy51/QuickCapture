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
    public BitmapSource Compose(ImageDocument document, bool includeAnnotations = true, bool includeBorder = false, CaptureHeaderInfo? header = null)
    {
        if (document.Image == null) throw new ObjectDisposedException(nameof(ImageDocument));
        bool annotations = includeAnnotations && document.Annotations.Count > 0;
        if (!annotations) return Decorate(document.Image, includeBorder, header);
        return RenderImage(document.Image, includeBorder, header, dc => DrawingLayer.Render(dc, document));
    }
    /// Capture-time copy of the untouched image with the enabled decorations only.
    public static BitmapSource Decorate(BitmapSource image, bool includeBorder, CaptureHeaderInfo? header) =>
        includeBorder || header != null ? RenderImage(image, includeBorder, header, null) : image;
    private static BitmapSource RenderImage(BitmapSource image, bool includeBorder, CaptureHeaderInfo? header, Action<DrawingContext>? annotations)
    {
        int inset = includeBorder ? 1 : 0;
        int band = header == null ? 0 : (int)CaptureHeader.Height;
        int width = checked(image.PixelWidth + inset * 2), height = checked(image.PixelHeight + inset * 2 + band);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (header != null)
            {
                // The close button is a control rather than part of the record,
                // so the exported band keeps a plain margin in its place.
                dc.DrawRectangle(CaptureHeader.RecordBackground, null, new Rect(0, 0, width, band));
                CaptureHeader.Render(dc, width, 1, header.Value, 0);
                dc.PushTransform(new TranslateTransform(0, band));
            }
            if (includeBorder)
            {
                double edge = image.PixelWidth + 2, bottom = image.PixelHeight + 2;
                var gray = new SolidColorBrush(Color.FromRgb(200, 200, 200));
                dc.DrawRectangle(gray, null, new Rect(0, 0, edge, 1));
                dc.DrawRectangle(gray, null, new Rect(0, bottom - 1, edge, 1));
                dc.DrawRectangle(gray, null, new Rect(0, 1, 1, bottom - 2));
                dc.DrawRectangle(gray, null, new Rect(edge - 1, 1, 1, bottom - 2));
                dc.PushTransform(new TranslateTransform(1, 1));
            }
            dc.DrawImage(image, new Rect(0, 0, image.PixelWidth, image.PixelHeight));
            annotations?.Invoke(dc);
            if (includeBorder) dc.Pop();
            if (header != null) dc.Pop();
        }
        var output = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        output.Render(visual); output.Freeze(); return output;
    }
    public static void WritePng(BitmapSource image, Stream target)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(target);
    }
    public void SavePng(ImageDocument document, string path, bool includeBorder = false, CaptureHeaderInfo? header = null)
    {
        var image = Compose(document, includeBorder: includeBorder, header: header);
        // Encode first so a failure does not truncate a previously saved file.
        using var encoded = new MemoryStream(); WritePng(image, encoded);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoded.Position = 0; encoded.CopyTo(stream);
    }
}
