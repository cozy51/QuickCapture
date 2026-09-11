using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickCapture.Controls;

public sealed class CaptureToolbar : Border
{
    private readonly WrapPanel panel = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Button highlighter;
    private readonly Button pen;
    private readonly Button text;
    private readonly Button undo;
    private readonly List<(Border Swatch, Color Color)> swatches = new();
    public CaptureToolbar(Action move, Action copy, Action highlight, Action drawPen, Action addText, Action undoAction, Action actual, Action fit, Action save, Action close, Action<Color> pickColor)
    {
        Background = new SolidColorBrush(Color.FromArgb(237, 29, 33, 41));
        CornerRadius = new CornerRadius(7); Padding = new Thickness(3);
        Margin = new Thickness(6); VerticalAlignment = VerticalAlignment.Top; HorizontalAlignment = HorizontalAlignment.Center;
        MaxWidth = 520;
        Child = panel;
        var grip = new TextBlock { Text = "⠿", Foreground = Brushes.Gray, Padding = new Thickness(7, 5, 7, 5), Cursor = Cursors.SizeAll, ToolTip = "ドラッグでウィンドウ移動（通常は画像の左ドラッグでも移動）" };
        grip.MouseLeftButtonDown += (_, e) => { move(); e.Handled = true; }; panel.Children.Add(grip);
        Add("⧉", "コピー · Ctrl+C", copy);
        highlighter = Add("H", "蛍光ペン · H / Ctrl＋ドラッグ", highlight);
        pen = Add("✎", "ペン · P", drawPen);
        text = Add("T", "テキスト · X / クリックで入力、文字をドラッグで移動", addText);
        // The colours change whichever tool is in hand, so they sit beside them.
        foreach (var (name, hex) in DrawingPalette.Colors)
        {
            var value = DrawingPalette.Parse(hex);
            var swatch = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Margin = new Thickness(2, 5, 2, 5),
                Background = new SolidColorBrush(value), BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand, ToolTip = $"{name} · 今のツールの色にする"
            };
            swatch.MouseLeftButtonDown += (_, e) => { e.Handled = true; pickColor(value); };
            panel.Children.Add(swatch); swatches.Add((swatch, value));
        }
        undo = Add("↶", "元に戻す · Ctrl+Z", undoAction);
        Add("1:1", "等倍 · 1 / Ctrl+0", actual);
        Add("Fit", "全体を表示 · F", fit);
        Add("↓", "PNGで保存 · Ctrl+S", save);
        Add("×", "閉じる · Delete", close);
    }
    private Button Add(string content, string hint, Action action)
    {
        var button = new Button { Content = content, ToolTip = hint, Width = 34, Height = 29, Margin = new Thickness(1), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Focusable = false, Cursor = Cursors.Hand };
        // A small explicit template avoids a bright system button chrome over the image.
        var factory = new FrameworkElementFactory(typeof(Border));
        factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        factory.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        var content2 = new FrameworkElementFactory(typeof(ContentPresenter)); content2.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center); content2.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center); factory.AppendChild(content2);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = factory };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(BackgroundProperty, new SolidColorBrush(Color.FromRgb(65, 73, 86)))); template.Triggers.Add(hover);
        button.Template = template;
        button.Click += (_, _) => action(); panel.Children.Add(button); return button;
    }
    public void Update(DrawingTool tool, bool canUndo, Color color)
    {
        var active = new SolidColorBrush(color); active.Freeze();
        highlighter.Foreground = tool == DrawingTool.Highlighter ? active : Brushes.White;
        pen.Foreground = tool == DrawingTool.Pen ? active : Brushes.White;
        text.Foreground = tool == DrawingTool.Text ? active : Brushes.White;
        foreach (var (swatch, value) in swatches)
            swatch.BorderBrush = tool != DrawingTool.None && value == color ? Brushes.White : Brushes.Transparent;
        undo.Opacity = canUndo ? 1 : 0.35; undo.IsEnabled = canUndo;
    }
}
