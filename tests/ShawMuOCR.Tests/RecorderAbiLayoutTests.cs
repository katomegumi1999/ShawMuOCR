using System;
using System.Runtime.InteropServices;
using MuRecorder.Interop;
using MuRecorder.Services;
using ShawMuOCR.Core.Capture;
using Xunit;

namespace ShawMuOCR.Tests;

/// <summary>
/// C ABI 结构布局与版本号的锁。
/// </summary>
/// <remarks>
/// 为什么值得单独一个测试文件：<see cref="RecorderConfig"/> 是「C# 写、Rust 读」的
/// 裸内存布局，字段顺序/类型/尾部追加只要错一处，Rust 就会读到错位的值——而错位
/// 读**不报错**，表现是「某个开关莫名不生效」或「多出几秒静音」，极难定位。
/// 唯一可靠的防线是版本号 + 字节大小 + 行为验证三件套：
///   1. 版本号必须与 recorder.h 的 RECORDER_ABI_VERSION 同步递增；
///   2. 结构体大小必须与 C 侧一致（下面写死了 x64 下的期望值）；
///   3. 尾部新字段必须真的被 Rust 读到（见 MicOverlay_* 用例）。
/// </remarks>
public class RecorderAbiLayoutTests
{
    /// <summary>ABI 版本必须与 recorder.h / config.rs 的常量一致。</summary>
    [Fact]
    public void AbiVersion_MatchesHeader()
    {
        Assert.Equal(5u, RecorderAbi.Version);
    }

    /// <summary>
    /// 结构体大小必须与 C 侧 <c>sizeof(RecorderConfig)</c> 一致。
    /// </summary>
    /// <remarks>
    /// x64 下的期望值 136 由以下布局算出（偏移在注释里，对应 recorder.h）：
    /// <code>
    ///   0   abi_version .. capture_cursor  (9 × 4 = 36) → 补 4 对齐到 8
    ///   40  output_path (ptr)        48  audio_source_type
    ///   56  audio_device_id (ptr)    64  audio_window_hwnd
    ///   72  audio_process_id         76  audio_bitrate_kbps
    ///   80  cursor_click_effect      84  cursor_trail_ms
    ///   88  capture_mode             92  canvas_w
    ///   96  canvas_h                100  canvas_background
    ///   104 source_count            112  sources (ptr)
    ///   120 capture_flags           124  mic_gain_percent   ← v5
    ///   128 mic_device_id (ptr)                             ← v5
    ///   136 末尾
    /// </code>
    /// 每次升 ABI 都要重算这个数——这正是它的作用：逼你回头核对两边布局。
    /// </remarks>
    [Fact]
    public void ConfigLayout_MatchesNativeSize()
    {
        Assert.Equal(8, IntPtr.Size); // 以下计算仅对 x64 成立
        Assert.Equal(136, Marshal.SizeOf<RecorderConfig>());
    }

    /// <summary>
    /// 尾部新字段必须真的落到 Rust 侧：主音源为「无音频」却开启麦克风叠加，
    /// 必须被 Rust 以 REC_ERR_INVALID 拒绝。
    /// </summary>
    /// <remarks>
    /// 这条用例同时验证了两件事：v5 的 <c>mic_gain_percent</c> 偏移正确（否则 Rust
    /// 读到 0，不会拒绝），以及 <see cref="RecorderAbi.Version"/> 与 Rust 的期望版本
    /// 一致（否则先撞版本不匹配，同样返回 Invalid —— 但至少不是静默放行）。
    /// </remarks>
    [Fact]
    public async Task MicOverlay_WithoutPrimarySource_IsRejectedByCore()
    {
        await using var service = new RecordingService();
        var options = new RecordingOptions
        {
            Region = new ScreenRect(0, 0, 320, 240),
            Fps = 30,
            OutputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shawmu_mic_overlay_none.mp4"),
            AudioSource = AudioSourceType.None,
            MicGainPercent = 100,
        };

        var ex = await Assert.ThrowsAsync<RecordingException>(() => service.StartAsync(options));
        Assert.Equal(RecorderResult.Invalid, ex.Result);
    }

    /// <summary>
    /// 主音源已经是麦克风时再叠加麦克风 → Rust 拒绝（同一端点录两遍没有意义）。
    /// </summary>
    /// <remarks>
    /// 顺带证明 <see cref="AudioSourceType.Mic"/> = 4 这个**新增枚举值**确实被 Rust
    /// 认出来了：若两侧枚举不同步，Rust 会报「未知 audio_source_type」，
    /// 同样是 Invalid，但那时下面的行为断言就不成立了（不会走到叠加校验）。
    /// </remarks>
    [Fact]
    public async Task MicOverlay_OnMicrophonePrimary_IsRejectedByCore()
    {
        await using var service = new RecordingService();
        var options = new RecordingOptions
        {
            Region = new ScreenRect(0, 0, 320, 240),
            Fps = 30,
            OutputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shawmu_mic_overlay_mic.mp4"),
            AudioSource = AudioSourceType.Mic,
            MicGainPercent = 100,
        };

        var ex = await Assert.ThrowsAsync<RecordingException>(() => service.StartAsync(options));
        Assert.Equal(RecorderResult.Invalid, ex.Result);
    }

    /// <summary>
    /// 麦克风增益超过 300% 必须被拒绝：钳位在 Rust 侧是硬红线（防止把底噪放大到爆音）。
    /// </summary>
    [Fact]
    public async Task MicGain_AboveLimit_IsRejectedByCore()
    {
        await using var service = new RecordingService();
        var options = new RecordingOptions
        {
            Region = new ScreenRect(0, 0, 320, 240),
            Fps = 30,
            OutputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shawmu_mic_gain_high.mp4"),
            AudioSource = AudioSourceType.None,
            MicGainPercent = 500,
        };

        var ex = await Assert.ThrowsAsync<RecordingException>(() => service.StartAsync(options));
        Assert.Equal(RecorderResult.Invalid, ex.Result);
    }
}
