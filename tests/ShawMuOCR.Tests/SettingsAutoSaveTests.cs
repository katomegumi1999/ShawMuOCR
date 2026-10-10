using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ShawMuOCR.Core.Hotkey;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.ViewModels;
using Xunit;

namespace ShawMuOCR.Tests;

public class SettingsAutoSaveTests
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

    private class FakeStartupService : IStartupService
    {
        public bool Enabled { get; private set; }
        public bool IsEnabled() => Enabled;
        public void SetEnabled(bool enabled) => Enabled = enabled;
    }

    private class FakeHotkeyService : IGlobalHotkeyService
    {
        public bool IsRegistered => true;
        public HotkeyGesture CurrentGesture => default;
        public bool IsPaused { get; set; }
        public bool Register(HotkeyGesture gesture, Action callback) => true;
        public void Unregister() { }
        public bool Register(int id, HotkeyGesture gesture, Action callback) => true;
        public void Unregister(int id) { }
        public bool IsRegisteredId(int id) => true;
        public HotkeyGesture GetGesture(int id) => default;
        public void Dispose() { }
    }

    private class FakeModelProvider : IOcrModelProvider
    {
        public string BaseModelsDirectory => "models";
        public string GetModelPath(string modelName) => string.Empty;
        public bool HasModel(string modelName) => true;
        public IReadOnlyList<string> GetAvailableModels() => ["PP-OCRv6_small", "PP-OCRv6_tiny"];
        public IReadOnlyList<string> GetAvailableModels(bool scanExternal = false) => ["PP-OCRv6_small", "PP-OCRv6_tiny"];
        public ExternalPaddleModelInfo? ResolveExternalModel(string modelName) => null;
    }

    [Fact]
    public void SettingsViewModel_ToggleAutoCopy_AutoSavesImmediately()
    {
        var settingsService = new FakeSettingsService();
        var startupService = new FakeStartupService();
        var hotkeyService = new FakeHotkeyService();
        var modelProvider = new FakeModelProvider();

        var vm = new SettingsViewModel(settingsService, startupService, hotkeyService, modelProvider);
        int initialSaveCalls = settingsService.SaveCallCount;

        // 切换开关
        vm.AutoCopy = !vm.AutoCopy;

        Assert.True(settingsService.SaveCallCount > initialSaveCalls);
        Assert.Equal(vm.AutoCopy, settingsService.Current.AutoCopy);
        Assert.Contains("自动保存", vm.StatusMessage);
    }

    [Fact]
    public void SettingsViewModel_ToggleSilentMode_AutoSavesImmediately()
    {
        var settingsService = new FakeSettingsService();
        var startupService = new FakeStartupService();
        var hotkeyService = new FakeHotkeyService();
        var modelProvider = new FakeModelProvider();

        var vm = new SettingsViewModel(settingsService, startupService, hotkeyService, modelProvider);
        int initialSaveCalls = settingsService.SaveCallCount;

        vm.SilentMode = !vm.SilentMode;

        Assert.True(settingsService.SaveCallCount > initialSaveCalls);
        Assert.Equal(vm.SilentMode, settingsService.Current.SilentMode);
    }

    [Fact]
    public void SettingsViewModel_SaveCommand_DoesNotInvokeRequestClose_PreservesSettingsPage()
    {
        var settingsService = new FakeSettingsService();
        var startupService = new FakeStartupService();
        var hotkeyService = new FakeHotkeyService();
        var modelProvider = new FakeModelProvider();

        var vm = new SettingsViewModel(settingsService, startupService, hotkeyService, modelProvider);
        bool requestCloseFired = false;
        vm.RequestClose += () => requestCloseFired = true;

        vm.SaveCommand.Execute(null);

        // 核心断言：保存设置绝不调用 RequestClose，不再跳转到 OCR 页
        Assert.False(requestCloseFired);
        Assert.Contains("✓ 设置已保存", vm.StatusMessage);
    }

    [Fact]
    public void SettingsViewModel_HotkeyRecorded_AutoSavesImmediately()
    {
        var settingsService = new FakeSettingsService();
        var startupService = new FakeStartupService();
        var hotkeyService = new FakeHotkeyService();
        var modelProvider = new FakeModelProvider();

        var vm = new SettingsViewModel(settingsService, startupService, hotkeyService, modelProvider);
        int initialSaveCalls = settingsService.SaveCallCount;

        var gesture = new HotkeyGesture(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0x51); // Ctrl+Alt+Q
        vm.OnHotkeyRecorded(gesture);

        Assert.True(settingsService.SaveCallCount > initialSaveCalls);
        Assert.Equal("Ctrl+Alt+Q", settingsService.Current.Hotkey);
        Assert.Contains("Ctrl+Alt+Q", vm.StatusMessage);
    }

    [Fact]
    public void SettingsViewModel_FlushAutoSave_SavesPendingChanges()
    {
        var settingsService = new FakeSettingsService();
        var startupService = new FakeStartupService();
        var hotkeyService = new FakeHotkeyService();
        var modelProvider = new FakeModelProvider();

        var vm = new SettingsViewModel(settingsService, startupService, hotkeyService, modelProvider);
        vm.BaiduAppId = "new_test_appid_123";

        // 主动触发 FlushAutoSave（模拟离开设置页面时的行为）
        vm.FlushAutoSave();

        Assert.Equal("new_test_appid_123", settingsService.Current.BaiduAppId);
    }

    [Fact]
    public void SettingsViewModel_ToggleEnableClipboard_DefaultsToFalseAndAutoSaves()
    {
        var settingsService = new FakeSettingsService();
        var startupService = new FakeStartupService();
        var hotkeyService = new FakeHotkeyService();
        var modelProvider = new FakeModelProvider();

        var vm = new SettingsViewModel(settingsService, startupService, hotkeyService, modelProvider);

        Assert.False(vm.EnableClipboard);
        Assert.False(settingsService.Current.EnableClipboard);

        vm.EnableClipboard = true;
        Assert.True(settingsService.Current.EnableClipboard);

        vm.EnableClipboard = false;
        Assert.False(settingsService.Current.EnableClipboard);
    }

    [Fact]
    public void SettingsViewModel_SwitchOcrBackend_DefaultsToAutoAndAutoSaves()
    {
        var settingsService = new FakeSettingsService();
        var startupService = new FakeStartupService();
        var hotkeyService = new FakeHotkeyService();
        var modelProvider = new FakeModelProvider();

        var vm = new SettingsViewModel(settingsService, startupService, hotkeyService, modelProvider);

        Assert.Equal("Auto", vm.OcrBackend);
        Assert.Equal("Auto", vm.SelectedOcrBackendOption.Value);
        Assert.Equal("Auto", settingsService.Current.OcrBackend);
        Assert.True(vm.IsAutoBackendSelected);
        Assert.Contains("自动", vm.OcrBackendHint);
        Assert.False(string.IsNullOrWhiteSpace(vm.AutoResolvedBackendText));
        Assert.False(string.IsNullOrWhiteSpace(vm.AutoResolvedReasonText));

        // 切换到 GPU (Vulkan)
        vm.SelectedOcrBackendOption = vm.AvailableOcrBackends[1];
        Assert.Equal("GPU", vm.OcrBackend);
        Assert.Equal("GPU", settingsService.Current.OcrBackend);
        Assert.False(vm.IsAutoBackendSelected);
        Assert.Contains("Vulkan", vm.OcrBackendHint);

        // 切换到 CPU
        vm.SelectedOcrBackendOption = vm.AvailableOcrBackends[2];
        Assert.Equal("CPU", vm.OcrBackend);
        Assert.Equal("CPU", settingsService.Current.OcrBackend);
        Assert.False(vm.IsAutoBackendSelected);
        Assert.Contains("CPU", vm.OcrBackendHint);
    }

    [Fact]
    public void PaddleOcrV6Engine_ParseOcrBackend_MapsValuesCorrectly()
    {
        Assert.Equal(Sdcb.SimdPaddleOCR.OcrBackend.Auto, ShawMuOCR.OCR.PaddleOcrV6Engine.ParseOcrBackend(null));
        Assert.Equal(Sdcb.SimdPaddleOCR.OcrBackend.Cpu, ShawMuOCR.OCR.PaddleOcrV6Engine.ParseOcrBackend("CPU"));
        Assert.Equal(Sdcb.SimdPaddleOCR.OcrBackend.Auto, ShawMuOCR.OCR.PaddleOcrV6Engine.ParseOcrBackend("Auto"));
        Assert.Equal(Sdcb.SimdPaddleOCR.OcrBackend.Vulkan, ShawMuOCR.OCR.PaddleOcrV6Engine.ParseOcrBackend("GPU"));
        Assert.Equal(Sdcb.SimdPaddleOCR.OcrBackend.Vulkan, ShawMuOCR.OCR.PaddleOcrV6Engine.ParseOcrBackend("Vulkan"));
    }

    [Fact]
    public void PaddleOcrV6Engine_ProbeBackendInfo_ReturnsResolvedModeAndDisplayText()
    {
        var probe = ShawMuOCR.OCR.PaddleOcrV6Engine.ProbeBackendInfo();
        Assert.NotNull(probe);
        Assert.True(probe.AutoResolvedMode is "GPU" or "CPU");
        Assert.False(string.IsNullOrWhiteSpace(probe.AutoResolvedDisplayText));
        Assert.False(string.IsNullOrWhiteSpace(probe.AutoDecisionReason));
    }

    [Fact]
    public void PaddleOcrV6Engine_EnumerateAllGpuDevices_ExecutesSafely()
    {
        var gpus = ShawMuOCR.OCR.PaddleOcrV6Engine.EnumerateAllGpuDevices();
        Assert.NotNull(gpus);
        // 不论测试机是否有 GPU 或有几块 GPU，均不应抛出异常
        foreach (var g in gpus)
        {
            Assert.True(g.Index >= 0);
            Assert.False(string.IsNullOrWhiteSpace(g.Name));
        }
    }

    [Fact]
    public void SettingsViewModel_SelectedGpuIndex_AutoSavesAndTogglesSelector()
    {
        var settingsService = new FakeSettingsService();
        var startupService = new FakeStartupService();
        var hotkeyService = new FakeHotkeyService();
        var modelProvider = new FakeModelProvider();

        var vm = new SettingsViewModel(settingsService, startupService, hotkeyService, modelProvider);
        Assert.Equal(-1, vm.SelectedGpuIndex);
        Assert.NotEmpty(vm.AvailableGpuDevices);
        Assert.Equal(-1, vm.AvailableGpuDevices[0].Index);

        int initialSaveCalls = settingsService.SaveCallCount;

        // 切换 GPU 索引
        vm.SelectedGpuIndex = 0;
        Assert.True(settingsService.SaveCallCount > initialSaveCalls);
        Assert.Equal(0, settingsService.Current.SelectedGpuIndex);

        // 只要检测到至少 1 块物理 GPU，均显示物理显卡选择器，方便用户随时查看与切换独显/核显
        bool hasAnyGpu = vm.AvailableGpuDevices.Count > 1;
        Assert.Equal(hasAnyGpu, vm.ShowGpuDeviceSelector);

        // 在纯 CPU 模式下，若用户主动手动指定某块物理显卡 (Index >= 0)，自动切换后端为 GPU
        vm.SelectedGpuIndex = -1;
        vm.OcrBackend = "CPU";
        Assert.Equal("CPU", vm.OcrBackend);
        vm.SelectedGpuIndex = 0;
        Assert.Equal("GPU", vm.OcrBackend);
    }
}
