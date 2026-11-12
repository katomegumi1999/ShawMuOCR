using System;
using System.Threading.Tasks;
using ShawMuOCR.Core.Capture;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Windows.ScreenCapture;
using SkiaSharp;
using Xunit;

namespace ShawMuOCR.Tests;

public class LongScreenshotAutoScrollTests
{
    private sealed class DummyScreenCaptureService : IScreenCaptureService
    {
        public OcrImage CaptureRegion(ScreenRect rect)
        {
            var bmp = new SKBitmap(100, 100);
            return new OcrImage(bmp);
        }

        public OcrImage CaptureVirtualScreen()
        {
            var bmp = new SKBitmap(100, 100);
            return new OcrImage(bmp);
        }

        public ScreenRect GetVirtualScreenBounds() => new(0, 0, 1920, 1080);
    }

    [Fact]
    public void StartAndStopAutoScroll_TogglesIsAutoScrollingCorrectly()
    {
        var capturer = new LongScreenshotCapturer(new DummyScreenCaptureService());
        var region = new ScreenRect(100, 100, 400, 600);

        Assert.False(capturer.IsAutoScrolling);

        capturer.StartAutoScroll(region);
        Assert.True(capturer.IsAutoScrolling);

        capturer.StopAutoScroll();
        Assert.False(capturer.IsAutoScrolling);
    }

    [Fact]
    public void RequestStop_StopsAutoScroll()
    {
        var capturer = new LongScreenshotCapturer(new DummyScreenCaptureService());
        var region = new ScreenRect(100, 100, 400, 600);

        capturer.StartAutoScroll(region);
        Assert.True(capturer.IsAutoScrolling);

        capturer.RequestStop();
        Assert.False(capturer.IsAutoScrolling);
    }
}
