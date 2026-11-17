namespace ShawMuOCR.Core.TextClean;

/// <summary>
/// 文本清洗分类规则标志位
/// </summary>
[Flags]
public enum TextCleanRuleFlags : ushort
{
    None = 0,

    /// <summary>
    /// 中文字符 (汉字 / CJK 统一表意文字)
    /// </summary>
    Chinese = 1 << 0,

    /// <summary>
    /// 英文字母 (a-z, A-Z，含全角字母)
    /// </summary>
    English = 1 << 1,

    /// <summary>
    /// 空格 (普通空格、全角空格、不换行空格等)
    /// </summary>
    Space = 1 << 2,

    /// <summary>
    /// 数字 (0-9，含全角数字)
    /// </summary>
    Number = 1 << 3,

    /// <summary>
    /// 圆角/全角标点符号 (中文常用标点、全角符号)
    /// </summary>
    FullWidthPunctuation = 1 << 4,

    /// <summary>
    /// 半角符号 (ASCII 英文标点与符号)
    /// </summary>
    HalfWidthPunctuation = 1 << 5,

    /// <summary>
    /// 换行符 (\r, \n)
    /// </summary>
    LineBreak = 1 << 6,

    /// <summary>
    /// 制表符 (\t)
    /// </summary>
    Tab = 1 << 7,

    /// <summary>
    /// 全部规则集合
    /// </summary>
    All = Chinese | English | Space | Number | FullWidthPunctuation | HalfWidthPunctuation | LineBreak | Tab
}
