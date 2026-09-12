using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuickCapture.Controls;
using QuickCapture.Models;
using QuickCapture.Services;

namespace QuickCapture.Windows;

public sealed class SettingsWindow : Window
{
    public SettingsWindow(AppSettings settings, Action<AppSettings> apply)
    {
        Title = "QuickCapture の設定"; Width = 410; SizeToContent = SizeToContent.Height;
        Icon = AppIcon.Image;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(247, 248, 250));
        var panel = new StackPanel { Margin = new Thickness(24) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "いつもの操作を、すぐに。", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 18) });
        void Label(string text) => panel.Children.Add(new TextBlock { Text = text, Margin = new Thickness(0, 8, 0, 5) });
        Label("キャプチャのショートカット");
        var shortcut = new TextBox { Text = settings.GlobalShortcut, Padding = new Thickness(8) }; panel.Children.Add(shortcut);
        Label("蛍光ペンの初期色");
        var color = new ComboBox { Padding = new Thickness(6) };
        foreach (var item in HighlighterTool.Palette) color.Items.Add(new ComboBoxItem { Content = item.Name, Tag = item.Hex });
        color.SelectedIndex = Array.FindIndex(HighlighterTool.Palette, p => p.Hex.Equals(settings.HighlighterColor, StringComparison.OrdinalIgnoreCase));
        if (color.SelectedIndex < 0) { color.Items.Add(new ComboBoxItem { Content = "カスタム色", Tag = settings.HighlighterColor }); color.SelectedIndex = color.Items.Count - 1; }
        panel.Children.Add(color);
        Label("線幅（元画像のpx、2〜200）");
        var width = new TextBox { Text = settings.HighlighterWidth.ToString(CultureInfo.CurrentCulture), Padding = new Thickness(8) }; panel.Children.Add(width);
        var topmost = new CheckBox { Content = "新しい画像を常に手前に表示", IsChecked = settings.AlwaysOnTop, Margin = new Thickness(0, 18, 0, 10) }; panel.Children.Add(topmost);
        var annotations = new CheckBox { Content = "コピーにマーキングを含める", IsChecked = settings.CopyIncludesAnnotations, Margin = new Thickness(0, 0, 0, 10) }; panel.Children.Add(annotations);
        var exportBorder = new CheckBox { Content = "コピー・PNG保存画像に薄いグレーの外枠を付ける", IsChecked = settings.ExportBorderEnabled, Margin = new Thickness(0, 0, 0, 10) }; panel.Children.Add(exportBorder);
        var exportHeader = new CheckBox { Content = "コピー・PNG保存画像に上部のカウンターと日時情報を含める", IsChecked = settings.ExportHeaderEnabled, Margin = new Thickness(0, 0, 0, 10) }; panel.Children.Add(exportHeader);
        var close = new CheckBox { Content = "コピー成功後に画像ウィンドウを閉じる", IsChecked = settings.CloseAfterCopy }; panel.Children.Add(close);
        var autoClose = new CheckBox { Content = "新しいキャプチャを3秒で閉じる", IsChecked = settings.AutoCloseCaptures, Margin = new Thickness(0, 10, 0, 0) }; panel.Children.Add(autoClose);
        var error = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) }; panel.Children.Add(error);
        var save = new Button { Content = "設定を保存", Padding = new Thickness(16, 8, 16, 8), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), IsDefault = true }; panel.Children.Add(save);
        save.Click += (_, _) =>
        {
            try
            {
                if (!double.TryParse(width.Text, out double pixels)) throw new ArgumentException("線幅を数値で入力してください。");
                var next = settings with { GlobalShortcut = shortcut.Text.Trim(), HighlighterColor = (string)((ComboBoxItem)color.SelectedItem).Tag, HighlighterWidth = pixels, AlwaysOnTop = topmost.IsChecked == true, CopyIncludesAnnotations = annotations.IsChecked == true, CloseAfterCopy = close.IsChecked == true, AutoCloseCaptures = autoClose.IsChecked == true };
                next.ExportBorderEnabled = exportBorder.IsChecked == true;
                next.ExportHeaderEnabled = exportHeader.IsChecked == true;
                next.Validate(); apply(next); Close();
            }
            catch (Exception ex) { error.Text = ex.Message; }
        };
    }
}
