using System;
using System.Threading;
using System.Threading.Tasks;
using ShawMuOCR.Core.Capture;
using ShawMuOCR.Core.Clipboard;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Services;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.OCR;
using ShawMuOCR.Services;
using SkiaSharp;
using Xunit;

namespace ShawMuOCR.Tests;

public class IdleSleepWarmupTests
{
    private class FakeIdleSleepService : IIdleSleepService
    {
        public bool IsSleeping { get; set; }
        public int TryWakeUpCallCount { get; private set; }

        public void ReportActivity()
        {
            IsSleeping = false;
        }

        public bool TryWakeUp()
        {
            TryWakeUpCallCount++;
            if (IsSleeping)
            {
                IsSleeping = false;
                return true;
            }
            return false;
        }

        public void Dispose() { }
    }

    private class WarmupTrackingOcrEngine : IOcrEngine
    {
        public string ModelName => "WarmupTrackingModel";
        public int WarmupCallCount { get; private set; }
        public int RecognizeCallCount { get; private set; }
        public bool WarmupFinishedBeforeRecognize { get; private set; }
        private readonly int _warmupDelayMs;
        private bool _warmupCompleted;

        public WarmupTrackingOcrEngine(int warmupDelayMs = 0)
        {
            _warmupDelayMs = warmupDelayMs;
        }

        public async Task WarmupAsync(CancellationToken cancellationToken = default)
        {
            WarmupCallCount++;
            if (_warmupDelayMs > 0)
            {
                await Task.Delay(_warmupDelayMs, cancellationToken);
            }
            _warmupCompleted = true;
        }

        public Task<OcrResult> RecognizeAsync(OcrImage image, CancellationToken cancellationToken = default)
        {
            RecognizeCallCount++;
            WarmupFinishedBeforeRecognize = _warmupCompleted;

            var result = new OcrResult
            {
                Text = "Warmup Test Text",
                Blocks = Array.Empty<OcrTextBlock>(),
                Elapsed = TimeSpan.FromMilliseconds(5),
                IsSuccess = true
            };
            return Task.FromResult(result);
        }

        public void Dispose() { }
    }

    private class WarmupEngineFactory : IOcrEngineFactory
    {
        private readonly IOcrEngine _engine;
        public WarmupEngineFactory(IOcrEngine engine) => _engine = engine;
        public IOcrEngine Create(string modelName) => _engine;
    }

    private class SimpleScreenCaptureService : IScreenCaptureService
    {
        public ScreenRect GetVirtualScreenBounds() => new(0, 0, 1920, 1080);
        public OcrImage CaptureVirtualScreen() => new(new SKBitmap(100, 100));
        public OcrImage CaptureRegion(ScreenRect rect) => new(new SKBitmap(rect.Width, rect.Height));
    }

    private class SimpleScreenSelectionService : IScreenSelectionService
    {
        private readonly OcrImage? _result;
        public SimpleScreenSelectionService(OcrImage? result) => _result = result;
        public Task<OcrImage?> SelectRegionAsync(OcrImage fullScreenshot, ScreenRect virtualBounds) => Task.FromResult(_result);
    }

    private class SimpleClipboardService : IClipboardService
    {
        public Task<bool> SetTextAsync(string text) => Task.FromResult(true);
        public Task<string?> GetTextAsync() => Task.FromResult<string?>(string.Empty);
        public Task<bool> SetImageAsync(OcrImage image) => Task.FromResult(true);
    }

    private class SimpleSettingsService : ISettingsService
    {
        public AppSettings Current { get; set; } = new() { AutoCopy = false, ShowOcrNotification = false };
        public string SettingsFilePath => "settings.json";
        public event Action<AppSettings>? SettingsChanged { add { } remove { } }
        public AppSettings Load() => Current;
        public void Save(AppSettings settings) => Current = settings;
    }

    [Fact]
    public void FakeIdleSleepService_TryWakeUp_StateTransitions()
    {
        var idleService = new FakeIdleSleepService { IsSleeping = true };

        // 第一次调用返回 true，且 IsSleeping 变为 false
        Assert.True(idleService.TryWakeUp());
        Assert.False(idleService.IsSleeping);

        // 第二次调用返回 false
        Assert.False(idleService.TryWakeUp());
        Assert.Equal(2, idleService.TryWakeUpCallCount);
    }

    [Fact]
    public async Task Coordinator_WhenSleeping_TriggersWarmupAndCompletesOcr()
    {
        var engine = new WarmupTrackingOcrEngine();
        var factory = new WarmupEngineFactory(engine);
        var captureService = new SimpleScreenCaptureService();
        var selectionService = new SimpleScreenSelectionService(new OcrImage(new SKBitmap(50, 50)));
        var clipboardService = new SimpleClipboardService();
        var settingsService = new SimpleSettingsService();
        var idleService = new FakeIdleSleepService { IsSleeping = true };

        using var coordinator = new OcrCoordinator(
            captureService,
            factory,
            clipboardService,
            settingsService,
            selectionService,
            null,
            null,
            idleService);

        // 触发截屏识别流程
        await coordinator.StartScreenCaptureAsync();

        // 验证结果
        Assert.Equal(OcrState.Success, coordinator.CurrentState);
        Assert.Equal(1, idleService.TryWakeUpCallCount);
        Assert.Equal(1, engine.WarmupCallCount);
        Assert.Equal(1, engine.RecognizeCallCount);
        Assert.True(engine.WarmupFinishedBeforeRecognize);
    }

    [Fact]
    public async Task Coordinator_WhenNotSleeping_DoesNotTriggerWarmup()
    {
        var engine = new WarmupTrackingOcrEngine();
        var factory = new WarmupEngineFactory(engine);
        var captureService = new SimpleScreenCaptureService();
        var selectionService = new SimpleScreenSelectionService(new OcrImage(new SKBitmap(50, 50)));
        var clipboardService = new SimpleClipboardService();
        var settingsService = new SimpleSettingsService();
        var idleService = new FakeIdleSleepService { IsSleeping = false }; // 未处于休眠

        using var coordinator = new OcrCoordinator(
            captureService,
            factory,
            clipboardService,
            settingsService,
            selectionService,
            null,
            null,
            idleService);

        await coordinator.StartScreenCaptureAsync();

        Assert.Equal(OcrState.Success, coordinator.CurrentState);
        Assert.Equal(1, idleService.TryWakeUpCallCount);
        Assert.Equal(0, engine.WarmupCallCount); // 不应触发预热
        Assert.Equal(1, engine.RecognizeCallCount);
    }

    [Fact]
    public async Task Coordinator_WaitsForRunningWarmupBeforeRecognize()
    {
        // 模拟预热耗时 50ms
        var engine = new WarmupTrackingOcrEngine(warmupDelayMs: 50);
        var factory = new WarmupEngineFactory(engine);
        var captureService = new SimpleScreenCaptureService();
        var selectionService = new SimpleScreenSelectionService(new OcrImage(new SKBitmap(50, 50)));
        var clipboardService = new SimpleClipboardService();
        var settingsService = new SimpleSettingsService();
        var idleService = new FakeIdleSleepService { IsSleeping = true };

        using var coordinator = new OcrCoordinator(
            captureService,
            factory,
            clipboardService,
            settingsService,
            selectionService,
            null,
            null,
            idleService);

        await coordinator.StartScreenCaptureAsync();

        Assert.Equal(1, engine.WarmupCallCount);
        Assert.Equal(1, engine.RecognizeCallCount);
        // 确认即使预热有延迟，也是在预热完全完成后才开始进入正式推理
        Assert.True(engine.WarmupFinishedBeforeRecognize);
    }

    [Fact]
    public async Task PaddleOcrV6Engine_WarmupAsync_ExecutesSuccessfully()
    {
        using var engine = new PaddleOcrV6Engine(modelName: "PP-OCRv6_tiny");
        // 调用预热，验证不发生任何异常
        await engine.WarmupAsync();
        Assert.True(true);
    }
}
