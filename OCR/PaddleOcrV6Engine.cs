using System.Diagnostics;
using Sdcb.SimdPaddleOCR;
using Sdcb.SimdPaddleOCR.Models.ChineseV6Small;
using Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Sorting;
using SkiaSharp;
using CoreOcrResult = ShawMuOCR.Core.OCR.OcrResult;

namespace ShawMuOCR.OCR;

public sealed class PaddleOcrV6Engine : IOcrEngine
{
    private readonly PaddleOcrAll _ocr;
    private readonly IOcrTextSorter _sorter;
    private bool _disposed;

    public string ModelName { get; }

    public PaddleOcrV6Engine(
        string modelName = "PP-OCRv6_small",
        IOcrTextSorter? sorter = null,
        bool enableAngle = false,
        int maxSideLen = 1280,
        string? customDirectory = null,
        ExternalPaddleModelInfo? externalModel = null)
    {
        ModelName = modelName;
        _sorter = sorter ?? new DefaultOcrTextSorter();

        var options = new PaddleOcrOptions
        {
            UseDirectionClassification = enableAngle,
            Detector = new PaddleOcrDetectorOptions
            {
                LimitSideLength = maxSideLen > 0 ? maxSideLen : 960
            }
        };

        // 1. 如果提供了 externalModel 或指定了 customDirectory，则加载外部 Paddle 模型
        if (externalModel != null || !string.IsNullOrWhiteSpace(customDirectory))
        {
            string detPath;
            string recPath;
            string? dictPath;
            string? clsPath;

            if (externalModel != null)
            {
                detPath = externalModel.DetectionPath;
                recPath = externalModel.RecognitionPath;
                dictPath = externalModel.DictionaryPath;
                clsPath = externalModel.ClassificationPath;
            }
            else
            {
                string targetDir = Path.IsPathRooted(customDirectory)
                    ? customDirectory!
                    : Path.Combine(AppContext.BaseDirectory, customDirectory!);

                if (!Directory.Exists(targetDir))
                {
                    throw new DirectoryNotFoundException($"未找到指定的外部模型目录: {targetDir}");
                }

                detPath = Directory.GetFiles(targetDir, "*det*.onnx").FirstOrDefault()
                    ?? throw new FileNotFoundException($"外部模型目录 [{targetDir}] 缺少检测模型 (*det*.onnx)");
                recPath = Directory.GetFiles(targetDir, "*rec*.onnx").FirstOrDefault()
                    ?? throw new FileNotFoundException($"外部模型目录 [{targetDir}] 缺少识别模型 (*rec*.onnx)");
                dictPath = Directory.GetFiles(targetDir, "*dict*.txt").FirstOrDefault()
                    ?? Directory.GetFiles(targetDir, "*.txt").FirstOrDefault();
                clsPath = Directory.GetFiles(targetDir, "*cls*.onnx").FirstOrDefault();
            }

            // 针对 Paddle 模型：如果没有附带 txt 字典或 cls 模型，自动复用内置官方字典与方向分类模型
            using var detStream = File.OpenRead(detPath);
            using var recStream = File.OpenRead(recPath);
            using var dictStream = !string.IsNullOrEmpty(dictPath) && File.Exists(dictPath)
                ? (Stream)File.OpenRead(dictPath)
                : ChineseV6SmallModels.Default.Dictionary.OpenRead();

            using var clsStream = enableAngle
                ? (!string.IsNullOrEmpty(clsPath) && File.Exists(clsPath)
                    ? (Stream)File.OpenRead(clsPath)
                    : ChineseV6SmallModels.Default.Classification?.OpenRead())
                : null;

            _ocr = PaddleOcrAll.LoadAsync(
                detectionOnnx: detStream,
                classificationOnnx: clsStream,
                recognitionOnnx: recStream,
                dictionaryUtf8: dictStream,
                options: options).GetAwaiter().GetResult();
            return;
        }

        // 2. 默认使用 Sdcb.SimdPaddleOCR 内置的程序集嵌入模型（零外部文件依赖）
        if (string.Equals(modelName, "PP-OCRv6_tiny", StringComparison.OrdinalIgnoreCase))
        {
            _ocr = PaddleOcrAll.LoadAsync(ChineseV6TinyModels.Default, options).GetAwaiter().GetResult();
        }
        else
        {
            // 默认 PP-OCRv6_small
            _ocr = PaddleOcrAll.LoadAsync(ChineseV6SmallModels.Default, options).GetAwaiter().GetResult();
        }
    }

    public async Task<CoreOcrResult> RecognizeAsync(OcrImage image, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(image);

        var sw = Stopwatch.StartNew();
        try
        {
            var bitmap = image.Bitmap;
            if (bitmap == null || bitmap.IsNull || bitmap.Handle == IntPtr.Zero || bitmap.Width <= 0 || bitmap.Height <= 0)
            {
                return CoreOcrResult.Empty(TimeSpan.Zero);
            }

            // 1. 确定位图格式：优先直通 Bgra8888 (Windows 原生) 与 Rgba8888 原生物理内存
            SKBitmap workingBitmap = bitmap;
            bool disposeWorking = false;

            if (bitmap.ColorType != SKColorType.Bgra8888 && bitmap.ColorType != SKColorType.Rgba8888)
            {
                var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                var converted = new SKBitmap(info);
                using (var canvas = new SKCanvas(converted))
                {
                    canvas.DrawBitmap(bitmap, 0, 0, SKSamplingOptions.Default);
                }
                workingBitmap = converted;
                disposeWorking = true;
            }

            int width = workingBitmap.Width;
            int height = workingBitmap.Height;
            PaddleOcrResult rawResult;
            try
            {
                var pixelFormat = workingBitmap.ColorType == SKColorType.Rgba8888
                    ? ImagePixelFormat.Rgba32
                    : ImagePixelFormat.Bgra32;

                // 在后台线程调度纯 C# SIMD 推理，确保绝对不阻塞 UI 线程
                // 在工作线程栈上直接获取原生连续像素 Span，零中间内存分配与零拷贝！
                rawResult = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var pixelSpan = workingBitmap.GetPixelSpan();
                    return _ocr.Run(pixelSpan, width, height, format: pixelFormat);
                }, cancellationToken);
            }
            finally
            {
                if (disposeWorking)
                {
                    workingBitmap.Dispose();
                }
            }

            sw.Stop();

            if (rawResult.Lines == null || rawResult.Lines.Length == 0)
            {
                return CoreOcrResult.Empty(sw.Elapsed);
            }

            var blocks = new List<OcrTextBlock>(rawResult.Lines.Length);
            foreach (var line in rawResult.Lines)
            {
                if (string.IsNullOrWhiteSpace(line.Text))
                {
                    continue;
                }

                var box = line.Box;
                var boxPoints = new SKPointI[]
                {
                    new((int)Math.Round(box.X1), (int)Math.Round(box.Y1)),
                    new((int)Math.Round(box.X2), (int)Math.Round(box.Y2)),
                    new((int)Math.Round(box.X3), (int)Math.Round(box.Y3)),
                    new((int)Math.Round(box.X4), (int)Math.Round(box.Y4)),
                };

                int minX = (int)Math.Min(Math.Min(box.X1, box.X2), Math.Min(box.X3, box.X4));
                int minY = (int)Math.Min(Math.Min(box.Y1, box.Y2), Math.Min(box.Y3, box.Y4));
                int maxX = (int)Math.Max(Math.Max(box.X1, box.X2), Math.Max(box.X3, box.X4));
                int maxY = (int)Math.Max(Math.Max(box.Y1, box.Y2), Math.Max(box.Y3, box.Y4));

                var boundingBox = new SKRectI(
                    Math.Clamp(minX, 0, width),
                    Math.Clamp(minY, 0, height),
                    Math.Clamp(maxX, 0, width),
                    Math.Clamp(maxY, 0, height));

                blocks.Add(new OcrTextBlock
                {
                    Text = line.Text,
                    Confidence = line.RecognitionScore,
                    BoundingBox = boundingBox,
                    BoxPoints = boxPoints
                });
            }

            // 按规划文档第十六节要求进行 Y-X 几何拓扑排序拼接
            string formattedText = _sorter.SortAndFormat(blocks);

            return new CoreOcrResult
            {
                Text = formattedText,
                Blocks = blocks,
                Elapsed = sw.Elapsed,
                IsSuccess = true
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return CoreOcrResult.Failed(ex.Message, sw.Elapsed);
        }
    }


    public void Dispose()
    {
        if (!_disposed)
        {
            _ocr.Dispose();
            _disposed = true;
        }
    }
}
