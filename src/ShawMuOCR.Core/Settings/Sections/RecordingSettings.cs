namespace ShawMuOCR.Core.Settings;

/// <summary>
/// 屏幕录制分块配置
/// </summary>
public sealed class RecordingSettings
{
    private string _filePrefix = "MuRecorder";

    /// <summary>
    /// 录屏编码器偏好：默认 1 = GPU H.265 (HEVC)
    /// 0=Auto, 1=GpuHevc, 2=GpuH264, 4=Soft, 5=GpuAv1
    /// </summary>
    public int Encoder { get; set; } = 1;

    /// <summary>
    /// 录屏默认帧率（默认 30）
    /// </summary>
    public uint Fps { get; set; } = 30;

    /// <summary>
    /// 录屏默认码率 kbps（默认 8000）
    /// </summary>
    public uint BitrateKbps { get; set; } = 8000;

    /// <summary>
    /// 录屏是否显示区域边界高亮线（默认 true）
    /// </summary>
    public bool ShowRegionBorder { get; set; } = true;

    /// <summary>
    /// 录屏自定义输出目录（可空，空则走系统默认视频目录）
    /// </summary>
    public string OutputDir { get; set; } = string.Empty;

    /// <summary>
    /// 录屏保存文件名前缀（默认 MuRecorder）
    /// </summary>
    public string FilePrefix
    {
        get => string.IsNullOrWhiteSpace(_filePrefix) ? "MuRecorder" : _filePrefix;
        set => _filePrefix = string.IsNullOrWhiteSpace(value) ? "MuRecorder" : value.Trim();
    }

    /// <summary>
    /// 录屏音频目标码率（kbps，默认 320）
    /// </summary>
    public uint AudioBitrateKbps { get; set; } = 320;

    /// <summary>
    /// 录屏是否捕获鼠标指针（默认 true）
    /// </summary>
    public bool CaptureCursor { get; set; } = true;

    /// <summary>
    /// 录屏是否启用鼠标点击水波纹特效（默认 false）
    /// </summary>
    public bool CursorClickEffect { get; set; } = false;

    /// <summary>
    /// 录屏是否启用鼠标轨迹拖尾残影（默认 false）
    /// </summary>
    public bool CursorTrail { get; set; } = false;

    /// <summary>
    /// 录屏鼠标轨迹拖尾持续时间（毫秒，默认 200，范围 40~3000）
    /// </summary>
    public uint CursorTrailMs { get; set; } = 200;

    /// <summary>
    /// 录屏悬浮工具栏是否对录制隐身（使用 WDA_EXCLUDEFROMCAPTURE。注意：远程桌面环境如被系统隐藏，可关闭此项）
    /// </summary>
    public bool ExcludeFloatingBarFromCapture { get; set; } = true;

    /// <summary>
    /// 录屏是否显示桌面悬浮控制条（默认 false，开启时显示且高亮 accent，关闭时隐藏）
    /// </summary>
    public bool ShowFloatingBar { get; set; } = false;

    /// <summary>
    /// 录屏时是否在桌面实时呈现鼠标点击波纹（演示模式，默认 false）
    /// </summary>
    public bool LiveCursorEffect { get; set; } = false;

    public RecordingSettings Clone() => new()
    {
        Encoder = Encoder,
        Fps = Fps,
        BitrateKbps = BitrateKbps,
        ShowRegionBorder = ShowRegionBorder,
        OutputDir = OutputDir,
        FilePrefix = FilePrefix,
        AudioBitrateKbps = AudioBitrateKbps,
        CaptureCursor = CaptureCursor,
        CursorClickEffect = CursorClickEffect,
        CursorTrail = CursorTrail,
        CursorTrailMs = CursorTrailMs,
        ExcludeFloatingBarFromCapture = ExcludeFloatingBarFromCapture,
        ShowFloatingBar = ShowFloatingBar,
        LiveCursorEffect = LiveCursorEffect
    };
}
