using ShawMuOCR.Core.Capture;
using ShawMuOCR.Core.Clipboard;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Services;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.Services;
using SkiaSharp;
using Xunit;

namespace ShawMuOCR.Tests;

/// <summary>
/// 长截图在协调器层的接线验证：框选坐标要正确传给采集器，结果要确实进剪贴板与预览会话。
/// 像素算法与采集循环本身的正确性由 <see cref="LongScreenshotStitcherTests"/> 与
/// <see cref="LongScreenshotCapturerTests"/> 负责，这里只盯「有没有接上」。
/// <para>
/// 端到端那条用例走的是采集器的生产默认节奏（空闲 700ms 收尾），耗时由真实等待决定，
/// 因此标记为 Slow，避免拖慢快档。
/// </para>
/// </summary>
public class OcrCoordinatorLongScreenshotTests
{
    private const int RegionWidth = LongScreenshotTestData.DocWidth;
    private const int RegionHeight = LongScreenshotTestData.ViewportHeight;

    [Fact]
    public async Task StartLongScreenshotAsync_WhenCanceled_SetsIdleAndCapturesNothing()
    {
        var captureService = new ScrollCaptureService(LongScreenshotTestData.CreateDocument(), 0, 137);
        var selectionService = new RegionSelectionService(null, new ScreenRect(0, 0, RegionWidth, RegionHeight));

        using var coordinator = CreateCoordinator(captureService, selectionService);

        await coordinator.StartLongScreenshotAsync();

        Assert.Equal(OcrState.Idle, coordinator.CurrentState);
        Assert.Equal("长截图已取消", coordinator.StatusMessage);
        // 只该有「框选遮罩用的那一张全屏快照」，取消后不该进入抓帧循环
        Assert.Equal(1, captureService.VirtualCaptureCount);
        Assert.Equal(0, captureService.CaptureRegionCount);
    }

    [Fact]
    public async Task StartLongScreenshotAsync_WithEmptyInitialRegion_FallsBackToSelection()
    {
        var captureService = new ScrollCaptureService(LongScreenshotTestData.CreateDocument(), 0, 137);
        var selectionService = new RegionSelectionService(null, new ScreenRect(0, 0, RegionWidth, RegionHeight));

        using var coordinator = CreateCoordinator(captureService, selectionService);

        // 空选区应当回退到正常的框选交互流程
        await coordinator.StartLongScreenshotAsync(ScreenRect.Empty);

        Assert.Equal(1, captureService.VirtualCaptureCount);
        Assert.Equal(1, selectionService.SelectCount);
        Assert.Equal("长截图已取消", coordinator.StatusMessage);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task StartLongScreenshotAsync_WithInitialRegion_SkipsSelection()
    {
        using var document = LongScreenshotTestData.CreateDocument();
        var captureService = new ScrollCaptureService(document, 0, 137);
        var selectionService = new RegionSelectionService(null, new ScreenRect(0, 0, RegionWidth, RegionHeight));

        var clipboard = new RecordingClipboardService();
        using var coordinator = CreateCoordinator(captureService, selectionService, clipboard);

        var initial = new ScreenRect(10, 10, RegionWidth, RegionHeight);
        await coordinator.StartLongScreenshotAsync(initial);

        // 传入有效 initialRegion 时应直接抓取，完全跳过框选服务与全屏遮罩快照
        Assert.Equal(0, captureService.VirtualCaptureCount);
        Assert.Equal(0, selectionService.SelectCount);
        Assert.Equal(OcrState.Success, coordinator.CurrentState);
        Assert.NotNull(clipboard.LastImage);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task StartLongScreenshotAsync_StitchesRegionAndCopiesToClipboard()
    {
        using var document = LongScreenshotTestData.CreateDocument();
        // 首帧 + 三次 137 像素滚动，随后画面不再变化 → 采集器靠空闲检测自行收尾
        var captureService = new ScrollCaptureService(document, 0, 137, 274, 411, 548);
        var selectionService = new RegionSelectionService(
            new OcrImage(LongScreenshotTestData.CreateDocument()),
            new ScreenRect(0, 0, RegionWidth, RegionHeight));

        var clipboard = new RecordingClipboardService();
        using var coordinator = CreateCoordinator(captureService, selectionService, clipboard);

        OcrSession? completed = null;
        coordinator.SessionCompleted += session => completed = session;

        await coordinator.StartLongScreenshotAsync();

        Assert.Equal(OcrState.Success, coordinator.CurrentState);
        Assert.NotNull(clipboard.LastImage);

        int expectedHeight = RegionHeight + 137 * 4;
        Assert.Equal(expectedHeight, clipboard.LastImage!.Height);
        Assert.NotNull(completed);
        // 会话里拿到的就是同一张长图（所有权已移交会话，不在这里释放）
        Assert.Same(clipboard.LastImage, completed!.Image);
        Assert.Contains("长截图已复制到剪贴板", coordinator.StatusMessage);
    }

    private static OcrCoordinator CreateCoordinator(
        IScreenCaptureService captureService,
        IScreenSelectionService selectionService,
        IClipboardService? clipboardService = null)
    {
        var settings = new AppSettings { ShowCaptureImageNotification = false, ShowOcrNotification = false };
        return new OcrCoordinator(
            captureService,
            new MockOcrEngineFactory(),
            clipboardService ?? new RecordingClipboardService(),
            new MockSettingsService(settings),
            selectionService);
    }

    /// <summary>按预设偏移量从长图上切帧的假捕获服务；偏移量用完后重复最后一个值。</summary>
    private sealed class ScrollCaptureService : IScreenCaptureService
    {
        private readonly SKBitmap _document;
        private readonly int[] _offsets;
        private int _cursor;

        public ScrollCaptureService(SKBitmap document, params int[] offsets)
        {
            _document = document;
            _offsets = offsets.Length > 0 ? offsets : new[] { 0 };
        }

        public int CaptureRegionCount { get; private set; }

        /// <summary>框选遮罩用的那张全屏快照，不计入抓帧脚本的偏移序列。</summary>
        public int VirtualCaptureCount { get; private set; }

        public ScreenRect GetVirtualScreenBounds() => new(0, 0, RegionWidth, RegionHeight);

        public OcrImage CaptureVirtualScreen()
        {
            VirtualCaptureCount++;
            return new OcrImage(LongScreenshotTestData.CutFrame(_document, 0, RegionHeight));
        }

        public OcrImage CaptureRegion(ScreenRect rect)
        {
            CaptureRegionCount++;
            int offset = _offsets[Math.Min(_cursor, _offsets.Length - 1)];
            _cursor++;
            return new OcrImage(LongScreenshotTestData.CutFrame(_document, offset, rect.Height));
        }
    }

    /// <summary>返回固定选区坐标的框选服务；<paramref name="image"/> 为 null 表示用户按了 ESC。</summary>
    private sealed class RegionSelectionService : IScreenSelectionService
    {
        private readonly OcrImage? _image;
        private readonly ScreenRect _bounds;

        public int SelectCount { get; private set; }

        public RegionSelectionService(OcrImage? image, ScreenRect bounds)
        {
            _image = image;
            _bounds = bounds;
        }

        public Task<OcrImage?> SelectRegionAsync(OcrImage fullScreenshot, ScreenRect virtualBounds)
        {
            SelectCount++;
            return Task.FromResult(_image);
        }

        public Task<ScreenSelectionResult?> SelectRegionExAsync(OcrImage fullScreenshot, ScreenRect virtualBounds)
        {
            SelectCount++;
            if (_image is null)
            {
                return Task.FromResult<ScreenSelectionResult?>(null);
            }

            return Task.FromResult<ScreenSelectionResult?>(new ScreenSelectionResult
            {
                Image = _image,
                ScreenBounds = _bounds
            });
        }
    }

    private sealed class RecordingClipboardService : IClipboardService
    {
        public string? LastText { get; private set; }
        public OcrImage? LastImage { get; private set; }

        public Task<bool> SetTextAsync(string text)
        {
            LastText = text;
            return Task.FromResult(true);
        }

        public Task<string?> GetTextAsync() => Task.FromResult(LastText);

        public Task<bool> SetImageAsync(OcrImage image)
        {
            LastImage = image;
            return Task.FromResult(true);
        }
    }

    private sealed class MockSettingsService : ISettingsService
    {
        public MockSettingsService(AppSettings settings) => Current = settings;

        public AppSettings Current { get; set; }
        public string SettingsFilePath => "test_settings.json";
        public event Action<AppSettings>? SettingsChanged;
        public AppSettings Load() => Current;

        public void Save(AppSettings settings)
        {
            Current = settings;
            SettingsChanged?.Invoke(settings);
        }
    }

    private sealed class MockOcrEngineFactory : IOcrEngineFactory
    {
        public IOcrEngine Create(string modelName) => new MockOcrEngine(modelName);
    }

    private sealed class MockOcrEngine : IOcrEngine
    {
        public MockOcrEngine(string modelName) => ModelName = modelName;

        public string ModelName { get; }
        public bool IsLoaded => true;

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<OcrResult> RecognizeAsync(OcrImage image, CancellationToken cancellationToken = default)
            => Task.FromResult(OcrResult.Empty());

        public void Dispose() { }
    }
}
