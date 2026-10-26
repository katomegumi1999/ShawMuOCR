using System.Text.RegularExpressions;
using ShawMuOCR.Core.Sorting;
using Xunit;

namespace ShawMuOCR.Tests;

/// <summary>
/// 表格 HTML 输出测试：Excel / WPS / Word 通过 CF_HTML 粘贴时，
/// 单元格数量、合并表头 colspan 与转义都必须正确，否则会出现串列或标签外泄。
/// </summary>
public class OcrTableLayoutHtmlTests
{
    private static OcrTableLayout CreateLayout(string[][] rows, int columnCount, int[]? firstCellSpans = null)
        => new(rows.Select(r => (IReadOnlyList<string>)r).ToList(), columnCount, firstCellSpans);

    [Fact]
    public void ToHtmlTable_RegularGrid_EmitsOneRowAndCellPerItem()
    {
        var layout = CreateLayout(
            new[]
            {
                new[] { "物资名称", "数量", "单价" },
                new[] { "螺丝", "120", "3.5" },
                new[] { "垫片", "500", "" }
            },
            3);

        string html = layout.ToHtmlTable();

        Assert.Equal(3, Regex.Matches(html, "<tr>").Count);
        Assert.Equal(9, Regex.Matches(html, "<td").Count);
        Assert.Contains("<td>物资名称</td>", html);
        // 空单元格仍保留占位 td，不会让后续列左移
        Assert.Contains("<td></td>", html);
        Assert.DoesNotContain("colspan", html);
    }

    [Fact]
    public void ToHtmlTable_MergedHeaderRow_UsesColspanAndSingleCell()
    {
        var layout = CreateLayout(
            new[]
            {
                new[] { "2026 年一季度采购明细", "", "" },
                new[] { "物资名称", "数量", "单价" },
                new[] { "螺丝", "120", "3.5" }
            },
            3,
            firstCellSpans: new[] { 3, 1, 1 });

        string html = layout.ToHtmlTable();

        Assert.Contains("<td colspan=\"3\">2026 年一季度采购明细</td>", html);
        Assert.Equal(3, Regex.Matches(html, "<tr>").Count);
        // 合并行只产出一个单元格，其余行各三个
        Assert.Equal(7, Regex.Matches(html, "<td").Count);
    }

    [Fact]
    public void ToHtmlTable_EscapesSpecialCharacters()
    {
        var layout = CreateLayout(
            new[]
            {
                new[] { "a<b", "c&d" },
                new[] { "\"引号\"", ">_<" }
            },
            2);

        string html = layout.ToHtmlTable();

        Assert.Contains("<td>a&lt;b</td>", html);
        Assert.Contains("<td>c&amp;d</td>", html);
        Assert.Contains("<td>&quot;引号&quot;</td>", html);
        Assert.Contains("<td>&gt;_&lt;</td>", html);
        // 原始尖括号不得泄漏成标签
        Assert.DoesNotContain("a<b", html);
    }

    [Fact]
    public void ToHtmlTable_SpanExceedingColumnCount_IsClamped()
    {
        var layout = CreateLayout(
            new[]
            {
                new[] { "标题", "", "" },
                new[] { "A", "B", "C" }
            },
            3,
            firstCellSpans: new[] { 99, 1 });

        string html = layout.ToHtmlTable();

        Assert.Contains("colspan=\"3\"", html);
        Assert.DoesNotContain("colspan=\"99\"", html);
    }
}
