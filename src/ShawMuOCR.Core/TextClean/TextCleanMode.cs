namespace ShawMuOCR.Core.TextClean;

/// <summary>
/// OCR 文本清洗模式
/// </summary>
public enum TextCleanMode
{
    /// <summary>
    /// 保留模式：只保留已选规则中的字符类型，剔除其他未选字符
    /// </summary>
    Keep = 0,

    /// <summary>
    /// 移除模式：剔除已选规则中的字符类型，保留其他未选字符
    /// </summary>
    Remove = 1
}
