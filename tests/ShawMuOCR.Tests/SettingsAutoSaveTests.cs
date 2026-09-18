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
}
