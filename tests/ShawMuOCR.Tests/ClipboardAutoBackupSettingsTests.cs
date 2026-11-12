using System;
using System.Collections.Generic;
using ShawMuOCR.Core.Hotkey;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.ViewModels;
using Xunit;

namespace ShawMuOCR.Tests;

public class ClipboardAutoBackupSettingsTests
{
    private sealed class FakeSettingsService : ISettingsService
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
        public void Reset() { Current = new AppSettings(); }
    }

    private sealed class FakeStartupService : IStartupService
    {
        public bool Enabled { get; private set; }
        public bool IsEnabled() => Enabled;
        public void SetEnabled(bool enabled) => Enabled = enabled;
    }

    private sealed class FakeHotkeyService : IGlobalHotkeyService
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

    private sealed class FakeModelProvider : IOcrModelProvider
    {
        public string BaseModelsDirectory => "models";
        public IReadOnlyList<string> GetAvailableModels() => new[] { "v6_mobile" };
        public IReadOnlyList<string> GetEmbeddedModels() => new[] { "v6_mobile" };
        public string GetModelPath(string modelName) => modelName;
        public bool IsModelAvailable(string modelName) => true;
        public bool HasModel(string modelName) => true;
    }

    [Fact]
    public void DefaultSettings_HasExpectedBackupIntervalAndKeep()
    {
        var settings = new AppSettings();
        Assert.Equal(7, settings.ClipboardAutoBackupKeep);
        Assert.Equal(24, settings.ClipboardAutoBackupIntervalHours);
    }

    [Fact]
    public void SettingsViewModel_LoadsAndSavesBackupSettings()
    {
        var settingsService = new FakeSettingsService();
        settingsService.Current.ClipboardAutoBackupKeep = 14;
        settingsService.Current.ClipboardAutoBackupIntervalHours = 12;

        var vm = new SettingsViewModel(
            settingsService,
            new FakeStartupService(),
            new FakeHotkeyService(),
            new FakeModelProvider());

        Assert.Equal(14, vm.ClipboardAutoBackupKeep);
        Assert.Equal(12, vm.ClipboardAutoBackupIntervalHours);
        Assert.Contains(14, vm.AvailableClipboardAutoBackupKeeps);
        Assert.Contains(12, vm.AvailableClipboardAutoBackupIntervalHours);

        // 修改属性并保存
        vm.ClipboardAutoBackupKeep = 30;
        vm.ClipboardAutoBackupIntervalHours = 72;
        vm.FlushAutoSave();

        Assert.Equal(30, settingsService.Current.ClipboardAutoBackupKeep);
        Assert.Equal(72, settingsService.Current.ClipboardAutoBackupIntervalHours);
    }
}
