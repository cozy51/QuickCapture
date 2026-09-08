using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Imaging;
using QuickCapture.Interop;
using QuickCapture.Models;
using QuickCapture.Windows;

namespace QuickCapture.Services;

public sealed class CaptureCoordinator : IDisposable
{
    private readonly CaptureService capture = new();
    private readonly List<CaptureOverlayWindow> overlays = new();
    private BitmapSource? desktop;
    private Int32Rect bounds;
    private Point start;
    public event Action<BitmapSource, Int32Rect>? Captured;
    public event Action<Exception>? Failed;
    public bool IsCapturing => desktop != null;
    public void Start()
    {
        if (IsCapturing) return;
        try
        {
            var monitors = NativeMethods.Monitors();
            bounds = PixelGeometry.Union(monitors);
            desktop = capture.Capture(bounds);
            foreach (var monitor in monitors)
            {
                var overlay = new CaptureOverlayWindow(desktop, bounds, monitor);
                overlay.SelectionStarted += p => { start = p; Update(p); };
                overlay.SelectionMoved += Update;
                overlay.SelectionFinished += Finish;
                overlay.PointerMoved += MovePointer;
                overlay.Cancelled += Cancel;
                overlays.Add(overlay);
                overlay.Show();
            }
            var cursor = NativeMethods.CursorPosition();
            MovePointer(cursor);
            foreach (var overlay in overlays)
                if (new Rect(overlay.Bounds.X, overlay.Bounds.Y, overlay.Bounds.Width, overlay.Bounds.Height).Contains(cursor)) { overlay.Activate(); break; }
        }
        catch { Cancel(); throw; }
    }
    private void Update(Point point)
    {
        var rect = PixelGeometry.Selection(start, point, bounds);
        foreach (var overlay in overlays) overlay.SetSelection(rect);
    }
    private void MovePointer(Point point)
    {
        foreach (var overlay in overlays) overlay.SetPointer(point);
    }
    private void Finish(Point point)
    {
        if (desktop == null) return;
        var rect = PixelGeometry.Selection(start, point, bounds);
        try
        {
            if (rect.Width < 2 || rect.Height < 2) { Cancel(); return; }
            var image = capture.Crop(desktop, new Int32Rect(rect.X - bounds.X, rect.Y - bounds.Y, rect.Width, rect.Height));
            Cancel();
            Captured?.Invoke(image, rect);
        }
        catch (Exception ex) { Failed?.Invoke(ex); }
        finally { Cancel(); }
    }
    public void Cancel()
    {
        desktop = null;
        var closing = overlays.ToArray(); overlays.Clear();
        foreach (var overlay in closing) { overlay.ReleaseImage(); overlay.Close(); }
    }
    public void Dispose() => Cancel();
}
