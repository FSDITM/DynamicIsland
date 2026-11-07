using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using DynamicIsland.Interop;

namespace DynamicIsland.Features;

/// <summary>Снимок одного монитора в физических пикселях.</summary>
internal sealed class MonitorShot(RECT bounds, Bitmap bitmap) : IDisposable
{
    public RECT Bounds { get; } = bounds;
    public Bitmap Bitmap { get; } = bitmap;
    private BitmapSource? _source;

    public BitmapSource Source => _source ??= ToSource(Bitmap);

    public void Dispose() => Bitmap.Dispose();

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint hObject);

    public static BitmapSource ToSource(Bitmap bmp)
    {
        var hBitmap = bmp.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(hBitmap, 0, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally { DeleteObject(hBitmap); }
    }
}

/// <summary>Захват экрана через GDI. Окно островка из кадра исключено системой (WDA_EXCLUDEFROMCAPTURE).</summary>
internal static class ScreenGrab
{
    public static List<MonitorShot> AllMonitors()
    {
        var result = new List<MonitorShot>();
        foreach (var m in Win32.GetMonitors())
        {
            var rect = m.rcMonitor;
            var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(rect.Left, rect.Top, 0, 0, new System.Drawing.Size(rect.Width, rect.Height), CopyPixelOperation.SourceCopy);
            result.Add(new MonitorShot(rect, bmp));
        }
        return result;
    }

    /// <summary>Кадр произвольной области экрана (для записи).</summary>
    public static Bitmap Region(int x, int y, int width, int height)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);
        return bmp;
    }

    public static Bitmap Crop(Bitmap source, Int32Rect rect)
    {
        var clamped = new System.Drawing.Rectangle(
            Math.Clamp(rect.X, 0, source.Width - 1), Math.Clamp(rect.Y, 0, source.Height - 1), 1, 1);
        clamped.Width = Math.Clamp(rect.Width, 1, source.Width - clamped.X);
        clamped.Height = Math.Clamp(rect.Height, 1, source.Height - clamped.Y);
        return source.Clone(clamped, source.PixelFormat);
    }

    public static byte[] ToPng(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>Не даёт окну попасть в снимки и записи (или, наоборот, разрешает).</summary>
    public static void SetCaptureVisibility(nint hwnd, bool visibleInCapture)
    {
        const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;
        SetWindowDisplayAffinity(hwnd, visibleInCapture ? WDA_NONE : WDA_EXCLUDEFROMCAPTURE);
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
}
