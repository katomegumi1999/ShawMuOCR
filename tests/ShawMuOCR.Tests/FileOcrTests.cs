using System;
using System.IO;
using System.Threading.Tasks;
using ShawMuOCR.Core.Capture;
using ShawMuOCR.Core.Clipboard;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Services;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.ViewModels;
using SkiaSharp;
using Xunit;

namespace ShawMuOCR.Tests;

public class FileOcrTests
{
    private sealed class FakeCoordinator : IOcrCoordinator
    {
        public OcrState CurrentState => OcrState.Idle;
        public OcrSession? CurrentSession => null;
        public string? StatusMessage => "就绪";

        public event Action<OcrState, string?>? StateChanged { add { } remove { } }
        public event Action<OcrSession>? SessionCompleted { add { } remove { } }

        public Action<ScreenRect>? RegionRecordingHandler { get; set; }
        public bool WasRunOcrCalled { get; private set; }
        public Task StartScreenCaptureAsync() => Task.CompletedTask;
        public Task StartCaptureImageAsync() => Task.CompletedTask;
        public Task StartAnnotateScreenshotAsync() => Task.CompletedTask;
        public Task StartLongScreenshotAsync(ScreenRect? initialRegion = null) => Task.CompletedTask;
        public Task StartTranslateSelectionAsync() => Task.CompletedTask;
        public Task StartOcrPopupAsync() => Task.CompletedTask;
        public Task RunOcrAsync(OcrImage image)
        {
            WasRunOcrCalled = true;
            return Task.CompletedTask;
        }

        public Task<OcrResult> RecognizeDirectAsync(OcrImage image)
        {
            return Task.FromResult(new OcrResult
            {
                Text = "测试文本",
                Blocks = Array.Empty<OcrTextBlock>(),
                Elapsed = TimeSpan.FromMilliseconds(50),
                IsSuccess = true
            });
        }

        public void Dispose() { }
    }

    private sealed class FakeClipboardService : IClipboardService
    {
        public string? CopiedText { get; private set; }
        public Task<bool> SetTextAsync(string text)
        {
            CopiedText = text;
            return Task.FromResult(true);
        }
        public Task<string?> GetTextAsync() => Task.FromResult(CopiedText);
        public Task<bool> SetImageAsync(OcrImage image) => Task.FromResult(true);
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Current { get; set; } = new();
        public string SettingsFilePath => "test_settings.json";
        public event Action<AppSettings>? SettingsChanged;

        public AppSettings Load() => Current;
        public void Save(AppSettings settings)
        {
            Current = settings;
            SettingsChanged?.Invoke(Current);
        }
    }

    [Fact]
    public async Task RecognizeFileAsync_NonExistentFile_SetsErrorMessage()
    {
        var coordinator = new FakeCoordinator();
        var clipboard = new FakeClipboardService();
        var settings = new FakeSettingsService();

        var vm = new MainViewModel(
            coordinator,
            clipboard,
            settings,
            () => null!);

        await vm.RecognizeFileAsync("C:\\NonExistentFile_123456789.png");

        Assert.Equal("文件不存在或路径无效", vm.StatusMessage);
    }

    [Fact]
    public async Task RecognizeFileAsync_ValidImage_TriggersOcr()
    {
        var coordinator = new FakeCoordinator();
        var clipboard = new FakeClipboardService();
        var settings = new FakeSettingsService();

        var vm = new MainViewModel(
            coordinator,
            clipboard,
            settings,
            () => null!);

        // 创建临时 PNG 图片
        string tempPng = Path.Combine(Path.GetTempPath(), $"test_ocr_{Guid.NewGuid():N}.png");
        try
        {
            using (var bmp = new SKBitmap(100, 50))
            {
                using var canvas = new SKCanvas(bmp);
                canvas.Clear(SKColors.White);
                using var image = SKImage.FromBitmap(bmp);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                using var stream = File.OpenWrite(tempPng);
                data.SaveTo(stream);
            }

            await vm.RecognizeFileAsync(tempPng);

            Assert.True(coordinator.WasRunOcrCalled);
        }
        finally
        {
            if (File.Exists(tempPng))
            {
                File.Delete(tempPng);
            }
        }
    }

    [Fact]
    public async Task RecognizeFileAsync_ValidJpegImage_NormalizesAndTriggersOcr()
    {
        var coordinator = new FakeCoordinator();
        var clipboard = new FakeClipboardService();
        var settings = new FakeSettingsService();

        var vm = new MainViewModel(
            coordinator,
            clipboard,
            settings,
            () => null!);

        // 创建临时 JPEG 图片（JPEG 解码通常采用 24 位 RGB，用于验证色彩空间规范化与预览防乱码）
        string tempJpg = Path.Combine(Path.GetTempPath(), $"test_ocr_{Guid.NewGuid():N}.jpg");
        try
        {
            using (var bmp = new SKBitmap(103, 57))
            {
                using var canvas = new SKCanvas(bmp);
                canvas.Clear(SKColors.White);
                using var image = SKImage.FromBitmap(bmp);
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
                using var stream = File.OpenWrite(tempJpg);
                data.SaveTo(stream);
            }

            await vm.RecognizeFileAsync(tempJpg);

            Assert.True(coordinator.WasRunOcrCalled);
        }
        finally
        {
            if (File.Exists(tempJpg))
            {
                File.Delete(tempJpg);
            }
        }
    }

    [Fact]
    public async Task RecognizeFileAsync_PreservesCurrentPage_DoesNotAutoJumpToOcr()
    {
        var coordinator = new FakeCoordinator();
        var clipboard = new FakeClipboardService();
        var settings = new FakeSettingsService();

        var vm = new MainViewModel(
            coordinator,
            clipboard,
            settings,
            () => null!);

        vm.CurrentPage = AppNavPage.Settings;

        string tempPng = Path.Combine(Path.GetTempPath(), $"test_ocr_{Guid.NewGuid():N}.png");
        try
        {
            using (var bmp = new SKBitmap(50, 50))
            {
                using var canvas = new SKCanvas(bmp);
                canvas.Clear(SKColors.White);
                using var image = SKImage.FromBitmap(bmp);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                using var stream = File.OpenWrite(tempPng);
                data.SaveTo(stream);
            }

            await vm.RecognizeFileAsync(tempPng);

            // 核心断言：识别后绝不自动跳转回 OCR 页，保留当前页面（如设置页）
            Assert.Equal(AppNavPage.Settings, vm.CurrentPage);
        }
        finally
        {
            if (File.Exists(tempPng))
            {
                File.Delete(tempPng);
            }
        }
    }

    [Fact]
    public void MainViewModel_PageNavigation_SwitchesLogoAndTitle()
    {
        var coordinator = new FakeCoordinator();
        var clipboard = new FakeClipboardService();
        var settings = new FakeSettingsService();

        var vm = new MainViewModel(
            coordinator,
            clipboard,
            settings,
            () => null!);

        var changedProperties = new System.Collections.Generic.List<string>();
        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName != null) changedProperties.Add(e.PropertyName);
        };

        // 默认 OCR 页面
        Assert.Equal("小沐 OCR", vm.AppTitleText);
        Assert.Equal("小沐 OCR", vm.WindowTitle);

        // 切换到剪贴板页
        changedProperties.Clear();
        vm.CurrentPage = AppNavPage.Clipboard;

        Assert.Equal("小沐剪贴板", vm.AppTitleText);
        Assert.Equal("小沐剪贴板", vm.WindowTitle);
        Assert.Contains(nameof(vm.AppTitleText), changedProperties);
        Assert.Contains(nameof(vm.WindowTitle), changedProperties);
        Assert.Contains(nameof(vm.AppLogo), changedProperties);
        Assert.Contains(nameof(vm.WindowIcon), changedProperties);

        // 切换回设置页面，自动恢复为小沐 OCR
        changedProperties.Clear();
        vm.CurrentPage = AppNavPage.Settings;

        Assert.Equal("小沐 OCR", vm.AppTitleText);
        Assert.Equal("小沐 OCR", vm.WindowTitle);
        Assert.Contains(nameof(vm.AppTitleText), changedProperties);
        Assert.Contains(nameof(vm.WindowTitle), changedProperties);
        Assert.Contains(nameof(vm.AppLogo), changedProperties);
        Assert.Contains(nameof(vm.WindowIcon), changedProperties);
    }
}

