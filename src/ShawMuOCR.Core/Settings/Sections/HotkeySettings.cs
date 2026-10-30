namespace ShawMuOCR.Core.Settings;

/// <summary>
/// 全局快捷键分块配置
/// </summary>
public sealed class HotkeySettings
{
    /// <summary>
    /// OCR 截屏识别全局快捷键（默认 Alt+Z）
    /// </summary>
    public string Ocr { get; set; } = "Alt+Z";

    /// <summary>
    /// 截屏复制全局快捷键（默认 Alt+X）
    /// </summary>
    public string CaptureImage { get; set; } = "Alt+X";

    /// <summary>
    /// 截图标注全局快捷键（默认 Alt+C）
    /// </summary>
    public string Annotate { get; set; } = "Alt+C";

    /// <summary>
    /// 选区翻译全局快捷键（默认 Alt+T）
    /// </summary>
    public string Translate { get; set; } = "Alt+T";

    /// <summary>
    /// OCR 窗格全局快捷键（默认 Alt+D），截屏识别后在悬浮窗中显示结果而不自动复制
    /// </summary>
    public string OcrPopup { get; set; } = "Alt+D";

    /// <summary>
    /// 随手便笺全局快捷键（默认 Alt+N），呼出自由输入浮窗
    /// </summary>
    public string QuickNote { get; set; } = "Alt+N";

    /// <summary>
    /// 极速剪贴板快捷面板全局快捷键（默认 Alt+Q）
    /// </summary>
    public string ClipboardPanel { get; set; } = "Alt+Q";

    /// <summary>
    /// 常用语快捷呼出键（默认 Alt+W）
    /// </summary>
    public string PhrasesPanel { get; set; } = "Alt+W";

    /// <summary>
    /// 长截图全局快捷键（默认 Alt+L）：框选区域后跟随用户手动滚动拼接成长图，结果写入剪贴板
    /// </summary>
    public string LongScreenshot { get; set; } = "Alt+L";

    /// <summary>
    /// 录屏全局快捷键（默认 Alt+R）
    /// </summary>
    public string Recording { get; set; } = "Alt+R";

    public HotkeySettings Clone() => new()
    {
        Ocr = Ocr,
        CaptureImage = CaptureImage,
        Annotate = Annotate,
        Translate = Translate,
        OcrPopup = OcrPopup,
        QuickNote = QuickNote,
        ClipboardPanel = ClipboardPanel,
        PhrasesPanel = PhrasesPanel,
        LongScreenshot = LongScreenshot,
        Recording = Recording
    };
}
