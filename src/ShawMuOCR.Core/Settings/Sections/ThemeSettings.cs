namespace ShawMuOCR.Core.Settings;

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
