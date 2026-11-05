using System;
using System.Collections.Generic;
using System.Text;
using ShawMuOCR.Core.Hotkey;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.ViewModels;
using Xunit;

namespace ShawMuOCR.Tests;

/// <summary>
/// 设置页「本地数据库加密状态」的判定逻辑。
/// 这条显示存在的意义是让用户能自己确认库到底有没有被加密，所以两条路径都要有测试兜住：
/// 核心可用时以核心实测为准（权威），核心不可用时退回读文件头（不能显示成「已加密」骗人）。
/// </summary>
public class MuClipEncryptionStatusTests
{
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

    private static SettingsViewModel CreateViewModel(Func<bool?>? probe)
    {
        return new SettingsViewModel(
            new FakeSettingsService(),
            new FakeStartupService(),
            new FakeHotkeyService(),
            new FakeModelProvider(),
            aboutVmFactory: null,
            loggerService: null,
            muClipEncryptionProbe: probe);
    }

    [Fact]
    public void IsPlaintextSqliteHeader_RecognizesSqiteMagic()
    {
        byte[] head = "SQLite format 3\0"u8.ToArray();

        Assert.True(SettingsViewModel.IsPlaintextSqliteHeader(head));
    }

    [Fact]
    public void IsPlaintextSqliteHeader_RejectsCiphertext()
    {
        // 真实 SQLCipher 库的文件头是随机密文，取一段确定性字节即可代表。
        byte[] head = { 0xE2, 0xCF, 0xBD, 0x6F, 0x46, 0x00, 0x55, 0xCB, 0xC2, 0x57, 0x1A, 0x7A, 0x64, 0x44, 0x62, 0x5C };

        Assert.False(SettingsViewModel.IsPlaintextSqliteHeader(head));
    }

    [Fact]
    public void IsPlaintextSqliteHeader_TooShortReturnsNull()
    {
        // 长度不足时不能猜：返回 null 让调用方显示「无法判断」，而不是误报明文。
        Assert.Null(SettingsViewModel.IsPlaintextSqliteHeader(new byte[8]));
        Assert.Null(SettingsViewModel.IsPlaintextSqliteHeader(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Refresh_WhenCoreReportsEncrypted_UsesCoreVerdict()
    {
        var vm = CreateViewModel(() => true);

        vm.RefreshMuClipEncryptionStatus();

        Assert.Contains("已加密", vm.MuClipEncryptionStatus);
        Assert.Contains("核心实测", vm.MuClipEncryptionDetail);
    }

    [Fact]
    public void Refresh_WhenCoreReportsPlaintext_DoesNotClaimEncrypted()
    {
        var vm = CreateViewModel(() => false);

        vm.RefreshMuClipEncryptionStatus();

        Assert.Contains("未加密", vm.MuClipEncryptionStatus);
        Assert.DoesNotContain("已加密", vm.MuClipEncryptionStatus);
    }

    [Fact]
    public void Refresh_WhenCoreUnavailable_FallsBackToFileHeader()
    {
        var vm = CreateViewModel(() => null);

        vm.RefreshMuClipEncryptionStatus();

        // 回退路径必须写明依据，避免和核心实测的结论混为一谈。
        Assert.Contains("库文件头", vm.MuClipEncryptionDetail);
        Assert.NotEqual("检测中…", vm.MuClipEncryptionStatus);
    }

    [Fact]
    public void Refresh_WhenProbeThrows_StillProducesVerdict()
    {
        var vm = CreateViewModel(() => throw new InvalidOperationException("native 库未加载"));

        vm.RefreshMuClipEncryptionStatus();

        Assert.NotEqual("检测中…", vm.MuClipEncryptionStatus);
        Assert.DoesNotContain("已加密（SQLCipher）", vm.MuClipEncryptionStatus);
    }

    [Fact]
    public void Constructor_RunsInitialProbe()
    {
        int calls = 0;
        _ = CreateViewModel(() =>
        {
            calls++;
            return true;
        });

        Assert.Equal(1, calls);
    }
}
