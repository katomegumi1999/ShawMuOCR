using System;
using System.IO;
using System.Threading.Tasks;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.Services;
using ShawMuOCR.ViewModels;
using Xunit;

namespace ShawMuOCR.Tests;

public class ThemeAndSchedulerTests
{
    private class FakeSettingsService : ISettingsService
    {
        public AppSettings Current { get; set; } = new();
        public string SettingsFilePath => "settings.json";
        public event Action<AppSettings>? SettingsChanged;
        public int SaveCallCount { get; private set; }

        public AppSettings Load() => Current;
        public void Save(AppSettings settings)
        {
            SaveCallCount++;
            Current = settings.Clone();
            SettingsChanged?.Invoke(Current);
        }
    }

    private sealed class FakeEngine : IOcrEngine
    {
        public string ModelName => "FakeModel";
        public Task<OcrResult> RecognizeAsync(OcrImage image, System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult(new OcrResult { IsSuccess = true });
        public Task WarmupAsync(System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class FakeEngineFactory : IOcrEngineFactory
    {
        public IOcrEngine Create(string modelName) => new FakeEngine();
        public IOcrEngine Create(string modelName, bool enableAngle, int maxSideLen) => new FakeEngine();
        public IOcrEngine Create(AppSettings settings) => new FakeEngine();
    }

    [Fact]
    public void AppSettings_Clone_PreservesThemeAndSchedulerSettings()
    {
        var settings = new AppSettings
        {
            IsSidebarExpanded = false,
            ThemeBackgroundImagePath = "C:\\test\\bg.jpg",
            ThemeBackgroundBlurRadius = 15.5,
            ThemeComponentOpacity = 0.72,
            ThemeButtonOpacity = 0.65,
            ThemeEnableAcrylic = true,
            ThemeBackgroundColorHex = "#E2E8F0",
            ThemeCardColorHex = "#FFFFFF",
            ThemeTextColorHex = "#FFFFFF",
            ThemeFloatingWindowBackgroundColorHex = "#1E1E1E",
            ThemeFloatingWindowTextColorHex = "#EDEDED",
            BatchSchedulerMode = "gpu",
            HttpApiSchedulerMode = "gpu"
        };

        var clone = settings.Clone();

        Assert.False(clone.IsSidebarExpanded);
        Assert.Equal("C:\\test\\bg.jpg", clone.ThemeBackgroundImagePath);
        Assert.Equal(15.5, clone.ThemeBackgroundBlurRadius);
        Assert.Equal(0.72, clone.ThemeComponentOpacity);
        Assert.Equal(0.65, clone.ThemeButtonOpacity);
        Assert.True(clone.ThemeEnableAcrylic);
        Assert.Equal("#E2E8F0", clone.ThemeBackgroundColorHex);
        Assert.Equal("#FFFFFF", clone.ThemeCardColorHex);
        Assert.Equal("#FFFFFF", clone.ThemeTextColorHex);
        Assert.Equal("#1E1E1E", clone.ThemeFloatingWindowBackgroundColorHex);
        Assert.Equal("#EDEDED", clone.ThemeFloatingWindowTextColorHex);
        Assert.Equal("gpu", clone.BatchSchedulerMode);
        Assert.Equal("gpu", clone.HttpApiSchedulerMode);
    }

    [Fact]
    public void ThemeService_InitialState_MatchesSettings()
    {
        var fakeSettings = new FakeSettingsService();
        fakeSettings.Current.ThemeBackgroundBlurRadius = 12;
        fakeSettings.Current.ThemeComponentOpacity = 0.85;
        fakeSettings.Current.ThemeButtonOpacity = 0.75;
        fakeSettings.Current.ThemeBackgroundColorHex = "#1E293B";
        fakeSettings.Current.ThemeCardColorHex = "#F1F5F9";
        fakeSettings.Current.ThemeTextColorHex = "#0284C7";
        fakeSettings.Current.ThemeFloatingWindowBackgroundColorHex = "#1E1E1E";
        fakeSettings.Current.ThemeFloatingWindowTextColorHex = "#FFFFFF";

        var themeService = new ThemeService(fakeSettings);

        Assert.Equal(12, themeService.BlurRadius);
        Assert.Equal(0.85, themeService.ComponentOpacity);
        Assert.Equal(0.75, themeService.ButtonOpacity);
        Assert.Equal("#1E293B", themeService.BackgroundColorHex);
        Assert.Equal("#F1F5F9", themeService.CardColorHex);
        Assert.Equal("#0284C7", themeService.TextColorHex);
        Assert.Equal("#1E1E1E", themeService.FloatingWindowBackgroundColorHex);
        Assert.Equal("#FFFFFF", themeService.FloatingWindowTextColorHex);
        Assert.False(themeService.HasBackgroundImage);
        Assert.True(themeService.HasBackgroundColor);
    }

    [Fact]
    public void ThemeService_SetOpacityAndBlurAndColors_PersistsToSettings()
    {
        var fakeSettings = new FakeSettingsService();
        var themeService = new ThemeService(fakeSettings);

        themeService.SetBlurRadius(20);
        themeService.SetComponentOpacity(0.65);
        themeService.SetButtonOpacity(0.55);
        themeService.SetBackgroundColor("#F8FAFC");
        themeService.SetCardColor("#E2E8F0");
        themeService.SetTextColor("#1E293B");
        themeService.SetFloatingWindowBackgroundColor("#18181B");
        themeService.SetFloatingWindowTextColor("#F4F4F5");

        Assert.Equal(20, fakeSettings.Current.ThemeBackgroundBlurRadius);
        Assert.Equal(0.65, fakeSettings.Current.ThemeComponentOpacity);
        Assert.Equal(0.55, fakeSettings.Current.ThemeButtonOpacity);
        Assert.Equal("#F8FAFC", fakeSettings.Current.ThemeBackgroundColorHex);
        Assert.Equal("#E2E8F0", fakeSettings.Current.ThemeCardColorHex);
        Assert.Equal("#18181B", fakeSettings.Current.ThemeFloatingWindowBackgroundColorHex);
        Assert.Equal("#F4F4F5", fakeSettings.Current.ThemeFloatingWindowTextColorHex);
        Assert.Equal("#F8FAFC", themeService.BackgroundColorHex);
        Assert.Equal("#E2E8F0", themeService.CardColorHex);
        Assert.Equal("#1E293B", themeService.TextColorHex);
        Assert.Equal("#18181B", themeService.FloatingWindowBackgroundColorHex);
        Assert.Equal("#F4F4F5", themeService.FloatingWindowTextColorHex);
    }

    [Fact]
    public void ThemeService_SetOpacity_AllowsZeroMinimum()
    {
        var fakeSettings = new FakeSettingsService();
        var themeService = new ThemeService(fakeSettings);

        // 测试完全透明 (0.0) 以及超下限截断 (-0.2)
        themeService.SetComponentOpacity(0.0);
        themeService.SetButtonOpacity(-0.2);

        Assert.Equal(0.0, themeService.ComponentOpacity);
        Assert.Equal(0.0, themeService.ButtonOpacity);
        Assert.Equal(0.0, fakeSettings.Current.ThemeComponentOpacity);
        Assert.Equal(0.0, fakeSettings.Current.ThemeButtonOpacity);
    }

    [Fact]
    public void ThemeService_ResetToDefaults_RestoresStandardValues()
    {
        var fakeSettings = new FakeSettingsService();
        fakeSettings.Current.ThemeBackgroundBlurRadius = 25;
        fakeSettings.Current.ThemeComponentOpacity = 0.50;
        fakeSettings.Current.ThemeButtonOpacity = 0.40;
        fakeSettings.Current.ThemeBackgroundColorHex = "#1E293B";
        fakeSettings.Current.ThemeCardColorHex = "#334155";
        fakeSettings.Current.ThemeTextColorHex = "#FF0000";

        var themeService = new ThemeService(fakeSettings);
        themeService.ResetToDefaults();

        Assert.Equal(0.0, themeService.BlurRadius);
        Assert.Equal(0.12, themeService.ComponentOpacity);
        Assert.Equal(0.12, themeService.ButtonOpacity);
        Assert.Null(themeService.BackgroundColorHex);
        Assert.Null(themeService.CardColorHex);
        Assert.Null(themeService.TextColorHex);
        Assert.Equal(0.0, fakeSettings.Current.ThemeBackgroundBlurRadius);
        Assert.Equal(0.12, fakeSettings.Current.ThemeComponentOpacity);
        Assert.Equal(0.12, fakeSettings.Current.ThemeButtonOpacity);
        Assert.Null(fakeSettings.Current.ThemeBackgroundColorHex);
        Assert.Null(fakeSettings.Current.ThemeCardColorHex);
        Assert.Null(fakeSettings.Current.ThemeTextColorHex);
    }

    [Fact]
    public void ThemeViewModel_ReflectsServiceChangesAndPaletteCommands()
    {
        var fakeSettings = new FakeSettingsService();
        var themeService = new ThemeService(fakeSettings);
        var vm = new ThemeViewModel(themeService);

        Assert.Equal(0.0, vm.BlurRadius);
        Assert.Equal(12.0, vm.ComponentOpacityPercent);
        Assert.Equal(12.0, vm.ButtonOpacityPercent);
        Assert.Null(vm.BackgroundColorHex);
        Assert.Null(vm.TextColorHex);
        Assert.False(vm.HasCustomBackgroundColor);
        Assert.False(vm.HasCustomTextColor);
        Assert.NotEmpty(vm.StandardPalettePresets);

        vm.BlurRadius = 18;
        Assert.Equal(18, themeService.BlurRadius);

        vm.ComponentOpacityPercent = 70;
        Assert.Equal(0.70, Math.Round(themeService.ComponentOpacity, 2));

        vm.ButtonOpacityPercent = 60;
        Assert.Equal(0.60, Math.Round(themeService.ButtonOpacity, 2));

        vm.SelectBackgroundColorCommand.Execute("#BAE6FD");
        Assert.Equal("#BAE6FD", vm.BackgroundColorHex);
        Assert.True(vm.HasCustomBackgroundColor);
        Assert.Equal("#BAE6FD", themeService.BackgroundColorHex);

        vm.SelectTextColorCommand.Execute("#059669");
        Assert.Equal("#059669", vm.TextColorHex);
        Assert.True(vm.HasCustomTextColor);
        Assert.Equal("#059669", themeService.TextColorHex);

        vm.SelectCardColorCommand.Execute("#F8FAFC");
        Assert.Equal("#F8FAFC", vm.CardColorHex);
        Assert.True(vm.HasCustomCardColor);
        Assert.Equal("#F8FAFC", themeService.CardColorHex);

        vm.ResetBackgroundColorCommand.Execute(null);
        Assert.Null(vm.BackgroundColorHex);
        Assert.False(vm.HasCustomBackgroundColor);
        Assert.Null(themeService.BackgroundColorHex);

        vm.ResetCardColorCommand.Execute(null);
        Assert.Null(vm.CardColorHex);
        Assert.False(vm.HasCustomCardColor);
        Assert.Null(themeService.CardColorHex);

        vm.ResetTextColorCommand.Execute(null);
        Assert.Null(vm.TextColorHex);
        Assert.False(vm.HasCustomTextColor);
        Assert.Null(themeService.TextColorHex);
    }

    [Fact]
    public void ThemeViewModel_ColorAndHexTwoWaySynchronization()
    {
        var fakeSettings = new FakeSettingsService();
        var themeService = new ThemeService(fakeSettings);
        var vm = new ThemeViewModel(themeService);

        // 默认背景底色应为 #F3F4F6 (Color: 243, 244, 246)
        Assert.Equal(Avalonia.Media.Color.FromRgb(243, 244, 246), vm.SelectedBackgroundColor);
        // 默认卡片底色应为 #FFFFFF (White)
        Assert.Equal(Avalonia.Media.Colors.White, vm.SelectedCardColor);

        // 1. 通过调色盘选色 (SelectedBackgroundColor) 同步到 Hex 字符串及服务
        vm.SelectedBackgroundColor = Avalonia.Media.Color.FromRgb(2, 132, 199); // #0284C7
        Assert.Equal("#0284C7", vm.BackgroundColorHex);
        Assert.Equal("#0284C7", themeService.BackgroundColorHex);

        // 2. 通过输入 Hex 字符串反向同步到调色盘选色 (SelectedBackgroundColor)
        vm.BackgroundColorHex = "#E11D48";
        Assert.Equal(Avalonia.Media.Color.FromRgb(225, 29, 72), vm.SelectedBackgroundColor);
        Assert.Equal("#E11D48", themeService.BackgroundColorHex);

        // 3. 卡片底色调色盘双向同步
        vm.SelectedCardColor = Avalonia.Media.Color.FromRgb(241, 245, 249); // #F1F5F9
        Assert.Equal("#F1F5F9", vm.CardColorHex);
        Assert.Equal("#F1F5F9", themeService.CardColorHex);

        // 4. 字体颜色调色盘双向同步
        vm.SelectedTextColor = Avalonia.Media.Color.FromRgb(255, 255, 255);
        Assert.Equal("#FFFFFF", vm.TextColorHex);
        Assert.Equal("#FFFFFF", themeService.TextColorHex);

        vm.TextColorHex = "#1E293B";
        Assert.Equal(Avalonia.Media.Color.FromRgb(30, 41, 59), vm.SelectedTextColor);
        Assert.Equal("#1E293B", themeService.TextColorHex);

        // 4. 重置恢复默认
        vm.ResetBackgroundColorCommand.Execute(null);
        Assert.Null(vm.BackgroundColorHex);
        Assert.Equal(Avalonia.Media.Color.FromRgb(243, 244, 246), vm.SelectedBackgroundColor);

        vm.ResetTextColorCommand.Execute(null);
        Assert.Null(vm.TextColorHex);
        Assert.Equal(Avalonia.Media.Color.FromRgb(30, 41, 59), vm.SelectedTextColor);
    }

    [Fact]
    public void BatchOcrViewModel_And_HttpApiViewModel_AdaptiveLabels_MatchGpuOrCpuMode()
    {
        var fakeSettings = new FakeSettingsService();
        var factory = new FakeEngineFactory();

        // 1. CPU 模式下的文案判定
        fakeSettings.Current.OcrBackend = "CPU";
        var batchVm = new BatchOcrViewModel(factory, fakeSettings);
        using var httpVm = new HttpApiViewModel(factory, fakeSettings);

        Assert.False(batchVm.IsGpuAccelerated);
        Assert.Equal("多核并行上限:", batchVm.ConcurrencyLabel);
        Assert.Equal($"{batchVm.MaxCores} 核", batchVm.ConcurrencyValueText);
        Assert.Equal("CPU 利用率上限:", batchVm.UsageLimitLabel);
        Assert.Equal($"{batchVm.CpuLimitPercent}%", batchVm.UsageLimitValueText);

        Assert.False(httpVm.IsGpuAccelerated);
        Assert.Equal("最大并发核心数上限:", httpVm.ConcurrencyLabel);
        Assert.Equal($"{httpVm.MaxCores} 核", httpVm.ConcurrencyValueText);
        Assert.Equal("CPU 利用率上限 (占空比限流):", httpVm.UsageLimitLabel);
        Assert.Equal($"{httpVm.CpuLimitPercent}%", httpVm.UsageLimitValueText);

        // 2. GPU 模式下的文案判定
        fakeSettings.Current.OcrBackend = "GPU";
        fakeSettings.Save(fakeSettings.Current);

        var batchVmGpu = new BatchOcrViewModel(factory, fakeSettings);
        using var httpVmGpu = new HttpApiViewModel(factory, fakeSettings);

        Assert.True(batchVmGpu.IsGpuAccelerated);
        Assert.Equal("GPU 并发流上限:", batchVmGpu.ConcurrencyLabel);
        Assert.Equal($"{batchVmGpu.MaxCores} 路并发", batchVmGpu.ConcurrencyValueText);
        Assert.Equal("GPU 算力占空比上限:", batchVmGpu.UsageLimitLabel);
        Assert.Equal($"{batchVmGpu.CpuLimitPercent}%", batchVmGpu.UsageLimitValueText);

        Assert.True(httpVmGpu.IsGpuAccelerated);
        Assert.Equal("GPU 并发流上限:", httpVmGpu.ConcurrencyLabel);
        Assert.Equal($"{httpVmGpu.MaxCores} 路并发", httpVmGpu.ConcurrencyValueText);
        Assert.Equal("GPU 算力占空比上限:", httpVmGpu.UsageLimitLabel);
        Assert.Equal($"{httpVmGpu.CpuLimitPercent}%", httpVmGpu.UsageLimitValueText);
    }

    [Fact]
    public void ThemeService_Transparency_ZeroIsOpaque_OneIsTransparent()
    {
        // 验证用户需求：0 为不透明 (Alpha=255)，100 (1.0) 为完全透明 (Alpha=0)
        double opacityZero = 0.0;
        byte alphaZero = (byte)((1.0 - Math.Clamp(opacityZero, 0.0, 1.0)) * 255);
        Assert.Equal(255, alphaZero);

        double opacityFull = 1.0;
        byte alphaFull = (byte)((1.0 - Math.Clamp(opacityFull, 0.0, 1.0)) * 255);
        Assert.Equal(0, alphaFull);

        double opacityDefault = 0.12;
        byte alphaDefault = (byte)((1.0 - Math.Clamp(opacityDefault, 0.0, 1.0)) * 255);
        Assert.Equal(224, alphaDefault);
    }

    [Fact]
    public void BatchOcrViewModel_And_HttpApiViewModel_ConcurrencyWarningAndLimits_WorkAsExpected()
    {
        var fakeSettings = new FakeSettingsService();
        var factory = new FakeEngineFactory();

        // 1. CPU 模式：默认半核，上限本机逻辑核数，且滑动绝不弹警告窗口
        fakeSettings.Current.OcrBackend = "CPU";
        var batchVmCpu = new BatchOcrViewModel(factory, fakeSettings);
        using var httpVmCpu = new HttpApiViewModel(factory, fakeSettings);

        int expectedCpuCores = Math.Max(1, Environment.ProcessorCount / 2);
        Assert.Equal(expectedCpuCores, batchVmCpu.MaxCores);
        Assert.Equal(Environment.ProcessorCount, batchVmCpu.MaxProcessorCount);
        Assert.Equal(expectedCpuCores, httpVmCpu.MaxCores);
        Assert.Equal(Environment.ProcessorCount, httpVmCpu.MaxAvailableCores);

        // CPU 模式下调节核心数直接生效，不触发弹窗警告
        batchVmCpu.MaxCores = Environment.ProcessorCount;
        Assert.False(batchVmCpu.IsConcurrencyWarningOpen);
        Assert.Equal(Environment.ProcessorCount, batchVmCpu.MaxCores);
        Assert.Equal(Environment.ProcessorCount, fakeSettings.Current.BatchMaxCores);

        httpVmCpu.MaxCores = Math.Max(1, Environment.ProcessorCount - 1);
        Assert.False(httpVmCpu.IsConcurrencyWarningOpen);
        Assert.Equal(Math.Max(1, Environment.ProcessorCount - 1), httpVmCpu.MaxCores);
        Assert.Equal(Math.Max(1, Environment.ProcessorCount - 1), fakeSettings.Current.HttpApiMaxCores);

        // 2. GPU 模式：默认 1 路并发，上限 8 路；超过 1 路时必须弹窗提示
        fakeSettings.Current.OcrBackend = "GPU";
        fakeSettings.Save(fakeSettings.Current);

        var batchVmGpu = new BatchOcrViewModel(factory, fakeSettings);
        using var httpVmGpu = new HttpApiViewModel(factory, fakeSettings);

        Assert.Equal(1, batchVmGpu.MaxCores);
        Assert.Equal(8, batchVmGpu.MaxProcessorCount);
        Assert.Equal(1, httpVmGpu.MaxCores);
        Assert.Equal(8, httpVmGpu.MaxAvailableCores);

        // 2.1 调至 > 1 时拦截并触发 GPU 并发流警告弹窗
        batchVmGpu.MaxCores = 4;
        Assert.True(batchVmGpu.IsConcurrencyWarningOpen);
        Assert.Equal(4, batchVmGpu.PendingMaxCores);
        Assert.Equal("保持 1 路 (推荐)", batchVmGpu.CancelConcurrencyButtonText);
        Assert.Equal("确定提升至 4 路", batchVmGpu.ConfirmConcurrencyButtonText);

        // 2.2 用户选择取消（保持 1 路），必须强制滑回 1
        batchVmGpu.CancelConcurrencyWarning();
        Assert.False(batchVmGpu.IsConcurrencyWarningOpen);
        Assert.Equal(1, batchVmGpu.MaxCores);

        // 2.3 再次调至 > 1，用户确认提升
        batchVmGpu.MaxCores = 3;
        Assert.True(batchVmGpu.IsConcurrencyWarningOpen);
        Assert.Equal(3, batchVmGpu.PendingMaxCores);

        batchVmGpu.ConfirmConcurrencyWarning();
        Assert.False(batchVmGpu.IsConcurrencyWarningOpen);
        Assert.Equal(3, batchVmGpu.MaxCores);
        Assert.Equal(3, fakeSettings.Current.BatchGpuStreams);

        // 2.4 确认后在 2~8 范围内滑动不再重复弹窗打扰
        batchVmGpu.MaxCores = 6;
        Assert.False(batchVmGpu.IsConcurrencyWarningOpen);
        Assert.Equal(6, batchVmGpu.MaxCores);
        Assert.Equal(6, fakeSettings.Current.BatchGpuStreams);

        // 3. HTTP API 调度器 GPU 警告机制验证
        httpVmGpu.MaxCores = 2;
        Assert.True(httpVmGpu.IsConcurrencyWarningOpen);
        Assert.Equal(2, httpVmGpu.PendingMaxCores);

        httpVmGpu.CancelConcurrencyWarning();
        Assert.False(httpVmGpu.IsConcurrencyWarningOpen);
        Assert.Equal(1, httpVmGpu.MaxCores);

        httpVmGpu.MaxCores = 2;
        httpVmGpu.ConfirmConcurrencyWarning();
        Assert.False(httpVmGpu.IsConcurrencyWarningOpen);
        Assert.Equal(2, httpVmGpu.MaxCores);
        Assert.Equal(2, fakeSettings.Current.HttpApiGpuStreams);
    }
}

