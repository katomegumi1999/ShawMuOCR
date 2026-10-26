using System.Threading.Tasks;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Sorting;
using ShawMuOCR.OCR;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace ShawMuOCR.Tests;

/// <summary>
/// 表格分列的真实 OCR 端到端验证：用 PP-OCRv6 识别合成的表格图片，
/// 检验真实检测框几何位置下的行列切分与空单元格占位是否正确。
/// </summary>
[Trait("Category", "Slow")]
public class OcrTableLayoutRealOcrTests
{
    private readonly ITestOutputHelper _output;

    public OcrTableLayoutRealOcrTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task RecognizeAsync_MergedTitleTableWithBlankCells_SplitsIntoGrid()
    {
        // 首行为居中合并标题，数据区存在空单元格（第 2 行数量为空、第 4 行单价为空）
        string[][] data =
        {
            new[] { "物资名称", "数量", "单价" },
            new[] { "螺丝", "", "3.5" },
            new[] { "垫片", "500", "1.2" },
            new[] { "钢板", "8", "" }
        };

        using var bitmap = RenderTable("2026 年一季度采购明细", data, new[] { 40, 360, 640 }, rowHeight: 70, top: 40, width: 900);
        using var ocrImage = new OcrImage(bitmap);
        using var engine = new PaddleOcrV6Engine("PP-OCRv6_small");

        var result = await engine.RecognizeAsync(ocrImage);
        Assert.True(result.IsSuccess, result.ErrorMessage);

        var layout = OcrTableLayoutBuilder.TryBuild(result.Blocks, out string? reason);
        Assert.NotNull(layout);

        _output.WriteLine($"表格还原结果：{reason}");
        _output.WriteLine(layout!.ToTsv());

        Assert.Equal(3, layout.ColumnCount);
        Assert.Equal(5, layout.RowCount);

        var rows = layout.Rows;
        // 合并标题横跨全表，应落在该行最左侧单元格
        Assert.Contains("采购明细", rows[0][0]);
        Assert.Equal(string.Empty, rows[0][1]);
        Assert.Equal(string.Empty, rows[0][2]);

        // 表头与数据落在正确的列上
        Assert.Contains("物资名称", rows[1][0]);
        Assert.Contains("数量", rows[1][1]);
        Assert.Contains("单价", rows[1][2]);

        // 空单元格必须保留为空串，靠连续制表符占位，保证后续列不错位
        Assert.Contains("螺丝", rows[2][0]);
        Assert.Equal(string.Empty, rows[2][1]);
        Assert.Contains("3.5", rows[2][2]);

        Assert.Contains("8", rows[4][1]);
        Assert.Equal(string.Empty, rows[4][2]);
    }

    [Fact]
    public async Task RecognizeAsync_PlainParagraphText_IsNotTreatedAsTable()
    {
        using var bitmap = new SKBitmap(900, 220);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var typeface = SKTypeface.FromFamilyName("Microsoft YaHei");
            using var font = new SKFont(typeface, 24);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            canvas.DrawText("小沐 OCR 是一款基于 PaddleOCR 的截图识别工具", 30, 60, SKTextAlign.Left, font, paint);
            canvas.DrawText("支持框选识别、批量转换与剪贴板自动复制", 30, 110, SKTextAlign.Left, font, paint);
            canvas.DrawText("识别结果会按阅读顺序排序后写入剪贴板", 30, 160, SKTextAlign.Left, font, paint);
        }

        using var ocrImage = new OcrImage(bitmap);
        using var engine = new PaddleOcrV6Engine("PP-OCRv6_small");

        var result = await engine.RecognizeAsync(ocrImage);
        Assert.True(result.IsSuccess, result.ErrorMessage);

        var layout = OcrTableLayoutBuilder.TryBuild(result.Blocks, out string? reason);
        _output.WriteLine($"判定结果：{reason}");

        Assert.Null(layout);
    }

    [Fact]
    public async Task RecognizeAsync_CompactTableWithTightColumnGaps_SplitsIntoThreeColumns()
    {
        // 紧凑排版：列间文字空白只有 14~18px（不足一个行高），是线上最容易「挤成一格」的形态
        string[][] data =
        {
            new[] { "物资名称", "数量", "单价" },
            new[] { "螺丝", "120", "3.5" },
            new[] { "垫片", "500", "1.2" },
            new[] { "钢板", "8", "260" },
            new[] { "螺母", "45", "0.8" },
            new[] { "弹簧", "90", "1.5" }
        };
        int[] colX = { 20, 130, 194 };

        using var bitmap = new SKBitmap(300, 40 + data.Length * 40);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var typeface = SKTypeface.FromFamilyName("Microsoft YaHei");
            using var font = new SKFont(typeface, 22);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            for (int r = 0; r < data.Length; r++)
            {
                for (int c = 0; c < data[r].Length; c++)
                {
                    canvas.DrawText(data[r][c], colX[c], 30 + r * 40, SKTextAlign.Left, font, paint);
                }
            }
        }

        using var ocrImage = new OcrImage(bitmap);
        using var engine = new PaddleOcrV6Engine("PP-OCRv6_small");
        var result = await engine.RecognizeAsync(ocrImage);

        var layout = OcrTableLayoutBuilder.TryBuild(result.Blocks, out string? reason);
        _output.WriteLine($"紧凑表格：{reason}");
        if (layout != null)
        {
            _output.WriteLine(layout.ToTsv().Replace("\t", " | "));
        }

        Assert.NotNull(layout);
        Assert.Equal(3, layout!.ColumnCount);
        Assert.Equal(data.Length, layout.RowCount);
        Assert.Contains("3.5", layout.Rows[1][2]);
    }

    [Fact]
    public async Task RecognizeAsync_MixedLanguageParagraph_IsNotTreatedAsTable()
    {
        // 中英混排段落：放宽列间隙阈值后仍不得被误判成多列表格
        string[] lines =
        {
            "小沐 OCR 是一款基于 PaddleOCR 的截图识别工具",
            "识别结果：Hello World 2026-10-23 16:38",
            "支持 Alt+Z 快捷键与 Alt+X 快速截图"
        };

        using var bitmap = new SKBitmap(900, 200);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var typeface = SKTypeface.FromFamilyName("Microsoft YaHei");
            using var font = new SKFont(typeface, 24);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            for (int i = 0; i < lines.Length; i++)
            {
                canvas.DrawText(lines[i], 30, 50 + i * 50, SKTextAlign.Left, font, paint);
            }
        }

        using var ocrImage = new OcrImage(bitmap);
        using var engine = new PaddleOcrV6Engine("PP-OCRv6_small");
        var result = await engine.RecognizeAsync(ocrImage);

        var layout = OcrTableLayoutBuilder.TryBuild(result.Blocks, out string? reason);
        _output.WriteLine($"中英混排段落：{reason}");

        Assert.Null(layout);
    }

    private static SKBitmap RenderTable(string title, string[][] data, int[] colX, int rowHeight, int top, int width)
    {
        int totalRows = data.Length + (string.IsNullOrEmpty(title) ? 0 : 1);
        var bitmap = new SKBitmap(width, top + rowHeight * totalRows + 30);

        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);

        using var line = new SKPaint { Color = SKColors.Gray, StrokeWidth = 2, IsAntialias = true };
        using var typeface = SKTypeface.FromFamilyName("Microsoft YaHei");
        using var font = new SKFont(typeface, 26);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };

        for (int r = 0; r <= totalRows; r++)
        {
            int y = top + r * rowHeight;
            canvas.DrawLine(20, y, width - 20, y, line);
        }
        foreach (int cx in colX)
        {
            canvas.DrawLine(cx - 16, top, cx - 16, top + rowHeight * totalRows, line);
        }
        canvas.DrawLine(width - 20, top, width - 20, top + rowHeight * totalRows, line);

        int rowIndex = 0;
        if (!string.IsNullOrEmpty(title))
        {
            canvas.DrawText(title, (width - 40) / 2f - 60, top + 45, SKTextAlign.Left, font, paint);
            rowIndex = 1;
        }

        for (int r = 0; r < data.Length; r++)
        {
            for (int c = 0; c < data[r].Length; c++)
            {
                if (string.IsNullOrEmpty(data[r][c]))
                {
                    continue;
                }
                canvas.DrawText(data[r][c], colX[c], top + rowHeight * (rowIndex + r) + 45, SKTextAlign.Left, font, paint);
            }
        }

        return bitmap;
    }
}
