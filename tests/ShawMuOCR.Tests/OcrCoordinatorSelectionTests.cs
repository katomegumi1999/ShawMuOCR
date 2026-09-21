using ShawMuOCR.Core.Capture;
using ShawMuOCR.Core.Clipboard;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Services;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.Services;
using SkiaSharp;
using Xunit;

namespace ShawMuOCR.Tests;

public class OcrCoordinatorSelectionTests
{
    private sealed class MockScreenCaptureService : IScreenCaptureService
    {
        public ScreenRect GetVirtualScreenBounds() => new(0, 0, 1920, 1080);

        public OcrImage CaptureVirtualScreen()
        {
            var bmp = new SKBitmap(100, 100);
            return new OcrImage(bmp);
        }

        public OcrImage CaptureRegion(ScreenRect rect)
        {
            var bmp = new SKBitmap(rect.Width, rect.Height);
            return new OcrImage(bmp);
        }
    }

    private sealed class MockScreenSelectionService : IScreenSelectionService
    {
        private readonly OcrImage? _resultToReturn;

        public MockScreenSelectionService(OcrImage? resultToReturn)
        {
            _resultToReturn = resultToReturn;
        }

        public Task<OcrImage?> SelectRegionAsync(OcrImage fullScreenshot, ScreenRect virtualBounds)
        {
            return Task.FromResult(_resultToReturn);
        }
    }

    private sealed class MockOcrEngineFactory : IOcrEngineFactory
    {
        public IOcrEngine Create(string modelName) => new MockOcrEngine(modelName);
    }

    private sealed class MockOcrEngine : IOcrEngine
    {
        public string ModelName { get; }
        public bool IsLoaded => true;

        public MockOcrEngine(string modelName) => ModelName = modelName;

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<OcrResult> RecognizeAsync(OcrImage image, CancellationToken cancellationToken = default)
        {
            var block = new OcrTextBlock { Text = "Hello ShawMu", Confidence = 0.99f };
            var result = new OcrResult
            {
                Text = "Hello ShawMu",
                Blocks = new[] { block },
                Elapsed = TimeSpan.FromMilliseconds(10),
                IsSuccess = true
            };
            return Task.FromResult(result);
        }

        public void Dispose() { }
    }

    private sealed class MockClipboardService : IClipboardService
    {
        public Task<string?> GetTextAsync() => Task.FromResult<string?>(null);
        public Task<bool> SetTextAsync(string text) => Task.FromResult(true);
        public Task<bool> SetImageAsync(OcrImage image)
        {
            LastImageCopied = image;
            return Task.FromResult(true);
        }
        public OcrImage? LastImageCopied { get; private set; }
    }

    private sealed class MockSettingsService : ISettingsService
    {
        public AppSettings Current { get; set; } = new() { ShowWindowAfterOcr = false };
        public string SettingsFilePath => "test_settings.json";
        public event Action<AppSettings>? SettingsChanged;
        public AppSettings Load() => Current;
        public void Save(AppSettings settings)
        {
            Current = settings;
            SettingsChanged?.Invoke(settings);
        }
    }

    [Fact]
    public async Task StartScreenCaptureAsync_WhenCanceled_SetsStateToIdle()
    {
        var captureService = new MockScreenCaptureService();
        var selectionService = new MockScreenSelectionService(null); // User pressed ESC
        var engineFactory = new MockOcrEngineFactory();
        var clipboardService = new MockClipboardService();
        var settingsService = new MockSettingsService();

        using var coordinator = new OcrCoordinator(
            captureService,
            engineFactory,
            clipboardService,
            settingsService,
            selectionService);

        await coordinator.StartScreenCaptureAsync();

        Assert.Equal(OcrState.Idle, coordinator.CurrentState);
        Assert.Equal("截屏已取消", coordinator.StatusMessage);
    }

    [Fact]
    public async Task StartScreenCaptureAsync_WhenSelected_CompletesOcrSession()
    {
        var captureService = new MockScreenCaptureService();
        var croppedBmp = new SKBitmap(50, 50);
        var croppedImage = new OcrImage(croppedBmp);
        var selectionService = new MockScreenSelectionService(croppedImage);
        var engineFactory = new MockOcrEngineFactory();
        var clipboardService = new MockClipboardService();
        var settingsService = new MockSettingsService();

        using var coordinator = new OcrCoordinator(
            captureService,
            engineFactory,
            clipboardService,
            settingsService,
            selectionService);

        OcrSession? completedSession = null;
        coordinator.SessionCompleted += session => completedSession = session;

        await coordinator.StartScreenCaptureAsync();

        Assert.Equal(OcrState.Success, coordinator.CurrentState);
        Assert.NotNull(completedSession);
        Assert.Equal("Hello ShawMu", completedSession!.Result!.Text);
    }

    [Fact]
    public async Task StartScreenCaptureAsync_WhenSilentModeTrue_DoesNotRequestShowWindow()
    {
        var captureService = new MockScreenCaptureService();
        var croppedBmp = new SKBitmap(50, 50);
        var croppedImage = new OcrImage(croppedBmp);
        var selectionService = new MockScreenSelectionService(croppedImage);
        var engineFactory = new MockOcrEngineFactory();
        var clipboardService = new MockClipboardService();
        var settingsService = new MockSettingsService
        {
            Current = new AppSettings
            {
                SilentMode = true,
                ShowWindowAfterOcr = true,
                ShowNotificationBadge = false
            }
        };

        using var coordinator = new OcrCoordinator(
            captureService,
            engineFactory,
            clipboardService,
            settingsService,
            selectionService);

        bool showRequested = false;
        coordinator.RequestShowMainWindow += () => showRequested = true;

        await coordinator.StartScreenCaptureAsync();

        Assert.Equal(OcrState.Success, coordinator.CurrentState);
        Assert.False(showRequested);
    }

    [Fact]
    public async Task StartCaptureImageAsync_CopiesImageToClipboardAndSavesSession()
    {
        var captureService = new MockScreenCaptureService();
        var croppedBmp = new SKBitmap(80, 60);
        var croppedImage = new OcrImage(croppedBmp);
        var selectionService = new MockScreenSelectionService(croppedImage);
        var engineFactory = new MockOcrEngineFactory();
        var clipboardService = new MockClipboardService();
        var settingsService = new MockSettingsService
        {
            Current = new AppSettings
            {
                ShowNotificationBadge = false
            }
        };

        using var coordinator = new OcrCoordinator(
            captureService,
            engineFactory,
            clipboardService,
            settingsService,
            selectionService);

        OcrSession? session = null;
        coordinator.SessionCompleted += s => session = s;

        await coordinator.StartCaptureImageAsync();

        Assert.Equal(OcrState.Success, coordinator.CurrentState);
        Assert.NotNull(clipboardService.LastImageCopied);
        Assert.Equal(80, clipboardService.LastImageCopied!.Width);
        Assert.Equal(60, clipboardService.LastImageCopied!.Height);
        Assert.NotNull(session);
        Assert.Equal(string.Empty, session!.Result!.Text);
        Assert.Equal(80, session!.Image!.Width);
    }

    [Fact]
    public void ToastNotificationWindow_Dismiss_ClosesActiveToastImmediately()
    {
        ShawMuOCR.Views.ToastNotificationWindow.Dismiss();
        Assert.False(ShawMuOCR.Views.ToastNotificationWindow.HasActiveToast);
    }
}
