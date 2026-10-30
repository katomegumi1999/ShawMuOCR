using System.Runtime.InteropServices;

namespace MuRecorder.Interop;

/// <summary>
/// 音源类型，与 recorder.h 的 AudioSourceType 对齐。
/// M1 仅支持 None（Rust 侧 audio 能力随 M4 WASAPI 落地）。
/// </summary>
public enum AudioSourceType
{
    None = 0,
    System = 1,
    Device = 2,
    Window = 3,
}

/// <summary>
/// 编码器偏好，与 C 侧 encoder 字段语义对齐。
/// </summary>
public enum EncoderPreference
{
    Auto = 0,
    GpuHevc = 1,
    GpuH264 = 2,
    Amf = 3,
    Soft = 4,
    GpuAv1 = 5,

    // 兼容历史命名
    Nvenc = 1,
    Qsv = 2,
}

/// <summary>
/// RecorderConfig 的 C# 映射（[StructLayout(Sequential)]，字段顺序/类型与 recorder.h 严格一致）。
/// 首字段固定 AbiVersion，后续加字段只能尾部追加（Rust 侧 config.rs 有校验回归测试）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct RecorderConfig
{
    /// <summary>必须填 <see cref="RecorderAbi.Version"/>。</summary>
    public uint AbiVersion;
    /// <summary>区域左上角 X（物理像素，虚拟桌面坐标系）。</summary>
    public int RegionX;
    /// <summary>区域左上角 Y（物理像素，虚拟桌面坐标系）。</summary>
    public int RegionY;
    public uint RegionWidth;
    public uint RegionHeight;
    public uint Fps;
    /// <summary><see cref="EncoderPreference"/>。</summary>
    public uint Encoder;
    public uint BitrateKbps;
    /// <summary>是否捕获鼠标指针（M1 忽略，预留）。</summary>
    public uint CaptureCursor;
    /// <summary>UTF-8 输出路径。Rust 只读不持有，调用返回后即可释放。</summary>
    public byte* OutputPath;
    /// <summary><see cref="AudioSourceType"/>。</summary>
    public uint AudioSourceTypeValue;
    /// <summary>UTF-8 音频设备 ID，可空。</summary>
    public byte* AudioDeviceId;
    public ulong AudioWindowHwnd;
    public uint AudioProcessId;
    /// <summary>音频码率（kbps，传 0 默认 320；支持 96, 128, 160, 192, 256, 320）。</summary>
    public uint AudioBitrateKbps;

    /* ---- v3 追加 ---- */
    /// <summary>0/1：点击处画扩散圆环（约 350 ms 淡出）。需 CaptureCursor=1</summary>
    public uint CursorClickEffect;
    /// <summary>移动轨迹拖尾时长 ms：0=关闭，否则 40~3000。需 CaptureCursor=1</summary>
    public uint CursorTrailMs;

    public AudioSourceType AudioSource
    {
        get => (AudioSourceType)AudioSourceTypeValue;
        set => AudioSourceTypeValue = (uint)value;
    }
}

/// <summary>ABI 版本常量（对应 C 侧 RECORDER_ABI_VERSION）。</summary>
public static class RecorderAbi
{
    public const uint Version = 3;
}

/// <summary>
/// RecorderStats 的 C# 映射（与 recorder.h 严格对齐）。get_stats 无锁快照，可直接在 UI 线程调。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct RecorderStats
{
    public ulong FramesCaptured;
    public ulong FramesEncoded;
    public ulong FramesDropped;
    /// <summary>滑动窗口瞬时帧率（200ms 采样）。</summary>
    public uint CurrentFps;
    /// <summary>滑动窗口瞬时码率（kbps）。</summary>
    public uint BitrateKbps;
    /// <summary>待编码帧积压（视频 ring）。</summary>
    public uint QueueDepth;
    /// <summary>实际选定的编码器类型（4 = 软件 H.264）。</summary>
    public uint EncoderType;
    /// <summary>最近一次错误码（0 = 无）。</summary>
    public int LastError;
    public ulong ElapsedMs;

    /// <summary>编码器可读名称。</summary>
    public string EncoderName => EncoderType switch
    {
        1 => "GPU · H.265 (HEVC)",
        2 => "GPU · H.264 (AVC)",
        4 => "CPU · H.264 (软编)",
        5 => "GPU · AV1 (硬件加速)",
        _ => "未知",
    };
}
