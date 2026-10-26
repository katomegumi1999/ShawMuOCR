using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Sorting;
using SkiaSharp;
using Xunit;

namespace ShawMuOCR.Tests;

/// <summary>
/// 表格结构还原与 TSV 生成的单元测试：
/// 覆盖规整表格、空单元格占位、合并表头、单元格内碎框、普通段落不误判等场景。
/// </summary>
public class OcrTableLayoutBuilderTests
{
    private const string CrLf = "\r\n";

    private static OcrTextBlock Block(string text, int left, int top, int width = 40, int height = 18)
        => new()
        {
            Text = text,
            Confidence = 0.99f,
            BoundingBox = new SKRectI(left, top, left + width, top + height),
            BoxPoints = Array.Empty<SKPointI>()
        };

    [Fact]
    public void TryBuild_Regular3x3Table_ProducesTsvGrid()
    {
        var blocks = new[]
        {
            Block("项目", 10, 10),
            Block("单价", 80, 10),
            Block("数量", 150, 10),
            Block("螺丝", 10, 45),
            Block("3.5", 80, 45),
            Block("120", 150, 45),
            Block("垫片", 10, 80),
            Block("1.2", 80, 80),
            Block("500", 150, 80)
        };

        var layout = OcrTableLayoutBuilder.TryBuild(blocks, out string? reason);

        Assert.NotNull(layout);
        Assert.Equal(3, layout!.RowCount);
        Assert.Equal(3, layout.ColumnCount);
        Assert.Equal(
            "项目\t单价\t数量" + CrLf +
            "螺丝\t3.5\t120" + CrLf +
            "垫片\t1.2\t500",
            layout.ToTsv());
        Assert.Contains("3 行 × 3 列", reason);
    }

    [Fact]
    public void TryBuild_EmptyCell_KeepsColumnAlignmentWithDoubleTab()
    {
        var blocks = new[]
        {
            Block("A1", 10, 10),
            Block("B1", 80, 10),
            Block("C1", 150, 10),
            Block("A2", 10, 45),
            // 第二行 B 列留空（合并 / 未填），用于验证列不错位
            Block("C2", 150, 45),
            Block("A3", 10, 80),
            Block("B3", 80, 80),
            Block("C3", 150, 80)
        };

        var layout = OcrTableLayoutBuilder.TryBuild(blocks);

        Assert.NotNull(layout);
        Assert.Equal(3, layout!.ColumnCount);
        Assert.Equal(
            "A1\tB1\tC1" + CrLf +
            "A2\t\tC2" + CrLf +
            "A3\tB3\tC3",
            layout.ToTsv());
    }

    [Fact]
    public void TryBuild_MergedTitleRowSpanningAllColumns_StillSplitsColumns()
    {
        // 首行为横跨全表的合并标题：不应把列间隙抹平
        var blocks = new[]
        {
            Block("2026 年第一季度采购明细", 10, 5, width: 185),
            Block("物资", 10, 45),
            Block("数量", 80, 45),
            Block("金额", 150, 45),
            Block("钢材", 10, 80),
            Block("12", 80, 80),
            Block("900", 150, 80)
        };

        var layout = OcrTableLayoutBuilder.TryBuild(blocks);

        Assert.NotNull(layout);
        Assert.Equal(3, layout!.RowCount);
        Assert.Equal(3, layout.ColumnCount);
    }

    [Fact]
    public void TryBuild_FragmentedBlocksInSameCell_JoinsWithoutGhostColumn()
    {
        // 同一单元格被 OCR 切成两段（间距小于列间隙阈值），应合并为同一格且中文直连
        var blocks = new[]
        {
            Block("华南", 10, 10, width: 36),
            Block("区", 54, 10, width: 18),
            Block("广州", 120, 10),
            Block("华北", 10, 45, width: 36),
            Block("区", 54, 45, width: 18),
            Block("北京", 120, 45)
        };

        var layout = OcrTableLayoutBuilder.TryBuild(blocks);

        Assert.NotNull(layout);
        Assert.Equal(2, layout!.ColumnCount);
        Assert.Equal(
            "华南区\t广州" + CrLf +
            "华北区\t北京",
            layout.ToTsv());
    }

    [Fact]
    public void TryBuild_PlainParagraph_ReturnsNull()
    {
        var blocks = new[]
        {
            Block("PaddleOCR 是一个开源的文字识别工具库，支持多语言", 10, 10, width: 400),
            Block("本项目在其基础上封装了截图与表格还原能力", 10, 45, width: 340),
            Block("所有结果都可以自动写入系统剪贴板", 10, 80, width: 300)
        };

        var layout = OcrTableLayoutBuilder.TryBuild(blocks, out string? reason);

        Assert.Null(layout);
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Fact]
    public void TryBuild_SingleLineRow_IsRecognizedAsSingleRowTable()
    {
        // 单行数据（如一行经纬度记录）同样应能分列：多个字段之间有明显横向空白
        var blocks = new[]
        {
            Block("姓名", 10, 10),
            Block("张三", 120, 10),
            Block("年龄", 240, 10)
        };

        var layout = OcrTableLayoutBuilder.TryBuild(blocks);

        Assert.NotNull(layout);
        Assert.Equal(1, layout!.RowCount);
        Assert.Equal(3, layout.ColumnCount);
        Assert.Equal("姓名\t张三\t年龄", layout.ToTsv());
    }

    [Fact]
    public void TryBuild_TightColumnsWithNarrowGap_SplitsAllColumns()
    {
        // 长数字并排时列间空白很窄（约 18px，不足一个行高），仍须切成多列而不是整行挤在一格
        var blocks = new[]
        {
            Block("J1", 26, 17, width: 48, height: 44),
            Block("2518217.622", 115, 21, width: 208, height: 37),
            Block("37569912.061", 341, 21, width: 226, height: 36)
        };

        var layout = OcrTableLayoutBuilder.TryBuild(blocks);

        Assert.NotNull(layout);
        Assert.Equal(3, layout!.ColumnCount);
        Assert.Equal("J1\t2518217.622\t37569912.061", layout.ToTsv());
    }

    [Fact]
    public void TryBuild_TooFewBlocks_ReturnsNull()
    {
        Assert.Null(OcrTableLayoutBuilder.TryBuild(Array.Empty<OcrTextBlock>()));
        Assert.Null(OcrTableLayoutBuilder.TryBuild(null));
        Assert.Null(OcrTableLayoutBuilder.TryBuild(new[] { Block("单框", 10, 10) }));
    }

    [Fact]
    public void ToTsv_SanitizesTabAndNewlineInsideCell()
    {
        var blocks = new[]
        {
            Block("A\tB", 10, 10),
            Block("C", 80, 10),
            Block("D\r\nE", 10, 45),
            Block("F", 80, 45)
        };

        var layout = OcrTableLayoutBuilder.TryBuild(blocks);

        Assert.NotNull(layout);
        Assert.Equal(
            "A B\tC" + CrLf +
            "D E\tF",
            layout!.ToTsv());
        Assert.Equal(4, layout.NonEmptyCellCount);
    }

    [Fact]
    public void TryBuild_UnorderedBlocks_SortsRowsTopToBottomAndCellsLeftToRight()
    {
        var blocks = new[]
        {
            Block("B2", 80, 45),
            Block("A1", 10, 10),
            Block("A2", 10, 45),
            Block("B1", 80, 10)
        };

        var layout = OcrTableLayoutBuilder.TryBuild(blocks);

        Assert.NotNull(layout);
        Assert.Equal("A1\tB1" + CrLf + "A2\tB2", layout!.ToTsv());
    }
}
