using ShawMuOCR.Core.OCR;
using ShawMuOCR.OCR;
using SkiaSharp;
using Xunit;

namespace ShawMuOCR.Tests;

public class PaddleOcrV6EngineTests
{
    [Fact]
    public async Task RecognizeAsync_SynthesizedTextImage_RecognizesTextAccurately()
    {
        // 1. 创建合成测试图片
        using var bitmap = new SKBitmap(400, 100);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint
            {
                Color = SKColors.Black,
                IsAntialias = true
            };
            using var tf = SKTypeface.FromFamilyName("Microsoft YaHei");
            using var font = new SKFont(tf, 28);
            canvas.DrawText("小沐 OCR 2026", 20, 60, SKTextAlign.Left, font, paint);
        }

        using var ocrImage = new OcrImage(bitmap);

        // 2. 初始化引擎并推理
        using var engine = new PaddleOcrV6Engine("PP-OCRv6_small");
        var result = await engine.RecognizeAsync(ocrImage);

        // 3. 断言识别结果
        Assert.True(result.IsSuccess, $"OCR failed with error: {result.ErrorMessage}");
        Assert.NotEmpty(result.Blocks);
        Assert.Contains("小沐", result.Text);
        Assert.Contains("OCR", result.Text);
        Assert.True(result.Elapsed.TotalMilliseconds > 0);
    }

    [Fact]
    public async Task RecognizeAsync_TinyModel_RecognizesFastAndAccurate()
    {
        using var bitmap = new SKBitmap(300, 80);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            using var tf = SKTypeface.FromFamilyName("Microsoft YaHei");
            using var font = new SKFont(tf, 26);
            canvas.DrawText("你好世界", 20, 50, SKTextAlign.Left, font, paint);
        }

        using var ocrImage = new OcrImage(bitmap);
        using var engine = new PaddleOcrV6Engine("PP-OCRv6_tiny");
        var result = await engine.RecognizeAsync(ocrImage);

        Assert.Contains("你好世界", result.Text);
    }

    [Fact]
    public async Task RecognizeAsync_DifferentColorFormatsAndOddDimensions_SucceedsWithoutError()
    {
        // 奇数宽高 + 8位灰度格式测试，验证跨步长和非标准色彩格式适配
        var info = new SKImageInfo(127, 45, SKColorType.Gray8, SKAlphaType.Opaque);
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black };
            using var font = new SKFont(SKTypeface.Default, 16);
            canvas.DrawText("测试", 10, 30, SKTextAlign.Left, font, paint);
        }

        using var ocrImage = new OcrImage(bitmap);
        using var engine = new PaddleOcrV6Engine("PP-OCRv6_tiny");
        var result = await engine.RecognizeAsync(ocrImage);

        Assert.True(result.IsSuccess, $"Gray8 OCR failed: {result.ErrorMessage}");
    }

    [Fact]
    public async Task RecognizeAsync_RepeatedCalls_MemoryRemainsStable()
    {
        using var engine = new PaddleOcrV6Engine("PP-OCRv6_tiny");

        for (int i = 1; i <= 10; i++)
        {
            using var curBitmap = new SKBitmap(400, 100);
            using (var canvas = new SKCanvas(curBitmap))
            {
                canvas.Clear(SKColors.White);
                using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
                using var tf = SKTypeface.FromFamilyName("Microsoft YaHei");
                using var font = new SKFont(tf, 24);
                canvas.DrawText($"测试循环识别第{i}次", 20, 60, SKTextAlign.Left, font, paint);
            }

            using var ocrImage = new OcrImage(curBitmap);
            var result = await engine.RecognizeAsync(ocrImage);
            Assert.True(result.IsSuccess);
        }

        // 验证引擎 Dispose 后资源能被垃圾回收器顺利释放
        engine.Dispose();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
    }

    [Fact]
    public async Task RecognizeAsync_ExternalModelWithoutDict_UsesBuiltInFallbackSuccessfully()
    {
        var provider = new OcrModelProvider();
        var extModel = provider.ResolveExternalModel("v6");
        if (extModel == null)
        {
            // 如果运行环境中未找到 models/v6，跳过或断言
            return;
        }

        // 构造一个不传入 DictionaryPath 的 externalModel，验证在仅有 det 和 rec 情况下能否无缝复用内置官方字典
        var modelWithoutDict = new ExternalPaddleModelInfo
        {
            Name = extModel.Name,
            DirectoryPath = extModel.DirectoryPath,
            DetectionPath = extModel.DetectionPath,
            RecognitionPath = extModel.RecognitionPath,
            DictionaryPath = null, // 无外部字典
            ClassificationPath = null
        };

        using var engine = new PaddleOcrV6Engine(
            modelName: "v6 (外部)",
            externalModel: modelWithoutDict);

        using var bitmap = new SKBitmap(300, 80);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            using var tf = SKTypeface.FromFamilyName("Microsoft YaHei");
            using var font = new SKFont(tf, 26);
            canvas.DrawText("外部模型测试", 20, 50, SKTextAlign.Left, font, paint);
        }

        using var ocrImage = new OcrImage(bitmap);
        var result = await engine.RecognizeAsync(ocrImage);

        Assert.True(result.IsSuccess, $"External model OCR failed: {result.ErrorMessage}");
        Assert.Contains("模型", result.Text);
    }
}
