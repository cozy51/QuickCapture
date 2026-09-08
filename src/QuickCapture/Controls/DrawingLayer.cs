using System.Windows;
using System.Windows.Media;
using QuickCapture.Models;

namespace QuickCapture.Controls;

public static class DrawingLayer
{
    public static void Render(DrawingContext context, ImageDocument document, IAnnotation? preview = null)
    {
        context.PushClip(new RectangleGeometry(new Rect(0, 0, document.Width, document.Height)));
        foreach (var annotation in document.Annotations) annotation.Render(context);
        preview?.Render(context);
        context.Pop();
    }
}
