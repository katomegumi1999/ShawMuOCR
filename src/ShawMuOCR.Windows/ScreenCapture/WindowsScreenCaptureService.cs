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

        IntPtr hdcMem = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr hOld = IntPtr.Zero;
        try
        {
            hdcMem = Gdi32.CreateCompatibleDC(hdcScreen);
            if (hdcMem == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to create memory device context.");
            }

            // 关键：直接建 DIB Section，让系统把像素内存映射到进程可读写地址。
            // 这样 BitBlt 的结果就躺在 ppvBits 里，省掉 CreateCompatibleBitmap 之后
            // GetDIBits 那一趟整图格式转换 + 全量拷贝（1080p 约 8MB，长截图循环里跑几十上百次）。
            var bmi = Gdi32.CreateTopDownBgraInfo(width, height);
            hBitmap = Gdi32.CreateDIBSection(
                hdcScreen,
                ref bmi,
                Gdi32.DIB_RGB_COLORS,
                out IntPtr pixels,
                IntPtr.Zero,
                0);

            if (hBitmap == IntPtr.Zero || pixels == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateDIBSection failed to allocate screenshot buffer.");
            }

            hOld = Gdi32.SelectObject(hdcMem, hBitmap);

            // 使用 BitBlt 抓取物理像素，包含透明窗口与分层窗体
            if (!Gdi32.BitBlt(
                    hdcMem,
                    0,
                    0,
                    width,
                    height,
                    hdcScreen,
                    x,
                    y,
                    Gdi32.SRCCOPY | Gdi32.CAPTUREBLT))
            {
                throw new InvalidOperationException("BitBlt failed to capture screen pixels.");
            }

            // 位图已绘制完成，先把位图从内存 DC 换出，之后即可安全销毁 DC
            Gdi32.SelectObject(hdcMem, hOld);
            hOld = IntPtr.Zero;

            // 零拷贝挂接：DIB 内存直接作为 SKBitmap 的像素存储。
            // 32bpp BGRA 无行对齐填充，行跨度就是 width * 4（不要用 width * 4 之外的猜测值）。
            var imageInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var owner = new DibSectionOwner(hBitmap);
            var skBitmap = new SKBitmap();
            if (skBitmap.InstallPixels(imageInfo, pixels, width * 4, ReleaseDibSection, owner))
            {
                // 所有权已移交 SKBitmap：hBitmap 由 ReleaseDibSection 在 Dispose 时释放
                hBitmap = IntPtr.Zero;
                return new OcrImage(skBitmap);
            }

            // 极端兜底：InstallPixels 拒绝挂接外部内存时退化为一次内存拷贝，绝不返回空图
            skBitmap.Dispose();
            var fallback = new SKBitmap(imageInfo);
            unsafe
            {
                int totalBytes = width * height * 4;
                Buffer.MemoryCopy((void*)pixels, (void*)fallback.GetPixels(), totalBytes, totalBytes);
            }
            // 幂等释放：若 Skia 在失败路径上已认领并回调过，这里不会重复 DeleteObject
            owner.Release();
            hBitmap = IntPtr.Zero;
            return new OcrImage(fallback);
        }
        finally
        {
            if (hOld != IntPtr.Zero && hdcMem != IntPtr.Zero)
            {
                Gdi32.SelectObject(hdcMem, hOld);
            }
            if (hBitmap != IntPtr.Zero)
            {
                Gdi32.DeleteObject(hBitmap);
            }
            if (hdcMem != IntPtr.Zero)
            {
                Gdi32.DeleteDC(hdcMem);
            }
            User32.ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    /// <summary>
    /// SKBitmap 释放像素时回调：像素内存属于 DIB Section，必须 DeleteObject 才能归还。
    /// 该委托从原生侧回调（NativeAOT 下同样可用，已实测），可能发生在任意线程，
    /// 即 OcrImage.Dispose 所在线程——DeleteObject 线程安全。
    /// </summary>
    private static readonly SKBitmapReleaseDelegate ReleaseDibSection = static (address, context) =>
    {
        if (context is DibSectionOwner owner)
        {
            owner.Release();
        }
    };

    /// <summary>
    /// DIB Section 句柄的幂等释放包装：句柄只会被删除一次，重复回调或重复释放都不会误删复用的句柄值。
    /// </summary>
    private sealed class DibSectionOwner
    {
        private IntPtr _handle;

        public DibSectionOwner(IntPtr handle) => _handle = handle;

        public void Release()
        {
            IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero)
            {
                Gdi32.DeleteObject(handle);
            }
        }
    }
}
