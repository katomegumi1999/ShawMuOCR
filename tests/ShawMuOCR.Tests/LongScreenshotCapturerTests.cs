using ShawMuOCR.Core.Capture;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Windows.ScreenCapture;
using SkiaSharp;
using Xunit;

namespace ShawMuOCR.Tests;

/// <summary>
/// 长截图采集循环的验证：关注「循环该在什么时候退出、退出后给出什么结果」，
/// 而不是像素算法本身（那是 LongScreenshotStitcherTests 的职责）。
/// <para>
/// 用脚本化的假捕获服务按预设偏移量吐出帧，因此每次滚动的真实位移已知，
/// 退出的时机也完全可控——不依赖真实屏幕，也不会因为机器性能不同而抖动。
/// </para>
/// </summary>
public class LongScreenshotCapturerTests
{
    private const int ViewportHeight = LongScreenshotTestData.ViewportHeight;
    private const int DocWidth = LongScreenshotTestData.DocWidth;

    /// <summary>把等待时间压到几十毫秒，整个类跑完仍在百毫秒量级。</summary>
    private static LongScreenshotTimings FastTimings(int maxFrames = 40, int maxConsecutiveFailures = 3) => new(
        FirstScrollTimeout: TimeSpan.FromMilliseconds(120),
        IdleStopDelay: TimeSpan.FromMilliseconds(40),
        StartupDelay: TimeSpan.Zero,
        MaxConsecutiveFailures: maxConsecutiveFailures,
        MaxFrames: maxFrames,
        TargetFrameInterval: TimeSpan.FromMilliseconds(8));

    private static ScreenRect Region => new(0, 0, DocWidth, ViewportHeight);

    // ========================= 正常拼接 =========================

    [Fact]
    public async Task Run_FollowsScriptedScroll_ProducesFullDocumentSlice()
    {
        using var document = LongScreenshotTestData.CreateDocument();
        var service = new ScriptedCaptureService(document, 0, 137, 274, 411, 548);
        var capturer = new LongScreenshotCapturer(service, FastTimings());

        var result = await capturer.RunAsync(Region);

        Assert.NotNull(result.Image);
        try
        {
            int expectedHeight = ViewportHeight + 137 * 4;
            Assert.Equal(expectedHeight, result.Height);
            Assert.True(result.FrameCount >= 5, $"帧数偏少：{result.FrameCount}");

            // 拼出来的每一行都必须与原文档逐字节一致
            byte[] expected = LongScreenshotTestData.ReadRows(document, 0, expectedHeight);
            byte[] actual = LongScreenshotTestData.ReadRows(result.Image, 0, expectedHeight);
            Assert.Equal(expected, actual);
        }
        finally
        {
            result.Image.Dispose();
        }

        Assert.False(capturer.IsRunning);
    }

    [Fact]
    public async Task Run_ScrollBack_IsIgnoredButKeepsLoopAlive()
    {
        using var document = LongScreenshotTestData.CreateDocument();
        // 往下滚 200 再回滚 50：长图高度只认净新增的 200 行
        var service = new ScriptedCaptureService(document, 0, 200, 150);
        var capturer = new LongScreenshotCapturer(service, FastTimings());

        var result = await capturer.RunAsync(Region);

        Assert.NotNull(result.Image);
        try
        {
            Assert.Equal(ViewportHeight + 200, result.Height);
            byte[] expected = LongScreenshotTestData.ReadRows(document, 0, result.Height);
            byte[] actual = LongScreenshotTestData.ReadRows(result.Image, 0, result.Height);
            Assert.Equal(expected, actual);
        }
        finally
        {
            result.Image.Dispose();
        }
    }

    // ========================= 退出条件 =========================

    [Fact]
    public async Task Run_NoScrollAtAll_TimesOutWithEmptyResult()
    {
        using var document = LongScreenshotTestData.CreateDocument();
        // 永远吐同一帧：位移恒为 0，用户根本没滚
        var service = new ScriptedCaptureService(document, 0);
        var capturer = new LongScreenshotCapturer(service, FastTimings());

        var result = await capturer.RunAsync(Region);

        Assert.Null(result.Image);
        Assert.Contains("等待滚动超时", result.Status);
        Assert.False(capturer.IsRunning);
    }

    [Fact]
    public async Task Run_ContentKeepsChanging_StopsAfterConsecutiveFailures()
    {
        using var document = LongScreenshotTestData.CreateDocument();
        // 文档帧与横条纹交替出现，模拟「窗口被切走 / 画面剧烈变化」：位移永远估不出来
        var service = new ScriptedCaptureService(document, 0, 137) { AlternateWithStripes = true };
        var capturer = new LongScreenshotCapturer(service, FastTimings(maxConsecutiveFailures: 3));

        var result = await capturer.RunAsync(Region);

        Assert.Null(result.Image);
        Assert.Contains("没有检测到有效滚动", result.Status);
        // 连续失败上限设为 3，加上首帧共 4 次捕获就该收手，不该继续空转
        Assert.True(service.CaptureCount <= 6, $"失败后没有及时退出，捕获了 {service.CaptureCount} 次");
    }

    [Fact]
    public async Task Run_RequestStop_EndsLoopImmediately()
    {
        using var document = LongScreenshotTestData.CreateDocument();
        var service = new ScriptedCaptureService(document, 0, 137, 274, 411);
        var capturer = new LongScreenshotCapturer(service, FastTimings());

        // 用捕获回调确定性地在第 3 帧触发「再按一次热键」
        service.OnCapture = count =>
        {
            if (count == 3)
            {
                capturer.RequestStop();
            }
        };

        var result = await capturer.RunAsync(Region);

        Assert.NotNull(result.Image);
        try
        {
            Assert.Equal(ViewportHeight + 137 * 2, result.Height);
        }
        finally
        {
            result.Image.Dispose();
        }

        Assert.False(capturer.IsRunning);
    }

    [Fact]
    public async Task Run_MaxFramesReached_StopsAppending()
    {
        using var document = LongScreenshotTestData.CreateDocument();
        var service = new ScriptedCaptureService(document, 0, 137, 274, 411, 548, 685);
        var capturer = new LongScreenshotCapturer(service, FastTimings(maxFrames: 3));

        var result = await capturer.RunAsync(Region);

        Assert.NotNull(result.Image);
        try
        {
            // 首帧 + 3 个追加帧
            Assert.Equal(ViewportHeight + 137 * 3, result.Height);
            Assert.Equal(4, result.FrameCount);
        }
        finally
        {
            result.Image.Dispose();
        }
    }

    // ========================= 边界与并发 =========================

    [Fact]
    public async Task Run_AlreadyRunning_ReturnsBusyWithoutStartingSecondLoop()
    {
        using var document = LongScreenshotTestData.CreateDocument();
        var service = new ScriptedCaptureService(document, 0);
        var capturer = new LongScreenshotCapturer(service, FastTimings());

        Task<LongScreenshotResult> first = capturer.RunAsync(Region);
        var second = await capturer.RunAsync(Region);

        Assert.Contains("已有长截图任务在进行中", second.Status);
        Assert.Null(second.Image);

        await first;
    }

    [Fact]
    public async Task Run_InvalidRegion_ReturnsImmediately()
    {
        using var document = LongScreenshotTestData.CreateDocument();
        var service = new ScriptedCaptureService(document, 0);
        var capturer = new LongScreenshotCapturer(service, FastTimings());

        var result = await capturer.RunAsync(new ScreenRect(0, 0, 0, 0));

        Assert.Contains("选区尺寸无效", result.Status);
        Assert.Equal(0, service.CaptureCount);
        Assert.False(capturer.IsRunning);
    }

    [Fact]
    public void Constructor_NullService_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new LongScreenshotCapturer(null!));
    }

    /// <summary>
    /// 脚本化的假捕获服务：按预设偏移量从「超长文档」上切帧，
    /// 偏移量用完后重复最后一个值——相当于用户停止滚动，画面不再变化。
    /// </summary>
    private sealed class ScriptedCaptureService : IScreenCaptureService
    {
        private readonly SKBitmap _document;
        private readonly int[] _offsets;
        private int _cursor;

        public ScriptedCaptureService(SKBitmap document, params int[] offsets)
        {
            _document = document;
            _offsets = offsets.Length > 0 ? offsets : new[] { 0 };
        }

        /// <summary>为 true 时奇数次捕获返回横条纹画面，让相邻帧永远对不上。</summary>
        public bool AlternateWithStripes { get; init; }

        /// <summary>每次捕获后回调，参数为累计捕获次数。</summary>
        public Action<int>? OnCapture { get; set; }

        public int CaptureCount { get; private set; }

        public ScreenRect GetVirtualScreenBounds() => new(0, 0, _document.Width, ViewportHeight);

        public OcrImage CaptureVirtualScreen() => CaptureRegion(GetVirtualScreenBounds());

        public OcrImage CaptureRegion(ScreenRect rect)
        {
            CaptureCount++;
            OnCapture?.Invoke(CaptureCount);

            if (AlternateWithStripes && CaptureCount % 2 == 0)
            {
                return new OcrImage(LongScreenshotTestData.CreateStripedFrame(_document.Width, rect.Height));
            }

            int offset = _offsets[Math.Min(_cursor, _offsets.Length - 1)];
            _cursor++;
            return new OcrImage(LongScreenshotTestData.CutFrame(_document, offset, rect.Height));
        }
    }
}
