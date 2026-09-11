using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Linq;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Input;
using QuickCapture.Interop;
using QuickCapture.Models;
using QuickCapture.Services;
using QuickCapture.Windows;
using Forms = System.Windows.Forms;

namespace QuickCapture;

public partial class App : Application
{
    private Mutex? mutex;
    private bool ownsMutex;
    private GlobalHotkeyService? hotkey;
    private CaptureCoordinator? capture;
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private readonly SettingsService settingsService = new();
    private AppSettings settings = new();
    private SettingsWindow? settingsWindow;
    /// Runs before WPF touches the screen, so the process is per-monitor DPI aware
    /// however it was started. Without this, launching through the shared host
    /// (`dotnet QuickCapture.dll`, a way around an exe blocked by Smart App
    /// Control) would leave Windows scaling the windows for us.
    [ModuleInitializer]
    internal static void UsePerMonitorDpi() => NativeMethods.UsePerMonitorDpi();
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        mutex = new Mutex(true, "Local\\QuickCapture.SingleInstance.v1", out ownsMutex);
        if (!ownsMutex) { if (!e.Args.Contains("--background")) NativeMethods.PostMessage(new IntPtr(0xffff), GlobalHotkeyService.CaptureMessage, IntPtr.Zero, IntPtr.Zero); Shutdown(); return; }
        settings = settingsService.Load();
        capture = new CaptureCoordinator();
        capture.Failed += ex => Notify("キャプチャできませんでした: " + ex.Message);
        capture.Captured += (image, rect) =>
        {
            var window = new CaptureWindow(new ImageDocument(image), rect, settings with { }, SetNextCapturesAutoClose, SetExportHeaderPreference);
            window.Show();
            _ = window.CopyInitialCaptureAsync();
        };
        hotkey = new GlobalHotkeyService();
        hotkey.Pressed += StartCapture;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("範囲をキャプチャ", null, (_, _) => Dispatcher.BeginInvoke(StartCapture));
        var autoCloseMode = new Forms.ToolStripMenuItem("キャプチャを3秒で閉じる") { CheckOnClick = true, Checked = settings.AutoCloseCaptures };
        autoCloseMode.Click += (_, _) =>
        {
            try
            {
                SetNextCapturesAutoClose(autoCloseMode.Checked);
            }
            catch (Exception ex) { autoCloseMode.Checked = settings.AutoCloseCaptures; Notify("設定を保存できません: " + ex.Message); }
        };
        menu.Items.Add(autoCloseMode);
        menu.Opening += (_, _) => autoCloseMode.Checked = settings.AutoCloseCaptures;
        menu.Items.Add("設定…", null, (_, _) => Dispatcher.BeginInvoke(OpenSettings));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("すべての画像を閉じる", null, (_, _) => { foreach (var window in Windows.OfType<CaptureWindow>().ToArray()) window.Close(); });
        menu.Items.Add("終了", null, (_, _) => Shutdown());
        trayIcon = AppIcon.CreateTrayIcon();
        tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "QuickCapture", ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) Dispatcher.BeginInvoke(StartCapture); };
        try { var gesture = AppSettings.ParseShortcut(settings.GlobalShortcut); hotkey.Register(gesture.Modifiers, gesture.Key); }
        catch (Exception ex) { Notify(ex.Message); }
        if (settingsService.LoadWarning != null) Notify(settingsService.LoadWarning);
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        if (!e.Args.Contains("--background")) Dispatcher.BeginInvoke(StartCapture);
    }
    private void DisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => capture?.Cancel());
    private void SetNextCapturesAutoClose(bool enabled)
    {
        var next = settings with { AutoCloseCaptures = enabled };
        settingsService.Save(next); settings = next;
    }
    /// Right-click switching on any image: save it and apply it to every open one.
    private void SetExportHeaderPreference(bool enabled)
    {
        var next = settings with { ExportHeaderEnabled = enabled };
        settingsService.Save(next); settings = next;
        foreach (var window in Windows.OfType<CaptureWindow>()) window.SetExportHeader(enabled);
    }
    private void OpenSettings()
    {
        if (settingsWindow != null) { settingsWindow.Activate(); return; }
        settingsWindow = new SettingsWindow(settings, next =>
        {
            var gesture = AppSettings.ParseShortcut(next.GlobalShortcut);
            bool shortcutChanged = gesture != AppSettings.ParseShortcut(settings.GlobalShortcut) || !hotkey!.IsRegistered;
            if (shortcutChanged) hotkey!.Register(gesture.Modifiers, gesture.Key);
            try { settingsService.Save(next); }
            catch
            {
                if (shortcutChanged) { var previous = AppSettings.ParseShortcut(settings.GlobalShortcut); hotkey!.Register(previous.Modifiers, previous.Key); }
                throw;
            }
            settings = next;
            foreach (var window in Windows.OfType<CaptureWindow>())
            {
                window.SetExportBorder(next.ExportBorderEnabled);
                window.SetExportHeader(next.ExportHeaderEnabled);
            }
        });
        settingsWindow.Closed += (_, _) => settingsWindow = null;
        settingsWindow.Show();
    }
    private void StartCapture()
    {
        try { capture?.Start(); }
        catch (Exception ex) { Notify("キャプチャできませんでした: " + ex.Message); }
    }
    private void Notify(string text) => tray?.ShowBalloonTip(4000, "QuickCapture", text, Forms.ToolTipIcon.Info);
    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        capture?.Dispose(); hotkey?.Dispose();
        if (tray != null) { tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose(); }
        trayIcon?.Dispose();
        if (ownsMutex) mutex?.ReleaseMutex(); mutex?.Dispose();
        base.OnExit(e);
    }
}
