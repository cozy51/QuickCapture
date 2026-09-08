using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickCapture.Interop;

namespace QuickCapture.Services;

public sealed class CaptureService
{
    public BitmapSource Capture(Int32Rect bounds)
    {
        IntPtr screen = NativeMethods.GetDC(IntPtr.Zero), memory = IntPtr.Zero, bitmap = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            if (screen == IntPtr.Zero) throw new Win32Exception();
            memory = NativeMethods.CreateCompatibleDC(screen);
            var info = new NativeMethods.BITMAPINFO { Size = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFO>(), Width = bounds.Width, Height = -bounds.Height, Planes = 1, BitCount = 32 };
            bitmap = NativeMethods.CreateDIBSection(screen, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (memory == IntPtr.Zero || bitmap == IntPtr.Zero) throw new Win32Exception();
            old = NativeMethods.SelectObject(memory, bitmap);
            if (!NativeMethods.BitBlt(memory, 0, 0, bounds.Width, bounds.Height, screen, bounds.X, bounds.Y, 0x40CC0020)) throw new Win32Exception();
            int stride = checked(bounds.Width * 4);
            var image = BitmapSource.Create(bounds.Width, bounds.Height, 96, 96, PixelFormats.Bgr32, null, bits, checked(stride * bounds.Height), stride);
            image.Freeze();
            return image;
        }
        finally
        {
            if (old != IntPtr.Zero) NativeMethods.SelectObject(memory, old);
            if (bitmap != IntPtr.Zero) NativeMethods.DeleteObject(bitmap);
            if (memory != IntPtr.Zero) NativeMethods.DeleteDC(memory);
            if (screen != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    // Copy only the selected pixels: a persistent CroppedBitmap would retain the entire desktop.
    public BitmapSource Crop(BitmapSource desktop, Int32Rect relative)
    {
        var output = new WriteableBitmap(relative.Width, relative.Height, 96, 96, desktop.Format, null);
        output.Lock();
        try { desktop.CopyPixels(relative, output.BackBuffer, checked(output.BackBufferStride * relative.Height), output.BackBufferStride); output.AddDirtyRect(new Int32Rect(0, 0, relative.Width, relative.Height)); }
        finally { output.Unlock(); }
        output.Freeze();
        return output;
    }
}
