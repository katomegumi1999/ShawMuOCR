using System.Buffers;
using System.Runtime.CompilerServices;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Settings;

namespace ShawMuOCR.Core.TextClean;

/// <summary>
/// 高性能纯 C# OCR 文本清洗器
/// 针对保留模式与移除模式进行位运算掩码优化与查表优化，支持无垃圾回收（零 GC）快速过滤。
/// </summary>
public static class OcrTextCleaner
{
    private static readonly TextCleanRuleFlags[] AsciiLookup = BuildAsciiLookup();

    private static TextCleanRuleFlags[] BuildAsciiLookup()
    {
        var table = new TextCleanRuleFlags[128];

        table['\t'] = TextCleanRuleFlags.Tab;
        table['\r'] = TextCleanRuleFlags.LineBreak;
        table['\n'] = TextCleanRuleFlags.LineBreak;
        table[' '] = TextCleanRuleFlags.Space;

        for (int c = '0'; c <= '9'; c++)
        {
            table[c] = TextCleanRuleFlags.Number;
        }

        for (int c = 'a'; c <= 'z'; c++)
        {
            table[c] = TextCleanRuleFlags.English;
        }

        for (int c = 'A'; c <= 'Z'; c++)
        {
            table[c] = TextCleanRuleFlags.English;
        }

        // ASCII 标点符号 (33..47, 58..64, 91..96, 123..126)
        int[] halfPunctuationRanges =
        [
            33, 47,
            58, 64,
            91, 96,
            123, 126
        ];

        for (int i = 0; i < halfPunctuationRanges.Length; i += 2)
        {
            int start = halfPunctuationRanges[i];
            int end = halfPunctuationRanges[i + 1];
            for (int c = start; c <= end; c++)
            {
                table[c] = TextCleanRuleFlags.HalfWidthPunctuation;
            }
        }

        return table;
    }

    /// <summary>
    /// 获取指定字符所属的文本清洗分类
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TextCleanRuleFlags GetCategory(char c)
    {
        if (c < 128)
        {
            return AsciiLookup[c];
        }

        // 1. 中文（汉字）：绝大多数 OCR 场景出现频率最高，优先分支
        if ((c >= 0x4E00 && c <= 0x9FFF) || // CJK 统一表意文字基本区
            (c >= 0x3400 && c <= 0x4DBF) || // CJK 扩展 A
            (c >= 0xF900 && c <= 0xFAFF) || // CJK 兼容表意文字
            (c >= 0x2E80 && c <= 0x2EFF))   // CJK 部首补充
        {
            return TextCleanRuleFlags.Chinese;
        }

        // 2. 空格（全角空格、不换行空格及常用 Unicode 空白）
        if (c == '\u3000' || c == '\u00A0' || (c >= 0x2002 && c <= 0x200B))
        {
            return TextCleanRuleFlags.Space;
        }

        // 3. 圆角/全角标点符号（中文标点、全角符号、常用引号、破折号、省略号）
        if ((c >= 0x3001 && c <= 0x303F) || // CJK 标点符号（排除 0x3000 全角空格）
            (c >= 0xFF01 && c <= 0xFF0F) || // 全角 ASCII 符号区 1
            (c >= 0xFF1A && c <= 0xFF20) || // 全角 ASCII 符号区 2
            (c >= 0xFF3B && c <= 0xFF40) || // 全角 ASCII 符号区 3
            (c >= 0xFF5B && c <= 0xFF65) || // 全角 ASCII 符号区 4
            (c >= 0xFFE0 && c <= 0xFFEE) || // 全角货币与符号变体 (如 ￥ ￠ ￡)
            (c >= 0x2010 && c <= 0x2027) || // 通用标点：破折号(—)、省略号(…)、引号(‘’“”)、项目符(•)等
            (c >= 0x2030 && c <= 0x205E) ||
            (c >= 0xFE10 && c <= 0xFE1F) || // 竖排标点
            (c >= 0xFE30 && c <= 0xFE4F) || // CJK 兼容形式标点
            (c >= 0xFE50 && c <= 0xFE6F))   // 小写标点变体
        {
            return TextCleanRuleFlags.FullWidthPunctuation;
        }

        // 4. 数字（全角数字：０-９）
        if (c >= 0xFF10 && c <= 0xFF19)
        {
            return TextCleanRuleFlags.Number;
        }

        // 5. 英文（全角英文字母：Ａ-Ｚ, ａ-ｚ）
        if ((c >= 0xFF21 && c <= 0xFF3A) || (c >= 0xFF41 && c <= 0xFF5A))
        {
            return TextCleanRuleFlags.English;
        }

        return TextCleanRuleFlags.None;
    }

    /// <summary>
    /// 判断单个字符在给定的模式与规则下是否应保留
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ShouldKeep(char c, TextCleanMode mode, TextCleanRuleFlags activeRules)
    {
        TextCleanRuleFlags category = GetCategory(c);
        bool isMatch = (category & activeRules) != 0;

        // 保留模式：匹配选中规则则保留；
        // 移除模式：匹配选中规则则丢弃。
        return mode == TextCleanMode.Keep ? isMatch : !isMatch;
    }

    /// <summary>
    /// 清洗指定的字符串
    /// </summary>
    public static string Clean(string? text, TextCleanSettings? settings)
    {
        if (string.IsNullOrEmpty(text) || settings == null || !settings.Enabled)
        {
            return text ?? string.Empty;
        }

        return Clean(text, settings.Mode, settings.ToFlags());
    }

    /// <summary>
    /// 使用指定的模式与规则标志位清洗字符串
    /// </summary>
    public static string Clean(string? text, TextCleanMode mode, TextCleanRuleFlags activeRules)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // 第一遍扫描：快速寻找首个需要丢弃的字符
        int firstDropIndex = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (!ShouldKeep(text[i], mode, activeRules))
            {
                firstDropIndex = i;
                break;
            }
        }

        // 若全部字符都满足保留条件，零分配直接返回原字符串
        if (firstDropIndex < 0)
        {
            return text;
        }

        int maxLen = text.Length;

        // 小文本（<= 512 字符，绝大部分 OCR 截图片段）使用 stackalloc 栈内存，无堆内存分配
        if (maxLen <= 512)
        {
            Span<char> buffer = stackalloc char[maxLen];
            text.AsSpan(0, firstDropIndex).CopyTo(buffer);
            int written = firstDropIndex;

            for (int i = firstDropIndex + 1; i < text.Length; i++)
            {
                char c = text[i];
                if (ShouldKeep(c, mode, activeRules))
                {
                    buffer[written++] = c;
                }
            }

            return written == 0 ? string.Empty : new string(buffer.Slice(0, written));
        }
        else
        {
            // 长文本使用 ArrayPool 共享池化内存，用完即还，避免频繁产生 GC 碎片
            char[] rented = ArrayPool<char>.Shared.Rent(maxLen);
            try
            {
                Span<char> buffer = rented.AsSpan(0, maxLen);
                text.AsSpan(0, firstDropIndex).CopyTo(buffer);
                int written = firstDropIndex;

                for (int i = firstDropIndex + 1; i < text.Length; i++)
                {
                    char c = text[i];
                    if (ShouldKeep(c, mode, activeRules))
                    {
                        buffer[written++] = c;
                    }
                }

                return written == 0 ? string.Empty : new string(buffer.Slice(0, written));
            }
            finally
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }

    /// <summary>
    /// 清洗 OCR 识别结果对象中的文本与分块
    /// </summary>
    public static OcrResult CleanResult(OcrResult result, TextCleanSettings? settings)
    {
        if (settings == null || !settings.Enabled || !result.IsSuccess || string.IsNullOrEmpty(result.Text))
        {
            return result;
        }

        string cleanedText = Clean(result.Text, settings);

        // 如果清洗后文本和原文本完全一致（ReferenceEquals），直接返回原 result
        if (ReferenceEquals(cleanedText, result.Text))
        {
            return result;
        }

        IReadOnlyList<OcrTextBlock> cleanedBlocks = result.Blocks;
        if (result.Blocks.Count > 0)
        {
            var list = new List<OcrTextBlock>(result.Blocks.Count);
            foreach (var block in result.Blocks)
            {
                string blockText = Clean(block.Text, settings);
                list.Add(new OcrTextBlock
                {
                    Text = blockText,
                    BoundingBox = block.BoundingBox,
                    BoxPoints = block.BoxPoints,
                    Confidence = block.Confidence
                });
            }
            cleanedBlocks = list;
        }

        return new OcrResult
        {
            Text = cleanedText,
            Blocks = cleanedBlocks,
            Elapsed = result.Elapsed,
            IsSuccess = result.IsSuccess,
            ErrorMessage = result.ErrorMessage
        };
    }
}
