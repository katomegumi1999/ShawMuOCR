using ShawMuOCR.Core.TextClean;

namespace ShawMuOCR.Core.Settings;

/// <summary>
/// OCR 文本后清洗配置
/// </summary>
public sealed class TextCleanSettings
{
    /// <summary>
    /// 是否启用文本清洗（默认关闭）
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// 清洗模式：保留模式 (Keep) 或 移除模式 (Remove)，默认保留模式
    /// </summary>
    public TextCleanMode Mode { get; set; } = TextCleanMode.Keep;

    /// <summary>
    /// 中文字符
    /// </summary>
    public bool Chinese { get; set; } = true;

    /// <summary>
    /// 英文字母
    /// </summary>
    public bool English { get; set; } = true;

    /// <summary>
    /// 数字 (0-9)
    /// </summary>
    public bool Number { get; set; } = true;

    /// <summary>
    /// 空格
    /// </summary>
    public bool Space { get; set; } = true;

    /// <summary>
    /// 圆角/全角标点符号
    /// </summary>
    public bool FullWidthPunctuation { get; set; } = true;

    /// <summary>
    /// 半角符号
    /// </summary>
    public bool HalfWidthPunctuation { get; set; } = true;

    /// <summary>
    /// 换行符 (\r, \n)
    /// </summary>
    public bool LineBreak { get; set; } = true;

    /// <summary>
    /// 制表符 (\t)
    /// </summary>
    public bool Tab { get; set; } = true;

    /// <summary>
    /// 将各个规则选项转换为标志掩码
    /// </summary>
    public TextCleanRuleFlags ToFlags()
    {
        TextCleanRuleFlags flags = TextCleanRuleFlags.None;
        if (Chinese) flags |= TextCleanRuleFlags.Chinese;
        if (English) flags |= TextCleanRuleFlags.English;
        if (Number) flags |= TextCleanRuleFlags.Number;
        if (Space) flags |= TextCleanRuleFlags.Space;
        if (FullWidthPunctuation) flags |= TextCleanRuleFlags.FullWidthPunctuation;
        if (HalfWidthPunctuation) flags |= TextCleanRuleFlags.HalfWidthPunctuation;
        if (LineBreak) flags |= TextCleanRuleFlags.LineBreak;
        if (Tab) flags |= TextCleanRuleFlags.Tab;
        return flags;
    }

    public TextCleanSettings Clone() => new()
    {
        Enabled = Enabled,
        Mode = Mode,
        Chinese = Chinese,
        English = English,
        Number = Number,
        Space = Space,
        FullWidthPunctuation = FullWidthPunctuation,
        HalfWidthPunctuation = HalfWidthPunctuation,
        LineBreak = LineBreak,
        Tab = Tab
    };
}
