using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;

namespace QuickCapture.Services;

public sealed class ClipboardService
{
    private static int latestRequest;
    public async Task<bool> CopyAsync(BitmapSource image)
    {
        int request = Interlocked.Increment(ref latestRequest);
        // Bitmap/DIB for Office and PNG for applications that prefer encoded images.
        using var png = new MemoryStream(); ImageExportService.WritePng(image, png); png.Position = 0;
        var data = new DataObject(); data.SetImage(image); data.SetData("PNG", png);
        for (int attempt = 0; ; attempt++)
        {
            if (request != Volatile.Read(ref latestRequest)) return false;
            try { png.Position = 0; Clipboard.SetDataObject(data, true); return true; }
            catch (COMException) when (attempt < 5) { await Task.Delay(40 * (attempt + 1)); }
        }
    }
}
