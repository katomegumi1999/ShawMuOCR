using ShawMuOCR.Core.Capture;
using ShawMuOCR.Core.Clipboard;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Services;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.Services;
using SkiaSharp;

namespace ShawMuOCR.Tests;

/// <summary>
/// 冷启动性能守卫：OCR 引擎（3 个 ONNX 会话，实测 250~350ms）不得在协调器构造期加载，
/// 否则主窗口要等模型加载完才可能出现。一旦有人把加载逻辑挪回构造函数，这里必须变红。
/// </summary>
public class OcrEngineLazyInitTests
{
    private sealed class CountingEngineFactory : IOcrEngineFactory
    {
        private int _createCount;

        public int CreateCount => Volatile.Read(ref _createCount);

        public IOcrEngine Create(string modelName) => Create(modelName, false, 1280);

        public IOcrEngine Create(string modelName, bool enableAngle, int maxSideLen)
        {
            Interlocked.Increment(ref _createCount);
            return new StubEngine(modelName);
        }
    }

    private sealed class StubEngine(string modelName) : IOcrEngine
    {
        public string ModelName { get; } = modelName;

        public int WarmupCount { get; private set; }

        public Task<OcrResult> RecognizeAsync(OcrImage image, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new OcrResult
            {
                Text = "lazy",
                Blocks = new[] { new OcrTextBlock { Text = "lazy", Confidence = 0.9f } },
                Elapsed = TimeSpan.FromMilliseconds(1),
                IsSuccess = true
            });
        }

        public Task WarmupAsync(CancellationToken cancellationToken = default)
        {
            WarmupCount++;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    private sealed class StubSettingsService : ISettingsService
    {
        public AppSettings Current { get; set; } = new() { Model = "stub-model", ShowWindowAfterOcr = false };
        public string SettingsFilePath => "test_settings.json";
        public event Action<AppSettings>? SettingsChanged;
        public AppSettings Load() => Current;
        public void Save(AppSettings settings)
        {
            Current = settings;
            SettingsChanged?.Invoke(settings);
        }
    }

    private sealed class StubScreenCaptureService : IScreenCaptureService
    {
        public ScreenRect GetVirtualScreenBounds() => new(0, 0, 64, 64);

        public OcrImage CaptureVirtualScreen() => new(new SKBitmap(64, 64));

        public OcrImage CaptureRegion(ScreenRect rect) => new(new SKBitmap(Math.Max(1, rect.Width), Math.Max(1, rect.Height)));
    }

    private sealed class StubClipboardService : IClipboardService
    {
        public string? LastText { get; private set; }

        public Task<bool> SetTextAsync(string text)
        {
            LastText = text;
            return Task.FromResult(true);
        }

        public Task<string?> GetTextAsync() => Task.FromResult<string?>(null);

        public Task<bool> SetImageAsync(OcrImage image) => Task.FromResult(true);
    }

    private static (OcrCoordinator Coordinator, CountingEngineFactory Factory) CreateCoordinator()
    {
        var factory = new CountingEngineFactory();
        var coordinator = new OcrCoordinator(
            new StubScreenCaptureService(),
            factory,
            new StubClipboardService(),
            new StubSettingsService());
        return (coordinator, factory);
    }

    [Fact]
    public void 构造协调器时不得加载引擎()
    {
        var (coordinator, factory) = CreateCoordinator();
        using (coordinator)
        {
            Assert.Equal(0, factory.CreateCount);
        }
    }

    [Fact]
    public async Task 首次识别时才加载引擎且并发调用只加载一次()
    {
        var (coordinator, factory) = CreateCoordinator();
        using (coordinator)
        {
            var images = Enumerable.Range(0, 4).Select(_ => new OcrImage(new SKBitmap(32, 32))).ToArray();

            var results = await Task.WhenAll(images.Select(img => coordinator.RecognizeDirectAsync(img)));

            Assert.All(results, r => Assert.True(r.IsSuccess));
            Assert.Equal(1, factory.CreateCount);
        }
    }

    [Fact]
    public async Task 后台预载会创建引擎并完成预热()
    {
        var (coordinator, factory) = CreateCoordinator();
        using (coordinator)
        {
            coordinator.PreloadEngineInBackground("测试预载");

            // 预载是异步的：等待引擎实例化完成
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (factory.CreateCount == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            Assert.Equal(1, factory.CreateCount);

            // 预载不得影响正式识别路径
            using var image = new OcrImage(new SKBitmap(32, 32));
            var result = await coordinator.RecognizeDirectAsync(image);
            Assert.True(result.IsSuccess);
            Assert.Equal(1, factory.CreateCount);
        }
    }
}
