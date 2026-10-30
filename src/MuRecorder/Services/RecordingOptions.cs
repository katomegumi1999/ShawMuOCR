using ShawMuOCR.Core.Capture;

namespace MuRecorder.Services;

/// <summary>
/// 录制选项（UI 层 → RecordingService 的输入）。区域为物理像素（虚拟桌面坐标系），
/// 由调用方（MonitorEnumerator / RegionSelectorWindow）完成 DPI 换算。
/// </summary>
public sealed class RecordingOptions
{
    /// <summary>录制区域（物理像素）。M1 要求完整落在单一显示器内（Rust 侧校验）。</summary>
    public required ScreenRect Region { get; init; }

    public uint Fps { get; init; } = 30;

    /// <summary>视频码率（kbps）。</summary>
    public uint BitrateKbps { get; init; } = 8000;

    public MuRecorder.Interop.EncoderPreference Encoder { get; init; } = MuRecorder.Interop.EncoderPreference.GpuH264;

    /// <summary>是否合成鼠标指针（Rust 核心合成到画面像素）。</summary>
    public bool CaptureCursor { get; init; } = true;

    /// <summary>鼠标点击处画扩散圆环波纹特效（需 CaptureCursor 为 true）。</summary>
    public bool CursorClickEffect { get; init; }

    /// <summary>鼠标移动流光光轨拖尾毫秒数（0=关闭，建议 120~400，需 CaptureCursor 为 true）。</summary>
    public uint CursorTrailMs { get; init; }

    /// <summary>输出文件路径（.mp4）。</summary>
    public required string OutputPath { get; init; }

    /// <summary>音源：None（静音）、System（默认扬声器 Loopback）、Device（指定设备）、Window（进程/窗口隔离）。</summary>
    public MuRecorder.Interop.AudioSourceType AudioSource { get; init; } = MuRecorder.Interop.AudioSourceType.None;

    public string? AudioDeviceId { get; init; }

    public IntPtr AudioWindowHwnd { get; init; }

    public uint AudioProcessId { get; init; }

    /// <summary>音频目标码率（kbps，传 0 默认 320；支持 96, 128, 160, 192, 256, 320）。</summary>
    public uint AudioBitrateKbps { get; init; } = 320;
}
