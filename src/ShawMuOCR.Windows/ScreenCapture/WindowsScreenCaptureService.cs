using System.Runtime.InteropServices;
using ShawMuOCR.Core.Capture;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Windows.Win32;
using SkiaSharp;

namespace ShawMuOCR.Windows.ScreenCapture;

public sealed class WindowsScreenCaptureService : IScreenCaptureService
{
    public ScreenRect GetVirtualScreenBounds()
    {
        try
        {
            User32.SetThreadDpiAwarenessContext(User32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch { }

        int x = User32.GetSystemMetrics(User32.SM_XVIRTUALSCREEN);
        int y = User32.GetSystemMetrics(User32.SM_YVIRTUALSCREEN);
        int w = User32.GetSystemMetrics(User32.SM_CXVIRTUALSCREEN);
        int h = User32.GetSystemMetrics(User32.SM_CYVIRTUALSCREEN);

        if (w <= 0 || h <= 0)
        {
            // 回退到主显示器
            w = User32.GetSystemMetrics(0); // SM_CXSCREEN
            h = User32.GetSystemMetrics(1); // SM_CYSCREEN
            x = 0;
            y = 0;
        }

        return new ScreenRect(x, y, w, h);
    }

    public OcrImage CaptureVirtualScreen()
    {
        var bounds = GetVirtualScreenBounds();
        return CaptureInternal(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    public OcrImage CaptureRegion(ScreenRect rect)
    {
        return CaptureInternal(rect.X, rect.Y, rect.Width, rect.Height);
    }

    private static OcrImage CaptureInternal(int x, int y, int width, int height)
    {
        try
        {
            User32.SetThreadDpiAwarenessContext(User32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch { }

        if (width <= 0) width = 1;
        if (height <= 0) height = 1;

        IntPtr hdcScreen = User32.GetDC(IntPtr.Zero);
        if (hdcScreen == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to get desktop device context.");
        }

        IntPtr hdcMem = Gdi32.CreateCompatibleDC(hdcScreen);
        IntPtr hBitmap = Gdi32.CreateCompatibleBitmap(hdcScreen, width, height);
        IntPtr hOld = Gdi32.SelectObject(hdcMem, hBitmap);

        try
        {
            // 使用 BitBlt 抓取物理像素，包含透明窗口与分层窗体
            Gdi32.BitBlt(
                hdcMem,
                0,
                0,
                width,
                height,
                hdcScreen,
                x,
                y,
                Gdi32.SRCCOPY | Gdi32.CAPTUREBLT);

            // 设置自顶向下的 32 位 BGRA 位图信息头
            var bmi = new Gdi32.BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<Gdi32.BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = width;
            bmi.bmiHeader.biHeight = -height; // 负数表示自顶向下，避免图像上下颠倒
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = (uint)Gdi32.BI_RGB;

            var imageInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var skBitmap = new SKBitmap(imageInfo);

            int lines = Gdi32.GetDIBits(
                hdcMem,
                hBitmap,
                0,
                (uint)height,
                skBitmap.GetPixels(),
                ref bmi,
                Gdi32.DIB_RGB_COLORS);

            if (lines == 0)
            {
                skBitmap.Dispose();
                throw new InvalidOperationException("GetDIBits failed to extract screenshot bits.");
            }

            return new OcrImage(skBitmap);
        }
        finally
        {
            Gdi32.SelectObject(hdcMem, hOld);
            Gdi32.DeleteObject(hBitmap);
            Gdi32.DeleteDC(hdcMem);
            User32.ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }
}
