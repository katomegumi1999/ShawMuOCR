using System;
using System.Collections.Generic;
using Avalonia.Media;

namespace ShawMuOCR.Services;

public record ColorPaletteItem(string Name, string? Hex, string DisplayColorHex, string Tooltip);

public static class ColorPaletteHelper
{
    public static readonly IReadOnlyList<ColorPaletteItem> StandardPresets = new ColorPaletteItem[]
    {
        new("默认浅灰", "#F3F4F6", "#F3F4F6", "默认柔和微浅灰底色 (#F3F4F6)"),
        new("纯白", "#FFFFFF", "#FFFFFF", "高对比度纯白"),
        new("雅白", "#F8FAFC", "#F8FAFC", "柔和纯净雅白"),
        new("雾灰", "#E2E8F0", "#E2E8F0", "优雅低调雾灰"),
        new("板岩灰", "#64748B", "#64748B", "沉稳板岩深灰"),
        new("雅黑", "#1E293B", "#1E293B", "深邃科技雅黑"),
        new("晴空蓝", "#0284C7", "#0284C7", "明亮晴空蓝"),
        new("冰川蓝", "#BAE6FD", "#BAE6FD", "清新冰川浅蓝"),
        new("翡翠绿", "#059669", "#059669", "自然翠绿"),
        new("薄荷绿", "#A7F3D0", "#A7F3D0", "柔美薄荷淡绿"),
        new("珊瑚红", "#E11D48", "#E11D48", "活力珊瑚红"),
        new("蜜桃粉", "#FECDD3", "#FECDD3", "温暖蜜桃淡粉"),
        new("琥珀金", "#D97706", "#D97706", "典雅琥珀金"),
        new("暖杏黄", "#FDE68A", "#FDE68A", "温馨暖杏黄"),
        new("紫罗兰", "#7C3AED", "#7C3AED", "高贵紫罗兰"),
        new("薰衣紫", "#DDD6FE", "#DDD6FE", "浪漫薰衣淡紫")
    };

    public static bool TryParseColor(string? hex, out Color color)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            color = default;
            return false;
        }

        string trimmed = hex.Trim();
        if (!trimmed.StartsWith('#'))
        {
            trimmed = "#" + trimmed;
        }

        if (Color.TryParse(trimmed, out color))
        {
            if (color.A == 0 && trimmed.Length <= 7)
            {
                color = Color.FromArgb(255, color.R, color.G, color.B);
            }
            return true;
        }

        color = default;
        return false;
    }
}
