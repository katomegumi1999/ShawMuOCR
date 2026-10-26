namespace ShawMuOCR.Core.Sorting;

/// <summary>
/// 中日韩字符判定与「按检测框拼接文本」的公共规则。
/// 供 <see cref="DefaultOcrTextSorter"/> 与 <see cref="OcrTableLayoutBuilder"/> 共用，
/// 保证屏幕取词结果与表格单元格内的拼接口径完全一致。
/// </summary>
internal static class OcrCjkTextHelper
{
    /// <summary>
    /// 简易 CJK 字符区间判断（汉字、扩展 A、中日韩标点、全角字符）
    /// </summary>
    public static bool IsCjk(char c)
    {
        return (c >= 0x4E00 && c <= 0x9FFF) ||
               (c >= 0x3400 && c <= 0x4DBF) ||
               (c >= 0x3000 && c <= 0x303F) || // 符号与标点
               (c >= 0xFF00 && c <= 0xFFEF);   // 全角字符
    }

    /// <summary>
    /// 将一段文本并入已有的 <see cref="System.Text.StringBuilder"/>：
    /// 两端均为 CJK 时直接相连（中文无需空格），否则补一个半角空格作为西文单词分隔。
    /// 调用方需保证 <paramref name="text"/> 已去除首尾空白且非空。
    /// </summary>
    public static void AppendInline(System.Text.StringBuilder sb, string text)
    {
        if (sb.Length > 0)
        {
            char lastChar = sb[^1];
            char firstChar = text[0];

            if (IsCjk(lastChar) && IsCjk(firstChar))
            {
                // 两端都是中文：直接连接，避免出现"中文 中文"的多余空格
            }
            else
            {
                sb.Append(' ');
            }
        }

        sb.Append(text);
    }
}
