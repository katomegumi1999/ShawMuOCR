using System.IO;
using System.Threading.Tasks;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.OCR;
using ShawMuOCR.Services.Pdf;
using SkiaSharp;
using UglyToad.PdfPig;
using Xunit;
using Xunit.Abstractions;

namespace ShawMuOCR.Tests;

/// <summary>
/// PDF 整页栅格化能力测试。
/// 起因：PdfPig 只能取出「内嵌位图」，对 GIS / CAD 导出的矢量型、文字转路径型 PDF
/// 既读不到文字层也拿不到图片，整页识别结果为空（只剩页码）。
/// </summary>
[Trait("Category", "Slow")]
public class PdfPageRendererTests
{
    private readonly ITestOutputHelper _output;

    public PdfPageRendererTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void TryRenderPage_VectorPdfWithoutTextLayerOrImage_ProducesBitmap()
    {
        // 矢量路径文字 + 线条：没有文字层，也没有内嵌位图，正是旧逻辑完全失效的那类 PDF
        byte[] pdf = BuildPdf(canvas =>
        {
            using var typeface = SKTypeface.FromFamilyName("Microsoft YaHei");
            using var font = new SKFont(typeface, 24);
            using var strokeOnly = new SKPaint
            {
                Color = SKColors.Black,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 0.8f
            };
            canvas.DrawText("矢量路径文字 2026", 60, 100, SKTextAlign.Left, font, strokeOnly);

            using var line = new SKPaint { Color = SKColors.Gray, StrokeWidth = 1, IsAntialias = true };
            canvas.DrawLine(60, 140, 400, 140, line);
        });

        string path = WriteTempPdf(pdf);
        using var document = PdfDocument.Open(path);

        var page = document.GetPage(1);
        var inlineImages = page.GetImages();
        _output.WriteLine($"文字层长度={page.Text?.Length}，内嵌图片数={System.Linq.Enumerable.Count(inlineImages)}");
        // 关键前提：这类页面没有内嵌位图（旧逻辑的 GetImages 分支完全走不通）
        Assert.Empty(inlineImages);

        var bitmap = PdfPageRenderer.TryRenderPage(document, 1, out string? error);

        Assert.NotNull(bitmap);
        using (bitmap)
        {
            _output.WriteLine($"渲染尺寸={bitmap!.Width}x{bitmap.Height}");
            // 目标宽 1600px，A4 竖版高度应相应放大
            Assert.True(bitmap.Width >= 1000, $"渲染宽度过小: {bitmap.Width}");
            Assert.True(bitmap.Height > bitmap.Width, "A4 竖版渲染后高度应大于宽度");
        }
        Assert.Null(error);
    }

    [Fact]
    public async Task RenderThenOcr_ScannedStylePdf_RecognizesText()
    {
        // 整页一张位图的扫描件风格 PDF：渲染后必须能 OCR 出文字
        byte[] pdf = BuildPdf(canvas =>
        {
            using var pageBitmap = new SKBitmap(1000, 500);
            using (var inner = new SKCanvas(pageBitmap))
            {
                inner.Clear(SKColors.White);
                using var typeface = SKTypeface.FromFamilyName("Microsoft YaHei");
                using var font = new SKFont(typeface, 48);
                using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
                inner.DrawText("扫描件表格 2026", 60, 250, SKTextAlign.Left, font, paint);
            }

            using var image = SKImage.FromBitmap(pageBitmap);
            canvas.DrawImage(image, new SKRect(40, 100, 555, 357), new SKSamplingOptions());
        });

        string path = WriteTempPdf(pdf);
        using var document = PdfDocument.Open(path);

        var page = document.GetPage(1);
        _output.WriteLine($"文字层长度={page.Text?.Length}");

        using var bitmap = PdfPageRenderer.TryRenderPage(document, 1, out string? error);
        Assert.NotNull(bitmap);
        Assert.Null(error);

        using var ocrImage = new OcrImage(bitmap!.Copy());
        using var engine = new PaddleOcrV6Engine("PP-OCRv6_small");
        var result = await engine.RecognizeAsync(ocrImage);

        _output.WriteLine($"OCR 结果: {result.Text}");
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Contains("扫描件", result.Text);
    }

    private static byte[] BuildPdf(System.Action<SKCanvas> draw)
    {
        using var stream = new MemoryStream();
        using (var document = SKDocument.CreatePdf(stream))
        {
            var canvas = document.BeginPage(595, 842);
            draw(canvas);
            document.EndPage();
            document.Close();
        }
        return stream.ToArray();
    }

    private static string WriteTempPdf(byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), $"shawmu_pdf_test_{System.Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
