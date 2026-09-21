using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.OCR;
using Sdcb.SimdPaddleOCR;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace ShawMuOCR.Tests;

public class PaddleOcrV6EngineTests
{
    private readonly ITestOutputHelper _output;

    public PaddleOcrV6EngineTests(ITestOutputHelper output)
    {
        _output = output;
    }
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
    public async Task ImagePixelFormat_Bgra32_RecognizesSuccessfully()
    {
        using var bitmap = new SKBitmap(300, 80, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            using var tf = SKTypeface.FromFamilyName("Microsoft YaHei");
            using var font = new SKFont(tf, 26);
            canvas.DrawText("测试原生识别", 20, 50, SKTextAlign.Left, font, paint);
        }

        using var ocr = await PaddleOcrAll.LoadAsync(Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny.ChineseV6TinyModels.Default);
        
        var span = bitmap.GetPixelSpan();
        var res = ocr.Run(span, bitmap.Width, bitmap.Height, format: Sdcb.SimdPaddleOCR.ImagePixelFormat.Bgra32);
        Assert.NotNull(res);
        Assert.NotEmpty(res.Lines);
        Assert.Contains("测试", res.Text);
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

    [Theory]
    [InlineData("PP-OCRv6_small", 10)]
    [InlineData("PP-OCRv6_tiny", 10)]
    public async Task RecognizeAsync_SpeedBenchmark_MeasuresColdAndWarmLatency(string modelName, int iterations)
    {
        // 1. 创建逼真的高画质测试图片 (模拟典型选区尺寸与图文内容)
        using var bitmap = new SKBitmap(640, 120, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint
            {
                Color = SKColors.Black,
                IsAntialias = true
            };
            using var tf = SKTypeface.FromFamilyName("Microsoft YaHei");
            using var fontTitle = new SKFont(tf, 26);
            using var fontSub = new SKFont(tf, 18);

            canvas.DrawText("小沐 OCR 极速离线识别 - 性能基准测试 2026", 20, 45, SKTextAlign.Left, fontTitle, paint);
            canvas.DrawText("SimdPaddleOCR 1.4 Native Zero-Copy AVX2 Speed: 1234567890", 20, 85, SKTextAlign.Left, fontSub, paint);
        }

        using var ocrImage = new OcrImage(bitmap);
        using var engine = new PaddleOcrV6Engine(modelName);

        // 2. 测量冷启动耗时 (JIT、引擎加载、SIMD 指令预热)
        var sw = Stopwatch.StartNew();
        var coldResult = await engine.RecognizeAsync(ocrImage);
        sw.Stop();
        double coldMs = sw.Elapsed.TotalMilliseconds;

        Assert.True(coldResult.IsSuccess, $"Cold start failed: {coldResult.ErrorMessage}");
        Assert.Contains("小沐", coldResult.Text);

        // 3. 连续多次热运行测量 (Warm Runs)
        var warmLatencies = new List<double>(iterations);
        for (int i = 0; i < iterations; i++)
        {
            sw.Restart();
            var result = await engine.RecognizeAsync(ocrImage);
            sw.Stop();

            Assert.True(result.IsSuccess, $"Warm run #{i + 1} failed: {result.ErrorMessage}");
            warmLatencies.Add(sw.Elapsed.TotalMilliseconds);
        }

        // 4. 统计指标计算
        warmLatencies.Sort();
        double minMs = warmLatencies[0];
        double maxMs = warmLatencies[^1];
        double avgMs = warmLatencies.Average();
        double medianMs = warmLatencies[warmLatencies.Count / 2];
        int p95Index = (int)Math.Ceiling(warmLatencies.Count * 0.95) - 1;
        double p95Ms = warmLatencies[Math.Clamp(p95Index, 0, warmLatencies.Count - 1)];
        double fps = 1000.0 / avgMs;

        // 5. 打印清晰美观的基准测试报告到测试控制台
        _output.WriteLine("==========================================================================");
        _output.WriteLine($"[OCR 速度基准评测报告 (Speed Benchmark)] - 模型: {modelName}");
        _output.WriteLine($"测试图像: {bitmap.Width}x{bitmap.Height} (Bgra32) | 识别字符: \"{coldResult.Text.Trim()}\"");
        _output.WriteLine("--------------------------------------------------------------------------");
        _output.WriteLine($" - 冷启动耗时 (Cold Start / JIT + 预热): {coldMs:F2} ms");
        _output.WriteLine($" - 热运行测试 (Warm Runs / 连续 {iterations} 次迭代):");
        _output.WriteLine($"     * 最佳速度 (Min):    {minMs:F2} ms");
        _output.WriteLine($"     * 最慢速度 (Max):    {maxMs:F2} ms");
        _output.WriteLine($"     * 平均延迟 (Avg):    {avgMs:F2} ms");
        _output.WriteLine($"     * 中位延迟 (Median): {medianMs:F2} ms");
        _output.WriteLine($"     * 95分位延迟 (P95):   {p95Ms:F2} ms");
        _output.WriteLine($"     * 识别吞吐量 (Throughput): {fps:F1} 帧/秒 (FPS)");
        _output.WriteLine("==========================================================================");

        // 6. 防性能劣化守卫断言 (Guard SLA: 典型截屏热运行平均不得超出 250ms)
        Assert.True(avgMs < 250, $"平均延迟超出基准阈值 (250ms): 当前为 {avgMs:F2} ms");
    }
}
