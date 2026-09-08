using System;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Interop;
using QuickCapture.Interop;

namespace QuickCapture.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    private readonly HwndSource source;
    private int currentId;
    internal static readonly uint CaptureMessage = NativeMethods.RegisterWindowMessage("QuickCapture.StartCapture.v1");
    public event Action? Pressed;
    public bool IsRegistered => currentId != 0;

    public GlobalHotkeyService()
    {
        source = new HwndSource(new HwndSourceParameters("QuickCapture hotkey") { Width = 0, Height = 0, WindowStyle = 0 });
        source.AddHook(Hook);
    }
    public void Register(ModifierKeys modifiers, Key key)
    {
        int nextId = currentId == 1 ? 2 : 1;
        if (!NativeMethods.RegisterHotKey(source.Handle, nextId, (uint)modifiers | 0x4000, (uint)KeyInterop.VirtualKeyFromKey(key)))
            throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "ショートカットを登録できません。他のアプリで使用されている可能性があります。");
        if (currentId != 0) NativeMethods.UnregisterHotKey(source.Handle, currentId);
        currentId = nextId;
    }
    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((msg == 0x0312 && wParam.ToInt32() == currentId) || (uint)msg == CaptureMessage) { handled = true; Pressed?.Invoke(); }
        return IntPtr.Zero;
    }
    public void Dispose() { if (currentId != 0) NativeMethods.UnregisterHotKey(source.Handle, currentId); source.RemoveHook(Hook); source.Dispose(); }
}
