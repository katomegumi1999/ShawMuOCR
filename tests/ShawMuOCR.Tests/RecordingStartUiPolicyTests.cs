using MuRecorder.ViewModels;
using ShawMuOCR.Core.Capture;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Settings;
using Xunit;

namespace ShawMuOCR.Tests;

/// <summary>
/// 开录瞬间的两个界面开关：**各管各的，互不联动**。
///
/// - 「开录后最小化主窗口」→ `MinimizeOnStart`（默认开）
/// - 「开录后显示悬浮控制条」→ `IsFloatingBarEnabled`（默认关）
///
/// 历史演进值得记一笔，免得后人又把这俩搅在一起：
/// 最早的做法是「浮窗开 或 配了录屏热键才允许最小化」，且最小化时强制把控制条推到桌面上
/// （`IsFloatingBarEnabled || minimizeHost`）。用户 2026-11-07 先裁定「别自动弹」，
/// 再补了一句「干脆给个开关，最小化也让我自己决定」。
/// 所以现在最小化不再是必然行为，控制条也不会因为最小化被强行打开 —— 两个开关独立生效。
/// </summary>
public class RecordingStartUiPolicyTests
{
    [Fact]
    public void Start_DoesNotShowFloatingBar_WhenUserDisabledIt()
    {
        // 设置里关掉控制条 → 开录后屏幕上不许凭空多出控制栏
        Assert.False(RecordingViewModel.ShouldShowFloatingBarOnStart(floatingBarEnabled: false));
    }

    [Fact]
    public void Start_ShowsFloatingBar_WhenUserEnabledIt()
    {
        Assert.True(RecordingViewModel.ShouldShowFloatingBarOnStart(floatingBarEnabled: true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Start_FloatingBarVisibility_FollowsSettingExactly(bool enabled)
    {
        // 幂等性：策略就是设置本身，不存在「取反」「只看第一次」这类隐式行为
        Assert.Equal(enabled, RecordingViewModel.ShouldShowFloatingBarOnStart(enabled));
        Assert.Equal(enabled, RecordingViewModel.ShouldShowFloatingBarOnStart(enabled));
    }

    [Fact]
    public void MinimizeOnStart_DefaultsToEnabled()
    {
        // 默认保持老行为（开录即最小化），老用户不会因为加了这个开关就"窗口突然不收了"
        var vm = new RecordingViewModel(new StubCapture(), new StubSelection());
        Assert.True(vm.MinimizeOnStart);
    }

    [Fact]
    public void MinimizeOnStart_LoadsFromSettings_AndPersistsBack()
    {
        var settings = new FakeSettingsService();
        settings.Current.RecordingMinimizeOnStart = false;

        var vm = new RecordingViewModel(new StubCapture(), new StubSelection(), logger: null, settingsService: settings);
        // 构造时从设置读：关着就关着，别被字段默认值 true 盖掉
        Assert.False(vm.MinimizeOnStart);

        // 用户勾上 → 立即写回设置（无需点保存）
        vm.MinimizeOnStart = true;
        Assert.True(settings.Current.RecordingMinimizeOnStart);
        Assert.True(settings.SaveCallCount > 0);

        // 新建一个 VM 读同一份设置 → 记忆生效
        var reopened = new RecordingViewModel(new StubCapture(), new StubSelection(), logger: null, settingsService: settings);
        Assert.True(reopened.MinimizeOnStart);
    }

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
    }

    private sealed class StubCapture : IScreenCaptureService
    {
        public ScreenRect GetVirtualScreenBounds() => new(0, 0, 1920, 1080);
        public OcrImage CaptureVirtualScreen() => new(new SkiaSharp.SKBitmap(8, 8));
        public OcrImage CaptureRegion(ScreenRect rect) => new(new SkiaSharp.SKBitmap(8, 8));
    }

    private sealed class StubSelection : IScreenSelectionService
    {
        public Task<OcrImage?> SelectRegionAsync(OcrImage fullScreenshot, ScreenRect virtualBounds)
            => Task.FromResult<OcrImage?>(null);
    }
}
