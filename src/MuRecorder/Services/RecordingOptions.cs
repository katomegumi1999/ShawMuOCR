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

    /// <summary>是否合成鼠标指针（M1 忽略，Rust 侧预留）。</summary>
    public bool CaptureCursor { get; init; }

    /// <summary>输出文件路径（.mp4）。</summary>
    public required string OutputPath { get; init; }

    /// <summary>音源。M1 仅支持 None（Rust M4 开放 WASAPI）。</summary>
    public MuRecorder.Interop.AudioSourceType AudioSource { get; init; } = MuRecorder.Interop.AudioSourceType.None;

    public string? AudioDeviceId { get; init; }

    public IntPtr AudioWindowHwnd { get; init; }

    public uint AudioProcessId { get; init; }
}
