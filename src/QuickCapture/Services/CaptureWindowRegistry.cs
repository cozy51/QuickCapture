using System.Collections.Generic;
using System.Linq;
using System.Windows;
using QuickCapture.Windows;

namespace QuickCapture.Services;

internal static class CaptureWindowRegistry
{
    private static readonly List<CaptureWindow> windows = new();
    internal static void Register(CaptureWindow window)
    {
        if (!windows.Contains(window)) windows.Add(window);
        Refresh();
    }
    internal static void Unregister(CaptureWindow window) { windows.Remove(window); Refresh(); }
    internal static void Refresh()
    {
        var visible = windows.Where(w => w.IsVisible && w.WindowState != WindowState.Minimized).OrderBy(w => w.CapturedAt).ToArray();
        for (int i = 0; i < visible.Length; i++) visible[i].UpdateSequence(i + 1, visible.Length);
    }
}
