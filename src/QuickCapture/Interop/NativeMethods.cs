using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;

namespace QuickCapture.Interop;

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct MINMAXINFO
    {
        public POINT Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly Int32Rect Pixels => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential)] internal struct MONITORINFO
    {
        public int Size; public RECT Monitor, Work; public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct BITMAPINFO
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPels, YPels; public uint Used, Important;
    }
    internal delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref RECT rect, IntPtr data);
    [DllImport("user32.dll")] internal static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sx, int sy, uint rop);
    /// The key as the keyboard sent it, before any IME or layout translation.
    [StructLayout(LayoutKind.Sequential)] internal struct KEYBOARDHOOK
    {
        public uint Key, Scan, Flags, Time; public IntPtr Extra;
    }
    internal delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetWindowsHookEx(int type, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    /// Which window the keyboard is actually talking to, so a watch on the whole
    /// system only ever acts on keys meant for this app.
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("imm32.dll")] internal static extern bool ImmAssociateContextEx(IntPtr window, IntPtr context, uint flags);
    [DllImport("imm32.dll")] internal static extern IntPtr ImmGetContext(IntPtr window);
    [DllImport("imm32.dll")] internal static extern bool ImmReleaseContext(IntPtr window, IntPtr context);
    [DllImport("imm32.dll")] internal static extern bool ImmSetOpenStatus(IntPtr context, bool open);
    [DllImport("imm32.dll")] internal static extern bool ImmGetOpenStatus(IntPtr context);

    /// DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2. The app manifest already asks
    /// for it, but a host that starts the app with its own manifest (running
    /// `dotnet QuickCapture.dll` instead of the exe) does not carry it. Windows
    /// refuses the call, harmlessly, once the awareness is already set.
    internal static void UsePerMonitorDpi()
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
        catch (EntryPointNotFoundException) { } // Before Windows 10 1703.
    }
    /// Hand the window back the default IME context and switch the IME on. A
    /// window whose context was dropped takes plain keys and pasted text but
    /// refuses the mode key, which is what stops Japanese from being typed.
    internal static bool SetIme(IntPtr window, bool on)
    {
        if (window == IntPtr.Zero) return false;
        ImmAssociateContextEx(window, IntPtr.Zero, 0x0010); // IACE_DEFAULT
        var context = ImmGetContext(window);
        if (context == IntPtr.Zero) return false;
        try { ImmSetOpenStatus(context, on); return ImmGetOpenStatus(context) == on; }
        finally { ImmReleaseContext(window, context); }
    }
    /// True when the IME is open for this window.
    internal static bool IsImeOn(IntPtr window)
    {
        if (window == IntPtr.Zero) return false;
        var context = ImmGetContext(window);
        if (context == IntPtr.Zero) return false;
        try { return ImmGetOpenStatus(context); }
        finally { ImmReleaseContext(window, context); }
    }
    internal static Point CursorPosition()
    {
        if (!GetCursorPos(out var p)) throw new Win32Exception();
        return new Point(p.X, p.Y);
    }
    internal static List<Int32Rect> Monitors()
    {
        var monitors = new List<Int32Rect>();
        MonitorCallback callback = (IntPtr m, IntPtr dc, ref RECT r, IntPtr data) => { monitors.Add(r.Pixels); return true; };
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero)) throw new Win32Exception();
        return monitors;
    }
    internal static Int32Rect WorkArea(Point point)
    {
        var monitor = MonitorFromPoint(new POINT { X = (int)point.X, Y = (int)point.Y }, 2);
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) throw new Win32Exception();
        return info.Work.Pixels;
    }
}
