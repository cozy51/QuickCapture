using System.Windows.Media;

namespace QuickCapture.Controls;

/// The colours offered for the highlighter, the pen and the labels alike.
public static class DrawingPalette
{
    public static readonly (string Name, string Hex)[] Colors =
    {
        ("青", "#2F7BF6"), ("赤", "#FF5555"), ("黄色", "#FFE338"), ("緑", "#68E675"),
        ("水色", "#51D9FF"), ("ピンク", "#FF83C9"), ("黒", "#222222")
    };
    public static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
