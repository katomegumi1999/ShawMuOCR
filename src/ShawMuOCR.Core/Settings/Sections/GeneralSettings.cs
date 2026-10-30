using System.Text.Json.Serialization;

namespace ShawMuOCR.Core.Settings;

/// <summary>
/// 通用系统偏好分块配置
/// </summary>
public sealed class GeneralSettings
{
    public bool AutoCopy { get; set; } = true;

    /// <summary>
    /// 表格智能分列复制：自动复制前判断识别结果是否呈表格结构（多行多列、按检测框几何对齐），
    /// 是则改用 TSV（制表符分列 + 换行分行）写入剪贴板，粘贴到 Excel / WPS 等表格软件时直接拆入单元格；
    /// 非表格内容仍按原有纯文本写入，粘贴到聊天窗口、记事本不受影响。
    /// </summary>
    public bool AutoCopyTableAsCells { get; set; } = true;

    /// <summary>
    /// 静默模式：开启后识别完成不再自动弹出主窗口
    /// </summary>
    public bool SilentMode { get; set; } = true;

    /// <summary>
    /// 静默启动：开启后启动直接最小化到系统托盘，不弹出主界面UI
    /// </summary>
    public bool SilentStartup { get; set; } = false;

    /// <summary>
    /// OCR 识别气泡提醒：开启后在屏幕底部弹出识别结果与耗时卡片
    /// </summary>
    public bool ShowOcrNotification { get; set; } = true;

    /// <summary>
    /// 截图复制气泡提醒：开启后在屏幕底部弹出截图复制成功提示
    /// </summary>
    public bool ShowCaptureImageNotification { get; set; } = true;

    /// <summary>
    /// 兼容旧版气泡提醒配置
    /// </summary>
    [JsonIgnore]
    public bool ShowNotificationBadge
    {
        get => ShowOcrNotification;
        set
        {
            ShowOcrNotification = value;
            ShowCaptureImageNotification = value;
        }
    }

    public bool ShowWindowAfterOcr { get; set; } = true;

    public bool StartWithWindows { get; set; } = false;

    /// <summary>
    /// 开启本地文件日志服务
    /// </summary>
    public bool EnableLogging { get; set; } = false;

    /// <summary>
    /// 开启日志定期自动清理
    /// </summary>
    public bool EnableLogAutoClean { get; set; } = true;

    /// <summary>
    /// 日志保留天数（超过该天数的日志文件自动清理删除，默认 7 天）
    /// </summary>
    public int LogRetentionDays { get; set; } = 7;

    /// <summary>
    /// 截图标注默认保存格式（PNG, JPEG, WEBP, BMP）
    /// </summary>
    public string DefaultSaveFormat { get; set; } = "PNG";

    /// <summary>
    /// 截图标注默认保存画质（10-100）
    /// </summary>
    public int DefaultSaveQuality { get; set; } = 90;

    /// <summary>
    /// 截图标注默认保存缩放百分比（如 100 代表 100%）
    /// </summary>
    public int DefaultSaveScale { get; set; } = 100;

    /// <summary>
    /// 截图上次保存的文件夹路径（为空时默认使用系统图片目录）
    /// </summary>
    public string? LastSaveDirectory { get; set; }

    /// <summary>
    /// 截图标注编辑器粗细记忆
    /// </summary>
    public float AnnotationStrokeWidth { get; set; } = 4f;

    /// <summary>
    /// 截图标注编辑器字号记忆
    /// </summary>
    public float AnnotationFontSize { get; set; } = 40f;

    /// <summary>
    /// 是否启用小沐剪贴板功能（默认关闭）
    /// </summary>
    public bool EnableClipboard { get; set; } = false;

    /// <summary>
    /// 快捷面板背景底色（十六进制颜色，默认 "#F4F4F6" 浅灰）
    /// </summary>
    public string ClipboardPanelBackground { get; set; } = "#F4F4F6";

    public GeneralSettings Clone() => new()
    {
        AutoCopy = AutoCopy,
        AutoCopyTableAsCells = AutoCopyTableAsCells,
        SilentMode = SilentMode,
        SilentStartup = SilentStartup,
        ShowOcrNotification = ShowOcrNotification,
        ShowCaptureImageNotification = ShowCaptureImageNotification,
        ShowWindowAfterOcr = ShowWindowAfterOcr,
        StartWithWindows = StartWithWindows,
        EnableLogging = EnableLogging,
        EnableLogAutoClean = EnableLogAutoClean,
        LogRetentionDays = LogRetentionDays,
        DefaultSaveFormat = DefaultSaveFormat,
        DefaultSaveQuality = DefaultSaveQuality,
        DefaultSaveScale = DefaultSaveScale,
        LastSaveDirectory = LastSaveDirectory,
        AnnotationStrokeWidth = AnnotationStrokeWidth,
        AnnotationFontSize = AnnotationFontSize,
        EnableClipboard = EnableClipboard,
        ClipboardPanelBackground = ClipboardPanelBackground
    };
}
