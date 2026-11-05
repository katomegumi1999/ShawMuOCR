using System;
using System.Collections.Generic;
using System.Linq;
using MuRecorder.Interop;
using ShawMuOCR.Core.Capture;

namespace MuRecorder.Services;

/// <summary>
/// 单个窗口源配置。
/// </summary>
public sealed class WindowSourceOption
{
    public IntPtr Hwnd { get; init; }
    public uint Z { get; init; }
    public bool ClientAreaOnly { get; init; }
    public bool AlphaBlend { get; init; }
    public uint Flags { get; init; }
}

/// <summary>
/// 画布配置。
/// </summary>
public sealed class CanvasOptions
{
    /// <summary>画布宽（物理像素，0 表示自动采用首源所在显示器物理宽）。</summary>
    public uint Width { get; init; }
    /// <summary>画布高（物理像素，0 表示自动采用首源所在显示器物理高）。</summary>
    public uint Height { get; init; }
    /// <summary>背景色 0x00RRGGBB（默认 0，纯黑）。</summary>
    public uint Background { get; init; }
}

/// <summary>
/// 录制选项（UI 层 → RecordingService 的输入）。
/// </summary>
public sealed class RecordingOptions
{
    /// <summary>采集模式（Region = 传统区域/整屏裁剪；Window = WGC 窗口表面 + 屏幕画布）。</summary>
    public CaptureMode CaptureMode { get; init; } = CaptureMode.Region;

    /// <summary>录制区域（物理像素）。区域录制模式下有效。</summary>
    public ScreenRect Region { get; init; } = ScreenRect.Empty;

    /// <summary>窗口源列表（窗口录制模式下有效）。</summary>
    public WindowSourceOption[] Sources { get; init; } = Array.Empty<WindowSourceOption>();

    /// <summary>画布设置（窗口录制模式下有效）。</summary>
    public CanvasOptions Canvas { get; init; } = new();

    /// <summary>捕获标志位（bit0 = 系统绘制光标；bit1 = 包含副窗口；bit2 = 窗口关闭时冻结末帧）。</summary>
    public uint CaptureFlags { get; init; } = 1; // 默认开启系统绘制光标

    public uint Fps { get; init; } = 30;

    /// <summary>视频码率（kbps）。</summary>
    public uint BitrateKbps { get; init; } = 8000;

    public EncoderPreference Encoder { get; init; } = EncoderPreference.GpuH264;

    /// <summary>是否合成鼠标指针（Rust 核心合成到画面像素）。</summary>
    public bool CaptureCursor { get; init; } = true;

    /// <summary>鼠标点击处画扩散圆环波纹特效（需 CaptureCursor 为 true）。</summary>
    public bool CursorClickEffect { get; init; }

    /// <summary>鼠标移动流光光轨拖尾毫秒数（0=关闭，建议 120~400，需 CaptureCursor 为 true）。</summary>
    public uint CursorTrailMs { get; init; }

    /// <summary>输出文件路径（.mp4）。</summary>
    public required string OutputPath { get; init; }

    /// <summary>
    /// 主音源：None（静音）、System（默认扬声器 Loopback）、Device（指定**渲染**设备）、
    /// Window（进程/窗口隔离）、Mic（麦克风，采集端点）。
    /// </summary>
    public AudioSourceType AudioSource { get; init; } = AudioSourceType.None;

    /// <summary>
    /// 主音源端点 ID。语义随 <see cref="AudioSource"/> 变化：
    /// Device 时是渲染端点（必填），Mic 时是采集端点（可空 = 默认麦克风）。
    /// </summary>
    public string? AudioDeviceId { get; init; }

    /// <summary>
    /// 叠加用的麦克风端点 ID（null = 默认麦克风）。仅当 <see cref="MicGainPercent"/> &gt; 0 时生效。
    /// </summary>
    public string? MicDeviceId { get; init; }

    /// <summary>
    /// 麦克风叠加增益百分比：0 = 不叠加（默认），1~300 = 叠加到主音轨并乘该系数。
    /// 主音源为 Mic 时不能再叠加（会得到同一端点录两遍）。
    /// </summary>
    public uint MicGainPercent { get; init; }

    public IntPtr AudioWindowHwnd { get; init; }

    public uint AudioProcessId { get; init; }

    /// <summary>音频目标码率（kbps，传 0 默认 320；支持 96, 128, 160, 192, 256, 320）。</summary>
    public uint AudioBitrateKbps { get; init; } = 320;

    /// <summary>
    /// 便利构造：为指定的单个或多个窗口创建窗口录制选项。
    /// </summary>
    public static RecordingOptions ForWindows(
        IEnumerable<IntPtr> hwnds,
        string outputPath,
        uint fps = 30,
        uint bitrateKbps = 8000,
        EncoderPreference encoder = EncoderPreference.GpuH264)
    {
        var sources = hwnds
            .Select((h, idx) => new WindowSourceOption { Hwnd = h, Z = (uint)idx })
            .ToArray();

        return new RecordingOptions
        {
            CaptureMode = CaptureMode.Window,
            Sources = sources,
            OutputPath = outputPath,
            Fps = fps,
            BitrateKbps = bitrateKbps,
            Encoder = encoder,
        };
    }
}
