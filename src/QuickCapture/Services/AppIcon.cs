using System;
using System.Windows;
using System.Windows.Media.Imaging;

namespace QuickCapture.Services;

internal static class AppIcon
{
    private static readonly Uri Resource = new("/QuickCapture;component/Assets/QuickCapture.ico", UriKind.Relative);
    internal static BitmapFrame Image { get; } = LoadImage();
    private static BitmapFrame LoadImage()
    {
        using var stream = Application.GetResourceStream(Resource)!.Stream;
        var image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        image.Freeze(); return image;
    }
    internal static System.Drawing.Icon CreateTrayIcon()
    {
        using var stream = Application.GetResourceStream(Resource)!.Stream;
        using var icon = new System.Drawing.Icon(stream, 32, 32);
        return (System.Drawing.Icon)icon.Clone();
    }
}
