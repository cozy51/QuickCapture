using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickCapture.Controls;

public sealed class CaptureToolbar : Border
{
    private readonly WrapPanel panel = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Button highlighter;
    private readonly Button undo;
    public CaptureToolbar(Action move, Action copy, Action highlight, Action undoAction, Action actual, Action fit, Action save, Action close)
    {
        Background = new SolidColorBrush(Color.FromArgb(237, 29, 33, 41));
        CornerRadius = new CornerRadius(7); Padding = new Thickness(3);
        Margin = new Thickness(6); VerticalAlignment = VerticalAlignment.Top; HorizontalAlignment = HorizontalAlignment.Center;
        Child = panel;
        var grip = new TextBlock { Text = "⠿", Foreground = Brushes.Gray, Padding = new Thickness(7, 5, 7, 5), Cursor = Cursors.SizeAll, ToolTip = "ドラッグでウィンドウ移動（通常は画像の左ドラッグでも移動）" };
        grip.MouseLeftButtonDown += (_, e) => { move(); e.Handled = true; }; panel.Children.Add(grip);
        Add("⧉", "コピー · Ctrl+C", copy);
        highlighter = Add("H", "蛍光ペン · H / Ctrl＋ドラッグ / 色は右クリック", highlight);
        undo = Add("↶", "元に戻す · Ctrl+Z", undoAction);
        Add("1:1", "等倍 · 1 / Ctrl+0", actual);
        Add("Fit", "全体を表示 · F", fit);
        Add("↓", "PNGで保存 · Ctrl+S", save);
        Add("×", "閉じる · Delete", close);
    }
    private Button Add(string text, string hint, Action action)
    {
        var button = new Button { Content = text, ToolTip = hint, Width = 34, Height = 29, Margin = new Thickness(1), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Focusable = false, Cursor = Cursors.Hand };
        // A small explicit template avoids a bright system button chrome over the image.
        var factory = new FrameworkElementFactory(typeof(Border));
        factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        factory.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        var content = new FrameworkElementFactory(typeof(ContentPresenter)); content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center); content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center); factory.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = factory };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(BackgroundProperty, new SolidColorBrush(Color.FromRgb(65, 73, 86)))); template.Triggers.Add(hover);
        button.Template = template;
        button.Click += (_, _) => action(); panel.Children.Add(button); return button;
    }
    public void Update(bool highlighting, bool canUndo, Color highlighterColor)
    {
        highlighter.Foreground = highlighting ? new SolidColorBrush(highlighterColor) : Brushes.White;
        undo.Opacity = canUndo ? 1 : 0.35; undo.IsEnabled = canUndo;
    }
}
