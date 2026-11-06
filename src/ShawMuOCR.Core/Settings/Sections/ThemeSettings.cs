namespace ShawMuOCR.Core.Settings;

/// <summary>
/// 主题相关的硬性默认值。放在 Core 层是因为主题服务与多个浮窗都要用同一份，
/// 各窗口各自写死一个色值迟早会漂。
/// </summary>
public static class ThemeDefaults
{
    /// <summary>
    /// 随动浮窗（选区翻译 / OCR 结果窗）在用户未自定义时的背景色。
    ///
    /// 这里必须给一个**不透明实色**：浮窗窗口本身是 `Background="Transparent"`，
    /// 早期实现「未自定义就清空资源、跟随系统默认」，结果浮窗整个变透明 ——
    /// 桌面内容直接透上来，文字糊在背景上没法看。默认浅灰既看得清又不像纯白那样刺眼。
    /// </summary>
    public const string FloatingWindowBackgroundHex = "#F3F3F3";
}

/// <summary>
/// 主题、外观与页面记忆分块配置
/// </summary>
public sealed class ThemeSettings
{
    public bool IsSidebarExpanded { get; set; } = true;
    public string? BackgroundImagePath { get; set; }
    public double BackgroundBlurRadius { get; set; } = 0.0;
    public double ComponentOpacity { get; set; } = 0.12;
    public double ButtonOpacity { get; set; } = 0.12;
    public bool EnableAcrylic { get; set; } = true;
    public string? BackgroundColorHex { get; set; }
    public string? CardColorHex { get; set; }
    public string? TextColorHex { get; set; }
    public string? FloatingWindowBackgroundColorHex { get; set; }
    public string? FloatingWindowTextColorHex { get; set; }

    /// <summary>
    /// 上次退出的导航子页面，下次非静默启动时优先先渲染该页面
    /// </summary>
    public string LastActivePage { get; set; } = "Ocr";

    public ThemeSettings Clone() => new()
    {
        IsSidebarExpanded = IsSidebarExpanded,
        BackgroundImagePath = BackgroundImagePath,
        BackgroundBlurRadius = BackgroundBlurRadius,
        ComponentOpacity = ComponentOpacity,
        ButtonOpacity = ButtonOpacity,
        EnableAcrylic = EnableAcrylic,
        BackgroundColorHex = BackgroundColorHex,
        CardColorHex = CardColorHex,
        TextColorHex = TextColorHex,
        FloatingWindowBackgroundColorHex = FloatingWindowBackgroundColorHex,
        FloatingWindowTextColorHex = FloatingWindowTextColorHex,
        LastActivePage = LastActivePage
    };
}
