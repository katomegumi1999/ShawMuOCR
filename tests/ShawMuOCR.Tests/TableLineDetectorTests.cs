using System;
using System.Collections.Generic;
using System.Diagnostics;
using ShawMuOCR.Core.Sorting;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace ShawMuOCR.Tests;

/// <summary>
/// 表格线检测测试。
///
/// 背景：OCR 文本检测框会按比例向外膨胀，紧凑表格的左右两列在膨胀后可能贴合甚至重叠，
/// 此时「框间空白」不足以切列，投影法会在临界点附近随机翻转 —— 同一张图有时分两列、有时不分。
/// 表格自带的竖线是像素级稳定证据，本类验证它能被可靠检出并转成列区间。
/// </summary>
public class TableLineDetectorTests
{
    private readonly ITestOutputHelper _output;

    public TableLineDetectorTests(ITestOutputHelper output) => _output = output;

    /// <summary>画一张带黑色边框与内部分隔线的规整表格</summary>
    private static SKBitmap RenderGrid(
        int width,
        int height,
        int[] verticalLines,
        int[] rowHeight,
        SKColor? lineColor = null,
        SKColorType colorType = SKColorType.Bgra8888)
    {
        var info = new SKImageInfo(width, height, colorType, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);

        using var paint = new SKPaint
        {
            Color = lineColor ?? SKColors.Black,
            StrokeWidth = 2,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };

        foreach (int x in verticalLines)
        {
            canvas.DrawLine(x, 0, x, height - 1, paint);
        }

        for (int y = 0; y < height; y += rowHeight[0])
        {
            canvas.DrawLine(0, y, width - 1, y, paint);
        }
        canvas.DrawLine(0, height - 1, width - 1, height - 1, paint);

        return bitmap;
    }

    /// <summary>
    /// 零拷贝快路径按位图字节序解 R/G/B：Bgra8888 与 Rgba8888 的通道顺序相反，
    /// 解错了会得到错误的灰度、进而漏检竖线。两种格式必须给出相同结果。
    /// </summary>
    [Theory]
    [InlineData(SKColorType.Bgra8888)]
    [InlineData(SKColorType.Rgba8888)]
    public void TryDetectColumns_BothPixelOrders_DetectSameColumns(SKColorType colorType)
    {
        using var bitmap = RenderGrid(311, 270, new[] { 0, 154, 310 }, new[] { 45 }, colorType: colorType);

        bool ok = TableLineDetector.TryDetectColumns(bitmap, out var columns, out string reason);

        Assert.True(ok, reason);
        Assert.Equal(2, columns.Count);
        Assert.InRange(columns[0].Right, 152, 157);
        Assert.Equal(311, columns[1].Right);
    }

    [Fact]
    public void TryDetectColumns_TwoColumnGridWithInnerLine_ReturnsTwoColumns()
    {
        // 对应「6 行 2 列坐标表」：外框在 0 / 310，内部竖线在 154
        using var bitmap = RenderGrid(311, 270, new[] { 0, 154, 310 }, new[] { 45 });

        bool ok = TableLineDetector.TryDetectColumns(bitmap, out var columns, out string reason);

        Assert.True(ok, reason);
        Assert.Equal(2, columns.Count);

        // 线宽 2px 且采样有步长，检测到的中心允许 ±2px 误差，但必须首尾相接、覆盖整幅图宽
        Assert.Equal(0, columns[0].Left);
        Assert.InRange(columns[0].Right, 152, 157);
        Assert.Equal(columns[0].Right, columns[1].Left);
        Assert.Equal(311, columns[1].Right);
    }

    [Fact]
    public void TryDetectColumns_ThreeColumnGrid_ReturnsThreeColumns()
    {
        using var bitmap = RenderGrid(600, 300, new[] { 0, 200, 400, 599 }, new[] { 50 });

        bool ok = TableLineDetector.TryDetectColumns(bitmap, out var columns, out string reason);

        Assert.True(ok, reason);
        Assert.Equal(3, columns.Count);
    }

    [Fact]
    public void TryDetectColumns_NoLinesAtAll_ReturnsFalse()
    {
        // 空白图（模拟纯文字截图没有表格线）
        using var bitmap = new SKBitmap(300, 200);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
        }

        bool ok = TableLineDetector.TryDetectColumns(bitmap, out var columns, out string reason);

        Assert.False(ok);
        Assert.Empty(columns);
        Assert.Contains("未检测到", reason);
    }

    [Fact]
    public void TryDetectColumns_OnlyOuterBorder_ReturnsFalse()
    {
        // 只有截图/文本框外框、没有内部分隔线时不能切列，否则会把一句话切成两半
        using var bitmap = new SKBitmap(400, 200);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black, StrokeWidth = 2, Style = SKPaintStyle.Stroke };
            canvas.DrawRect(new SKRect(0, 0, 399, 199), paint);
        }

        bool ok = TableLineDetector.TryDetectColumns(bitmap, out var columns, out string reason);

        Assert.False(ok);
        Assert.Empty(columns);
        Assert.Contains("外框", reason);
    }

    [Fact]
    public void TryDetectColumns_LightGrayGridLines_AreDetected()
    {
        // Excel / WPS 默认网格线是浅灰（约 #D0D0D0，亮度 208），绝对深色阈值抓不到，
        // 需要靠「相对背景亮度」的自适应阈值兜底
        var gray = new SKColor(0xD0, 0xD0, 0xD0);
        using var bitmap = RenderGrid(400, 240, new[] { 0, 200, 399 }, new[] { 40 }, gray);

        bool ok = TableLineDetector.TryDetectColumns(bitmap, out var columns, out string reason);

        Assert.True(ok, reason);
        Assert.Equal(2, columns.Count);
    }

    [Fact]
    public void TryDetectColumns_NullBitmap_ReturnsFalse()
    {
        bool ok = TableLineDetector.TryDetectColumns(null, out var columns, out string reason);

        Assert.False(ok);
        Assert.Empty(columns);
        Assert.Contains("无源图像", reason);
    }

    [Fact]
    public void TryDetectColumns_TinyBitmap_ReturnsFalse()
    {
        using var bitmap = new SKBitmap(8, 8);

        bool ok = TableLineDetector.TryDetectColumns(bitmap, out _, out string reason);

        Assert.False(ok);
        Assert.Contains("过小", reason);
    }

    /// <summary>
    /// 数据安全底线：检测只读像素，绝不修改传入的图像。
    /// </summary>
    [Fact]
    public void TryDetectColumns_DoesNotMutateSourceBitmap()
    {
        using var bitmap = RenderGrid(311, 270, new[] { 0, 154, 310 }, new[] { 45 });
        var before = bitmap.GetPixel(10, 100);

        TableLineDetector.TryDetectColumns(bitmap, out _, out _);

        Assert.Equal(before, bitmap.GetPixel(10, 100));
    }

    /// <summary>
    /// 性能护栏：分列判定发生在每次识别之后，必须保持在个位数毫秒级。
    /// 下采样后统计量与图像面积基本无关，4K 全屏截图不应比小图慢一个数量级。
    /// </summary>
    [Theory]
    [InlineData(311, 270)]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    public void TryDetectColumns_LargeScreenshot_StaysFast(int width, int height)
    {
        using var bitmap = RenderGrid(width, height, new[] { 0, width / 2, width - 1 }, new[] { Math.Max(40, height / 8) });

        TableLineDetector.TryDetectColumns(bitmap, out _, out _); // 预热，排除 JIT 开销

        var sw = Stopwatch.StartNew();
        TableLineDetector.TryDetectColumns(bitmap, out var columns, out string reason);
        sw.Stop();

        _output.WriteLine($"{width}×{height} 耗时 {sw.Elapsed.TotalMilliseconds:F1} ms | {reason}");

        Assert.NotEmpty(columns);
        Assert.True(
            sw.ElapsedMilliseconds < 500,
            $"{width}×{height} 表格线检测耗时 {sw.ElapsedMilliseconds} ms，超出预算");
    }
}
