using System;
using System.Collections.Generic;
using ShawMuOCR.Core.Hotkey;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.Services;
using ShawMuOCR.ViewModels;
using Xunit;

namespace ShawMuOCR.Tests;

public class IdleSleepServiceTests
{
    private class FakeDiagnosticsService : IModelMemoryDiagnosticsService
    {
        public int TrimCallCount { get; private set; }

        public ModelMemoryReport GetReport() => new(
            PhysicalWorkingSetMb: 100,
            PeakWorkingSetMb: 200,
            PrivateMemoryMb: 90,
            ManagedMemoryMb: 30,
            NativeEngineMemoryMb: 60,
            GdiHandles: 5,
            UserHandles: 5,
            ModelName: "TestModel",
            ModelStatus: "Ready",
            ModelWeightsDescription: "Test",
            EngineDescription: "Test",
            Timestamp: DateTime.Now);

        public ModelMemoryReport TrimMemory()
        {
            TrimCallCount++;
            return GetReport();
        }
    }

    private class FakeSettingsService : ISettingsService
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

    private class FakeStartupService : IStartupService
    {
        public bool IsEnabled() => false;
        public void SetEnabled(bool enabled) { }
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
        public string GetModelPath(string modelName) => "models/" + modelName;
        public bool HasModel(string modelName) => true;
        public IReadOnlyList<string> GetAvailableModels() => new[] { "PP-OCRv6_small" };
    }

    [Fact]
    public void AppSettings_IdleSleep_DefaultsAndClone()
    {
        var settings = new AppSettings();
        Assert.True(settings.EnableIdleSleep);
        Assert.Equal(3, settings.IdleSleepMinutes);

        settings.EnableIdleSleep = false;
        settings.IdleSleepMinutes = 10;

        var cloned = settings.Clone();
        Assert.False(cloned.EnableIdleSleep);
        Assert.Equal(10, cloned.IdleSleepMinutes);
    }

    [Fact]
    public void IdleSleepService_InitializesAndReportsActivity()
    {
        var settingsService = new FakeSettingsService();
        var diagService = new FakeDiagnosticsService();

        using var idleService = new IdleSleepService(settingsService, diagService, null);
        Assert.False(idleService.IsSleeping);

        idleService.ReportActivity();
        Assert.False(idleService.IsSleeping);
    }

    [Fact]
    public void SettingsViewModel_Includes960AndIdleSleepOptions()
    {
        var settingsService = new FakeSettingsService();
        var startupService = new FakeStartupService();
        var hotkeyService = new FakeHotkeyService();
        var modelProvider = new FakeModelProvider();

        var vm = new SettingsViewModel(settingsService, startupService, hotkeyService, modelProvider);

        Assert.Contains(960, vm.AvailableMaxSideLens);
        Assert.Contains(1024, vm.AvailableMaxSideLens);
        Assert.Contains(1280, vm.AvailableMaxSideLens);

        Assert.True(vm.EnableIdleSleep);
        Assert.Equal(3, vm.IdleSleepMinutes);
        Assert.Contains(1, vm.AvailableIdleSleepMinutes);
        Assert.Contains(3, vm.AvailableIdleSleepMinutes);
        Assert.Contains(5, vm.AvailableIdleSleepMinutes);
    }
}
