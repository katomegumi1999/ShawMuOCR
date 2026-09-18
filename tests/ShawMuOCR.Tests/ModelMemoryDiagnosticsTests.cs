using System;
using System.Threading.Tasks;
using ShawMuOCR.Core.Clipboard;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.Services;
using ShawMuOCR.ViewModels;
using Xunit;

namespace ShawMuOCR.Tests;

public class ModelMemoryDiagnosticsTests
{
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
        public AppSettings Current { get; set; } = new AppSettings { Model = "PP-OCRv6_small" };
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
    public void GetReport_ShouldReturnValidMemoryMetrics()
    {
        var settingsService = new FakeSettingsService();
        var diag = new ModelMemoryDiagnosticsService(settingsService);

        var report = diag.GetReport();

        Assert.NotNull(report);
        Assert.True(report.PhysicalWorkingSetMb > 0, "WorkingSet should be > 0");
        Assert.True(report.PrivateMemoryMb > 0, "PrivateMemory should be > 0");
        Assert.True(report.ManagedMemoryMb > 0, "ManagedMemory should be > 0");
        Assert.Contains("PP-OCRv6", report.ModelName);
        Assert.Contains("15.9 MB", report.ModelWeightsDescription);
        Assert.False(string.IsNullOrWhiteSpace(report.ModelStatus));
        Assert.False(string.IsNullOrWhiteSpace(report.SummaryText));
    }

    [Fact]
    public void TrimMemory_ShouldExecuteWithoutException()
    {
        var settingsService = new FakeSettingsService();
        var diag = new ModelMemoryDiagnosticsService(settingsService);

        var report = diag.TrimMemory();

        Assert.NotNull(report);
        Assert.True(report.PhysicalWorkingSetMb > 0);
    }

    [Fact]
    public void AboutViewModel_InitializationAndRefresh_ShouldPopulateProperties()
    {
        var settingsService = new FakeSettingsService();
        var diag = new ModelMemoryDiagnosticsService(settingsService);
        using var vm = new AboutViewModel(diag);

        Assert.Contains("MB", vm.PhysicalWorkingSetText);
        Assert.Contains("MB", vm.PrivateMemoryText);
        Assert.Contains("MB", vm.ManagedMemoryText);
        Assert.Contains("PP-OCRv6", vm.ModelName);
        Assert.Contains("10,000", vm.GdiHandlesText);
        Assert.Equal("https://github.com/katomegumi1999/ShawMuOCR", vm.GitHubUrl);
        Assert.Equal("katomegumi1999/ShawMuOCR", vm.GitHubRepoText);
        Assert.Contains("沐洵羽", vm.AuthorText);
        Assert.Contains("B站 @沐洵羽", vm.AuthorText);
        Assert.Equal("https://space.bilibili.com/192158726", vm.BilibiliUrl);

        vm.RefreshMemory();
        Assert.Contains("✓ 已刷新", vm.StatusMessage);

        vm.TrimMemory();
        Assert.Contains("✓ 内存整理完成", vm.StatusMessage);
    }

    [Fact]
    public async Task AboutViewModel_CopyGitHubUrl_CopiesToClipboard()
    {
        var settingsService = new FakeSettingsService();
        var diag = new ModelMemoryDiagnosticsService(settingsService);
        var clipboard = new FakeClipboardService();
        using var vm = new AboutViewModel(diag, clipboard);

        await vm.CopyGitHubUrlAsync();
        Assert.Equal("https://github.com/katomegumi1999/ShawMuOCR", clipboard.CopiedText);
        Assert.Contains("已复制", vm.StatusMessage);
    }

    [Fact]
    public void AppSettings_AngleDetectionAndMaxSideLen_DefaultsAndClone()
    {
        var settings = new AppSettings();
        Assert.False(settings.EnableAngleDetection);
        Assert.Equal(1280, settings.MaxSideLen);

        settings.EnableAngleDetection = true;
        settings.MaxSideLen = 1600;

        var cloned = settings.Clone();
        Assert.True(cloned.EnableAngleDetection);
        Assert.Equal(1600, cloned.MaxSideLen);
    }

    [Fact]
    public void AppSettings_SilentStartup_DefaultsAndClone()
    {
        var settings = new AppSettings();
        Assert.False(settings.SilentStartup);

        settings.SilentStartup = true;
        var cloned = settings.Clone();
        Assert.True(cloned.SilentStartup);
    }

    [Fact]
    public void GetReport_ShouldReflectAngleAndMaxSideInEngineDescription()
    {
        var settingsService = new FakeSettingsService();
        settingsService.Current.EnableAngleDetection = false;
        settingsService.Current.MaxSideLen = 1280;

        var diag = new ModelMemoryDiagnosticsService(settingsService);
        var report = diag.GetReport();

        Assert.Contains("1280", report.EngineDescription);
        Assert.Contains("关闭", report.EngineDescription);
    }
}
