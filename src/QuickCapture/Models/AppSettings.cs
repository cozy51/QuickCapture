using System;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickCapture.Models;

public sealed record AppSettings
{
    public const string DefaultHighlighterColor = "#68E675";
    public string GlobalShortcut { get; set; } = "Ctrl+Shift+R";
    public string HighlighterColor { get; set; } = DefaultHighlighterColor;
    public double HighlighterWidth { get; set; } = 20;
    public double HighlighterOpacity { get; set; } = 0.45;
    public bool AlwaysOnTop { get; set; } = true;
    public bool CopyIncludesAnnotations { get; set; } = true;
    public bool CloseAfterCopy { get; set; }
    public bool AutoCloseCaptures { get; set; }
    public bool ExportBorderEnabled { get; set; } = true;
    public bool ExportHeaderEnabled { get; set; }

    public void Validate()
    {
        _ = ParseShortcut(GlobalShortcut);
        try { if (string.IsNullOrWhiteSpace(HighlighterColor) || ColorConverter.ConvertFromString(HighlighterColor) is not Color) throw new ArgumentException(); }
        catch { throw new ArgumentException("蛍光ペン色が正しくありません。"); }
        if (!double.IsFinite(HighlighterWidth) || HighlighterWidth < 2 || HighlighterWidth > 200) throw new ArgumentException("線幅は2〜200pxで指定してください。");
        if (!double.IsFinite(HighlighterOpacity) || HighlighterOpacity < 0.1 || HighlighterOpacity > 0.8) throw new ArgumentException("透明度は0.1〜0.8で指定してください。");
    }
    public static (ModifierKeys Modifiers, Key Key) ParseShortcut(string value)
    {
        ModifierKeys modifiers = ModifierKeys.None; Key key = Key.None;
        foreach (var part in (value ?? "").Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL": case "CONTROL": modifiers |= ModifierKeys.Control; break;
                case "SHIFT": modifiers |= ModifierKeys.Shift; break;
                case "ALT": modifiers |= ModifierKeys.Alt; break;
                case "WIN": case "WINDOWS": modifiers |= ModifierKeys.Windows; break;
                default:
                    if (key != Key.None) throw new ArgumentException("キーは1つだけ指定してください。");
                    try { key = (Key)new KeyConverter().ConvertFromInvariantString(part)!; }
                    catch { throw new ArgumentException("例: Ctrl+Shift+R の形式で指定してください。"); }
                    break;
            }
        }
        if (key == Key.None || KeyInterop.VirtualKeyFromKey(key) == 0 || (modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0)
            throw new ArgumentException("Ctrl / Alt / Win と通常キーを組み合わせてください。");
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            throw new ArgumentException("修飾キー以外のキーを指定してください。");
        return (modifiers, key);
    }
}
