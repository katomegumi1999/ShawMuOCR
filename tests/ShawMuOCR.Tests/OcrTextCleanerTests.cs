using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.Core.TextClean;
using Xunit;

namespace ShawMuOCR.Tests;

public class OcrTextCleanerTests
{
    [Fact]
    public void DefaultSettings_ShouldBeDisabled_AndDefaultToKeepMode()
    {
        var settings = new TextCleanSettings();

        Assert.False(settings.Enabled);
        Assert.Equal(TextCleanMode.Keep, settings.Mode);
        Assert.True(settings.Chinese);
        Assert.True(settings.English);
        Assert.True(settings.Number);
        Assert.True(settings.Space);
        Assert.True(settings.FullWidthPunctuation);
        Assert.True(settings.HalfWidthPunctuation);
        Assert.True(settings.LineBreak);
        Assert.True(settings.Tab);
    }

    [Fact]
    public void Clean_WhenDisabled_ReturnsOriginalStringReference()
    {
        var settings = new TextCleanSettings { Enabled = false };
        string input = "Hello 世界 123! \t\n";

        string result = OcrTextCleaner.Clean(input, settings);

        Assert.Same(input, result);
    }

    [Fact]
    public void Clean_WhenAllMatched_ReturnsOriginalStringReferenceWithoutAllocation()
    {
        // 开启保留模式，并且包含全部规则
        var settings = new TextCleanSettings
        {
            Enabled = true,
            Mode = TextCleanMode.Keep
        };
        string input = "Hello 世界 123，！\t\n";

        string result = OcrTextCleaner.Clean(input, settings);

        // 全部字符都在保留规则内，返回原字符串引用
        Assert.Same(input, result);
    }

    [Fact]
    public void KeepMode_OnlyChinese_RetainsOnlyChineseCharacters()
    {
        var settings = new TextCleanSettings
        {
            Enabled = true,
            Mode = TextCleanMode.Keep,
            Chinese = true,
            English = false,
            Number = false,
            Space = false,
            FullWidthPunctuation = false,
            HalfWidthPunctuation = false,
            LineBreak = false,
            Tab = false
        };

        string input = "小沐 OCR 识别：100% 正确！\n\tWell done.";
        string result = OcrTextCleaner.Clean(input, settings);

        Assert.Equal("小沐识别正确", result);
    }

    [Fact]
    public void KeepMode_OnlyEnglish_RetainsOnlyEnglishLetters()
    {
        var settings = new TextCleanSettings
        {
            Enabled = true,
            Mode = TextCleanMode.Keep,
            Chinese = false,
            English = true,
            Number = false,
            Space = false,
            FullWidthPunctuation = false,
            HalfWidthPunctuation = false,
            LineBreak = false,
            Tab = false
        };

        string input = "小沐 OCR 2026! Hello World.";
        string result = OcrTextCleaner.Clean(input, settings);

        Assert.Equal("OCRHelloWorld", result);
    }

    [Fact]
    public void KeepMode_OnlyNumbers_RetainsOnlyDigits()
    {
        var settings = new TextCleanSettings
        {
            Enabled = true,
            Mode = TextCleanMode.Keep,
            Chinese = false,
            English = false,
            Number = true,
            Space = false,
            FullWidthPunctuation = false,
            HalfWidthPunctuation = false,
            LineBreak = false,
            Tab = false
        };

        string input = "订单号：NO-2026-9527，总金额 $100.50";
        string result = OcrTextCleaner.Clean(input, settings);

        Assert.Equal("2026952710050", result);
    }

    [Fact]
    public void KeepMode_OnlySpacesAndTabsAndLineBreaks()
    {
        var settings = new TextCleanSettings
        {
            Enabled = true,
            Mode = TextCleanMode.Keep,
            Chinese = false,
            English = false,
            Number = false,
            Space = true,
            FullWidthPunctuation = false,
            HalfWidthPunctuation = false,
            LineBreak = true,
            Tab = true
        };

        string input = "A B\tC\r\n中文 123";
        string result = OcrTextCleaner.Clean(input, settings);

        Assert.Equal(" \t\r\n ", result);
    }

    [Fact]
    public void KeepMode_HalfWidthAndFullWidthPunctuation()
    {
        var settings = new TextCleanSettings
        {
            Enabled = true,
            Mode = TextCleanMode.Keep,
            Chinese = false,
            English = false,
            Number = false,
            Space = false,
            FullWidthPunctuation = true,
            HalfWidthPunctuation = true,
            LineBreak = false,
            Tab = false
        };

        string input = "你好，世界！This is a test: [100%]";
        string result = OcrTextCleaner.Clean(input, settings);

        Assert.Equal("，！:[%]", result);
    }

    [Fact]
    public void RemoveMode_RemoveSpacesAndLineBreaksAndTabs()
    {
        var settings = new TextCleanSettings
        {
            Enabled = true,
            Mode = TextCleanMode.Remove,
            Chinese = false,
            English = false,
            Number = false,
            Space = true,
            FullWidthPunctuation = false,
            HalfWidthPunctuation = false,
            LineBreak = true,
            Tab = true
        };

        string input = "小沐 OCR\r\n\t版本 2.0  好用！";
        string result = OcrTextCleaner.Clean(input, settings);

        Assert.Equal("小沐OCR版本2.0好用！", result);
    }

    [Fact]
    public void RemoveMode_RemoveHalfAndFullWidthPunctuation()
    {
        var settings = new TextCleanSettings
        {
            Enabled = true,
            Mode = TextCleanMode.Remove,
            Chinese = false,
            English = false,
            Number = false,
            Space = false,
            FullWidthPunctuation = true,
            HalfWidthPunctuation = true,
            LineBreak = false,
            Tab = false
        };

        string input = "测试：Hello, World!（你好，世界！）";
        string result = OcrTextCleaner.Clean(input, settings);

        Assert.Equal("测试Hello World你好世界", result);
    }

    [Fact]
    public void RemoveMode_WhenNothingSelected_RetainsEverything()
    {
        var settings = new TextCleanSettings
        {
            Enabled = true,
            Mode = TextCleanMode.Remove,
            Chinese = false,
            English = false,
            Number = false,
            Space = false,
            FullWidthPunctuation = false,
            HalfWidthPunctuation = false,
            LineBreak = false,
            Tab = false
        };

        string input = "没有任何字符被移除 123 !@#";
        string result = OcrTextCleaner.Clean(input, settings);

        Assert.Same(input, result);
    }

    [Fact]
    public void Clean_LargeText_OperatesCorrectlyViaPoolBuffer()
    {
        // 构造大于 512 字符的文本，测试池化缓冲路径
        string chunk = "中文123ABC，。! \t\n";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 100; i++)
        {
            sb.Append(chunk);
        }
        string largeInput = sb.ToString();
        Assert.True(largeInput.Length > 512);

        var settings = new TextCleanSettings
        {
            Enabled = true,
            Mode = TextCleanMode.Keep,
            Chinese = true,
            English = false,
            Number = false,
            Space = false,
            FullWidthPunctuation = false,
            HalfWidthPunctuation = false,
            LineBreak = false,
            Tab = false
        };

        string result = OcrTextCleaner.Clean(largeInput, settings);

        var expectedSb = new System.Text.StringBuilder(200);
        for (int i = 0; i < 100; i++) expectedSb.Append("中文");
        string expected = expectedSb.ToString();

        Assert.Equal(expected, result);
        Assert.Equal(200, result.Length);
        Assert.All(result, c => Assert.True(c == '中' || c == '文'));
    }

    [Fact]
    public void CleanResult_CleansBothTextAndBlocks()
    {
        var settings = new TextCleanSettings
        {
            Enabled = true,
            Mode = TextCleanMode.Keep,
            Chinese = true,
            English = true,
            Number = false,
            Space = false,
            FullWidthPunctuation = false,
            HalfWidthPunctuation = false,
            LineBreak = false,
            Tab = false
        };

        var originalResult = new OcrResult
        {
            Text = "识别文本 123",
            Blocks = new List<OcrTextBlock>
            {
                new() { Text = "识别 123" },
                new() { Text = "文本 ABC" }
            },
            IsSuccess = true
        };

        var cleanedResult = OcrTextCleaner.CleanResult(originalResult, settings);

        Assert.Equal("识别文本", cleanedResult.Text);
        Assert.Equal(2, cleanedResult.Blocks.Count);
        Assert.Equal("识别", cleanedResult.Blocks[0].Text);
        Assert.Equal("文本ABC", cleanedResult.Blocks[1].Text);
    }

    [Fact]
    public void SettingsPersistence_CloneAndToFlags_WorkCorrectly()
    {
        var original = new TextCleanSettings
        {
            Enabled = true,
            Mode = TextCleanMode.Remove,
            Chinese = false,
            English = true,
            Number = false,
            Space = true,
            FullWidthPunctuation = false,
            HalfWidthPunctuation = true,
            LineBreak = false,
            Tab = true
        };

        var clone = original.Clone();

        Assert.True(clone.Enabled);
        Assert.Equal(TextCleanMode.Remove, clone.Mode);
        Assert.False(clone.Chinese);
        Assert.True(clone.English);
        Assert.False(clone.Number);
        Assert.True(clone.Space);
        Assert.False(clone.FullWidthPunctuation);
        Assert.True(clone.HalfWidthPunctuation);
        Assert.False(clone.LineBreak);
        Assert.True(clone.Tab);

        var flags = clone.ToFlags();
        Assert.True(flags.HasFlag(TextCleanRuleFlags.English));
        Assert.True(flags.HasFlag(TextCleanRuleFlags.Space));
        Assert.True(flags.HasFlag(TextCleanRuleFlags.HalfWidthPunctuation));
        Assert.True(flags.HasFlag(TextCleanRuleFlags.Tab));
        Assert.False(flags.HasFlag(TextCleanRuleFlags.Chinese));
        Assert.False(flags.HasFlag(TextCleanRuleFlags.Number));
        Assert.False(flags.HasFlag(TextCleanRuleFlags.FullWidthPunctuation));
        Assert.False(flags.HasFlag(TextCleanRuleFlags.LineBreak));
    }
}
