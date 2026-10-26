using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Sorting;
using ShawMuOCR.OCR;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace ShawMuOCR.Tests;

/// <summary>
/// 「表格线优先」分列路径的集成测试。
///
/// 核心场景：列间隙刚好卡在投影法阈值的临界点上 —— 同一张图 OCR 检测框偏移一两像素，
/// 投影法就会在「分成两列」与「挤成一列」之间翻转。这正是用户反馈
/// 「有时候能识别出两列有时候不行」的成因。表格线不随检测框膨胀变化，可以消除这种不确定性。
/// </summary>
public class OcrTableLayoutLineAwareTests
{
    private readonly ITestOutputHelper _output;

    public OcrTableLayoutLineAwareTests(ITestOutputHelper output) => _output = output;

    private static OcrTextBlock Block(string text, int left, int top, int width, int height = 29)
        => new()
        {
            Text = text,
            Confidence = 0.99f,
            BoundingBox = new SKRectI(left, top, left + width, top + height)
        };

    /// <summary>
    /// 构造 6 行 × 2 列的检测框：左列 x 8..146，右列 x 156..305，行高 29。
    /// 列间空白只有 10px，而行高 29px 对应的投影法阈值是 10.15px —— 恰好卡死。
    /// </summary>
    private static List<OcrTextBlock> BuildTightTwoColumnBlocks()
    {
        string[] left = { "2518105.078", "2518106.563", "2518104.459", "2518092.420", "2518094.815", "2518105.078" };
        string[] right = { "37570013.211", "37570015.279", "37570028.108", "37570026.133", "37570011.528", "37570013.211" };

        var blocks = new List<OcrTextBlock>(12);
        for (int i = 0; i < left.Length; i++)
        {
            int top = 7 + i * 44;
            blocks.Add(Block(left[i], 8, top, 138));
            blocks.Add(Block(right[i], 156, top, 149));
        }
        return blocks;
    }

    /// <summary>画一张 311×270、内部竖线位于 154 的 2 列网格</summary>
    private static SKBitmap RenderTwoColumnGrid(int verticalLineX = 154)
    {
        var bitmap = new SKBitmap(311, 270);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);

        using var paint = new SKPaint
        {
            Color = SKColors.Black,
            StrokeWidth = 2,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };

        canvas.DrawLine(0, 0, 0, 269, paint);
        canvas.DrawLine(verticalLineX, 0, verticalLineX, 269, paint);
        canvas.DrawLine(310, 0, 310, 269, paint);
        for (int y = 0; y < 270; y += 44)
        {
            canvas.DrawLine(0, y, 310, y, paint);
        }
        canvas.DrawLine(0, 269, 310, 269, paint);

        return bitmap;
    }

    [Fact]
    public void TryBuild_NarrowGapBundleByProjection_TableLinesRescue()
    {
        var blocks = BuildTightTwoColumnBlocks();

        // 1) 不带图像：只有投影法可用，临界间隙下判定失败 —— 复现用户的「有时不行」
        var byProjection = OcrTableLayoutBuilder.TryBuild(blocks, out string? projectionReason);
        _output.WriteLine($"[投影法] {(byProjection is null ? "未分列" : "已分列")} | {projectionReason}");
        Assert.Null(byProjection);
        Assert.Contains("仅检测到 1 列", projectionReason);

        // 2) 带图像：表格竖线给出稳定列边界，直接分列
        using var bitmap = RenderTwoColumnGrid();
        var byLines = OcrTableLayoutBuilder.TryBuild(blocks, bitmap, out string? lineReason);
        _output.WriteLine($"[表格线] {(byLines is null ? "未分列" : "已分列")} | {lineReason}");

        Assert.NotNull(byLines);
        Assert.Equal(6, byLines.RowCount);
        Assert.Equal(2, byLines.ColumnCount);
        Assert.Contains("表格线", lineReason);

        // 内容与列归属必须完全正确，不能串列
        Assert.Equal("2518105.078", byLines.Rows[0][0]);
        Assert.Equal("37570013.211", byLines.Rows[0][1]);
        Assert.Equal("2518094.815", byLines.Rows[4][0]);
        Assert.Equal("37570011.528", byLines.Rows[4][1]);
    }

    [Fact]
    public void TryBuild_WithoutInnerLine_FallsBackToProjection()
    {
        // 图里没有内部竖线（只有外框）时不能硬切，应回退投影法：
        // 本用例故意给出宽松列间隙，投影法能正常分列
        var blocks = new List<OcrTextBlock>
        {
            Block("物资名称", 10, 10, 60),
            Block("数量", 200, 10, 40),
            Block("螺丝", 10, 45, 40),
            Block("120", 200, 45, 30)
        };

        using var bitmap = new SKBitmap(300, 200);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
        }

        var layout = OcrTableLayoutBuilder.TryBuild(blocks, bitmap, out string? reason);
        _output.WriteLine(reason);

        Assert.NotNull(layout);
        Assert.Equal(2, layout.ColumnCount);
        Assert.Contains("投影法", reason);
    }

    [Fact]
    public void TryBuild_DecorativeLineBeyondText_DropsEmptyColumn()
    {
        // 截图右侧带进来一条无关竖线（如窗口分隔条）：文字只到 x≈305，竖线在 350，
        // 于是 (350, 420) 整列无字，必须被剔除，否则粘贴到 Excel 会多出一个空列
        var blocks = BuildTightTwoColumnBlocks();

        using var bitmap = new SKBitmap(420, 270);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint
            {
                Color = SKColors.Black,
                StrokeWidth = 2,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke
            };
            canvas.DrawLine(0, 0, 0, 269, paint);
            canvas.DrawLine(154, 0, 154, 269, paint);
            canvas.DrawLine(350, 0, 350, 269, paint);
            canvas.DrawLine(419, 0, 419, 269, paint);
            for (int y = 0; y < 270; y += 44)
            {
                canvas.DrawLine(0, y, 419, y, paint);
            }
        }

        var layout = OcrTableLayoutBuilder.TryBuild(blocks, bitmap, out string? reason);
        _output.WriteLine(reason);

        Assert.NotNull(layout);
        Assert.Equal(2, layout.ColumnCount);
        Assert.Equal("2518105.078", layout.Rows[0][0]);
        Assert.Equal("37570013.211", layout.Rows[0][1]);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task RecognizeAsync_CoordinateTableWithGridLines_SplitsTwoColumns()
    {
        // 端到端：渲染一张与用户截图同构的坐标表（311×270、2 列 6 行、带框线），走真实 OCR
        using var bitmap = RenderCoordinateTable();

        using var ocrImage = new OcrImage(bitmap);
        using var engine = new PaddleOcrV6Engine("PP-OCRv6_small");
        var result = await engine.RecognizeAsync(ocrImage);
        Assert.True(result.IsSuccess, result.ErrorMessage);

        // 投影法在这种紧凑排版下会失败（这正是用户遇到的形态）
        var byProjection = OcrTableLayoutBuilder.TryBuild(result.Blocks, out string? projectionReason);
        _output.WriteLine($"[投影法] {(byProjection is null ? "未分列" : "已分列")} | {projectionReason}");

        var layout = OcrTableLayoutBuilder.TryBuild(result.Blocks, bitmap, out string? reason);
        _output.WriteLine($"[表格线] {(layout is null ? "未分列" : "已分列")} | {reason}");
        if (layout is not null)
        {
            _output.WriteLine(layout.ToTsv().Replace("\t", " | "));
        }

        Assert.NotNull(layout);
        Assert.Equal(2, layout.ColumnCount);
        Assert.Equal(6, layout.RowCount);

        // 每一行的两列都必须有内容且互不串列
        foreach (var row in layout.Rows)
        {
            Assert.NotEqual(string.Empty, row[0]);
            Assert.NotEqual(string.Empty, row[1]);
        }
        Assert.Contains("2518", layout.Rows[0][0]);
        Assert.Contains("3757", layout.Rows[0][1]);
    }

    [Fact]
    public void TryBuildWithColumns_MatchesBitmapOverload()
    {
        // 并行化改造的前提：由调用方预先算好列区间传进来，结果必须与内部自行检测完全一致。
        var blocks = BuildTightTwoColumnBlocks();
        using var bitmap = RenderTwoColumnGrid();

        Assert.True(TableLineDetector.TryDetectColumns(bitmap, out var columns, out string lineReason), lineReason);

        var viaColumns = OcrTableLayoutBuilder.TryBuildWithColumns(blocks, columns, out string? columnsReason);
        var viaBitmap = OcrTableLayoutBuilder.TryBuild(blocks, bitmap, out string? bitmapReason);

        _output.WriteLine($"[预计算] {columnsReason}");
        _output.WriteLine($"[内部检测] {bitmapReason}");

        Assert.NotNull(viaColumns);
        Assert.NotNull(viaBitmap);
        Assert.Equal(viaBitmap.RowCount, viaColumns.RowCount);
        Assert.Equal(viaBitmap.ColumnCount, viaColumns.ColumnCount);
        Assert.Equal(viaBitmap.ToTsv(), viaColumns.ToTsv());
    }

    [Fact]
    public void TryBuildWithColumns_NullColumns_FallsBackToProjection()
    {
        // 线检测未执行或未检出时（并行分支被跳过），必须等价于原来的纯投影法行为
        var blocks = new List<OcrTextBlock>
        {
            Block("物资名称", 10, 10, 60),
            Block("数量", 200, 10, 40),
            Block("螺丝", 10, 45, 40),
            Block("120", 200, 45, 30)
        };

        var layout = OcrTableLayoutBuilder.TryBuildWithColumns(blocks, null, out string? reason);
        _output.WriteLine(reason);

        Assert.NotNull(layout);
        Assert.Equal(2, layout.ColumnCount);
        Assert.Contains("投影法", reason);
    }

    private static SKBitmap RenderCoordinateTable()
    {
        string[] left = { "2518105.078", "2518106.563", "2518104.459", "2518092.420", "2518094.815", "2518105.078" };
        string[] right = { "37570013.211", "37570015.279", "37570028.108", "37570026.133", "37570011.528", "37570013.211" };

        var bitmap = new SKBitmap(311, 270);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);

        using var linePaint = new SKPaint
        {
            Color = SKColors.Black,
            StrokeWidth = 2,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };

        canvas.DrawLine(1, 1, 1, 268, linePaint);
        canvas.DrawLine(154, 1, 154, 268, linePaint);
        canvas.DrawLine(309, 1, 309, 268, linePaint);
        for (int i = 0; i <= 6; i++)
        {
            int y = 1 + i * 44;
            canvas.DrawLine(1, y, 309, y, linePaint);
        }

        using var typeface = SKTypeface.FromFamilyName("Consolas");
        using var font = new SKFont(typeface, 22);
        using var textPaint = new SKPaint { Color = SKColors.Black, IsAntialias = true };

        for (int i = 0; i < 6; i++)
        {
            int baseline = 1 + i * 44 + 31;
            canvas.DrawText(left[i], 8, baseline, SKTextAlign.Left, font, textPaint);
            canvas.DrawText(right[i], 162, baseline, SKTextAlign.Left, font, textPaint);
        }

        return bitmap;
    }
}
