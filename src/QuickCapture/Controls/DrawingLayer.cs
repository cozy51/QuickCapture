using System.Windows;
using System.Windows.Media;
using QuickCapture.Models;

namespace QuickCapture.Controls;

public static class DrawingLayer
{
    /// <param name="hidden">Left out while it is being dragged somewhere else.</param>
    public static void Render(DrawingContext context, ImageDocument document, IAnnotation? preview = null, IAnnotation? hidden = null)
    {
        context.PushClip(new RectangleGeometry(new Rect(0, 0, document.Width, document.Height)));
        foreach (var annotation in document.Annotations)
            if (!ReferenceEquals(annotation, hidden)) annotation.Render(context);
        preview?.Render(context);
        context.Pop();
    }
}
