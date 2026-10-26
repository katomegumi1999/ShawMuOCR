using System.Diagnostics;
using ShawMuOCR.Core.Capture;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Windows.ScreenCapture;
using ShawMuOCR.Windows.Win32;
using SkiaSharp;
using Xunit;

namespace ShawMuOCR.Tests;

/// <summary>
/// 屏幕捕获的 DIBSection 零拷贝路径回归测试。
/// 关注三件事：产物形状正确（尺寸/行跨度/可直接寻址）、GDI 对象不泄漏、Dispose 后内存归还。
/// 不校验具体像素内容——桌面内容随时在变，只能验证结构与生命周期。
/// </summary>
public class ScreenCaptureServiceTests
{
    private const int TestWidth = 96;
    private const int TestHeight = 64;

    private static ScreenRect SmallRect() => new(0, 0, TestWidth, TestHeight);

    [Fact]
    public void CaptureRegion_ReturnsRequestedSizeAndBgraLayout()
    {
        var service = new WindowsScreenCaptureService();

        using var image = service.CaptureRegion(SmallRect());

        Assert.Equal(TestWidth, image.Width);
        Assert.Equal(TestHeight, image.Height);
        Assert.Equal(SKColorType.Bgra8888, image.Bitmap.ColorType);
        // 32bpp 无行对齐填充，行跨度必须精确等于 width * 4，否则按行读像素会错位
        Assert.Equal(TestWidth * 4, image.Bitmap.RowBytes);
    }

    [Fact]
    public void CaptureRegion_PixelsAreDirectlyAddressable()
    {
        var service = new WindowsScreenCaptureService();

        using var image = service.CaptureRegion(SmallRect());

        Assert.NotEqual(IntPtr.Zero, image.Bitmap.GetPixels());
        // 零拷贝的关键收益：像素跨度可以直接交给 PeekPixels / GetPixelSpan 走连续内存
        using var pixmap = image.Bitmap.PeekPixels();
        Assert.NotNull(pixmap);
        Assert.Equal(TestWidth, pixmap.Width);
        Assert.Equal(TestHeight, pixmap.Height);
        Assert.True(pixmap.GetPixelSpan().Length >= TestWidth * TestHeight * 4);
    }

    [Fact]
    public void CaptureRegion_DisposeReleasesGdiObjects()
    {
        var service = new WindowsScreenCaptureService();

        // 预热一次，排除首次建 DC / 页表建立的干扰
        using (service.CaptureRegion(SmallRect())) { }

        int before = GdiObjectCount();
        for (int i = 0; i < 10; i++)
        {
            using var image = service.CaptureRegion(SmallRect());
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        int after = GdiObjectCount();

        // 允许 ±2 的抖动（GDI 计数是进程级、可能被其他测试并发影响）
        Assert.InRange(after - before, -2, 2);
    }

    [Fact]
    public void CaptureRegion_ConcurrentLiveBitmaps_GdiHandlesReturnAfterDispose()
    {
        var service = new WindowsScreenCaptureService();

        using (service.CaptureRegion(SmallRect())) { }

        int before = GdiObjectCount();
        var alive = new List<OcrImage>();
        for (int i = 0; i < 5; i++)
        {
            alive.Add(service.CaptureRegion(SmallRect()));
        }

        // 每张存活的截图都应持有一个尚未归还的 DIB Section
        Assert.InRange(GdiObjectCount() - before, 4, 10);

        foreach (var image in alive)
        {
            image.Dispose();
        }
        alive.Clear();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        Assert.InRange(GdiObjectCount() - before, -2, 2);
    }

    [Fact]
    public void CaptureRegion_SubRegion_DoesNotThrowAndKeepsSize()
    {
        var service = new WindowsScreenCaptureService();
        var rect = new ScreenRect(4, 6, 33, 21);

        using var image = service.CaptureRegion(rect);

        Assert.Equal(33, image.Width);
        Assert.Equal(21, image.Height);
        Assert.Equal(33 * 4, image.Bitmap.RowBytes);
    }

    [Fact]
    public void CaptureRegion_DownscaledTo1x1_DoesNotThrow()
    {
        var service = new WindowsScreenCaptureService();

        // 宽高为 0 时内部会钳制到 1，DIB Section 不能接受 0 尺寸
        using var image = service.CaptureRegion(new ScreenRect(0, 0, 0, 0));

        Assert.Equal(1, image.Width);
        Assert.Equal(1, image.Height);
    }

    [Fact]
    public void CaptureVirtualScreen_MatchesVirtualBounds()
    {
        var service = new WindowsScreenCaptureService();
        var bounds = service.GetVirtualScreenBounds();

        using var image = service.CaptureVirtualScreen();

        Assert.True(bounds.Width > 0);
        Assert.True(bounds.Height > 0);
        Assert.Equal(bounds.Width, image.Width);
        Assert.Equal(bounds.Height, image.Height);
    }

    [Fact]
    public void CapturedBitmap_CropProducesIndependentBitmap()
    {
        var service = new WindowsScreenCaptureService();

        using var full = service.CaptureRegion(new ScreenRect(0, 0, 64, 48));
        using var cropped = full.Crop(8, 8, 16, 16);

        Assert.Equal(16, cropped.Width);
        Assert.Equal(16, cropped.Height);
        // Crop 必须独立分配，不能与 DIB 共享内存——否则 Dispose 母图后子图变成悬垂指针
        Assert.NotEqual(full.Bitmap.GetPixels(), cropped.Bitmap.GetPixels());
        Assert.Equal(16 * 4, cropped.Bitmap.RowBytes);
    }

    private static int GdiObjectCount()
    {
        using var process = Process.GetCurrentProcess();
        return (int)User32.GetGuiResources(process.Handle, User32.GR_GDIOBJECTS);
    }
}
