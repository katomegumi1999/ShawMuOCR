using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Sdcb.SimdPaddleOCR;
using Sdcb.SimdPaddleOCR.Models.ChineseV6Small;
using ShawMuOCR.Android.Platform;

namespace ShawMuOCR.Android.OCR;

/// <summary>
/// Android 端专用 OCR 推理协调服务
/// 内置 ChineseV6Small 模型包（免外部模型文件），支持手机端 CPU/GPU (Vulkan) 模式动态切换
/// </summary>
public sealed class AndroidOcrService : IDisposable
{
    private static readonly SemaphoreSlim s_gate = new(1, 1);
    private PaddleOcrAll? _ocrEngine;
    private AndroidOcrSettings _currentSettings = new();
    private bool _isInitialized;

    public AndroidOcrSettings CurrentSettings => _currentSettings;
    public bool IsInitialized => _isInitialized;
    public string ActiveModelName => "PP-OCRv6 Small (内置集成)";

    private PaddleOcrOptions CreateOptions(OcrBackend backend)
    {
        // 移动端多核核心调度：检测大图使用大核集群（最多4个大核心），避免进入低频小能效核产生栅栏等待
        int effectiveThreads = _currentSettings.DetIntraOpThreads;
        if (effectiveThreads > 4 && Environment.ProcessorCount >= 8)
        {
            // 针对典型 8 核手机 (1超大+3大+4小)，收敛到 4 线程，精准锁定高性能核心丛集
            effectiveThreads = 4;
        }

        // 行识别批处理：GPU 下批处理 6 行，CPU 下批处理 4 行，彻底告别单行低效前向传播
        int batchLines = backend == OcrBackend.Vulkan ? 6 : 4;

        AndroidCrashLogger.Log($"[OCR OPTIONS] 配置参数: 后端={backend}, 算子线程={effectiveThreads}, 行并发Worker={_currentSettings.LineWorkerCount}, 行批处理BatchLines={batchLines}, 方向分类={_currentSettings.EnableAngle}");

        return new PaddleOcrOptions
        {
            UseDirectionClassification = _currentSettings.EnableAngle,
            DetIntraOpThreads = effectiveThreads,
            LineWorkerCount = _currentSettings.LineWorkerCount,
            RecBatchLines = batchLines,
            Detector = new PaddleOcrDetectorOptions
            {
                LimitSideLength = _currentSettings.MaxSideLen > 0 ? _currentSettings.MaxSideLen : 1280,
                Backend = backend
            },
            Recognizer = new PaddleOcrRecognizerOptions
            {
                Backend = backend
            },
            Classifier = new PaddleOcrClassifierOptions
            {
                Backend = backend
            }
        };
    }

    public async Task EnsureInitializedAsync(AndroidOcrSettings? settings = null)
    {
        if (_isInitialized && _ocrEngine != null && settings == null)
        {
            return;
        }

        await s_gate.WaitAsync();
        try
        {
            if (settings != null)
            {
                _currentSettings = settings;
            }

            _ocrEngine?.Dispose();

            var backend = _currentSettings.ResolveOcrBackend();
            AndroidCrashLogger.Log($"[OCR SERVICE] 正在初始化 PaddleOcrAll，目标后端: {backend}，线程数: {_currentSettings.DetIntraOpThreads}，Worker数: {_currentSettings.LineWorkerCount}");

            var options = CreateOptions(backend);

            try
            {
                // 直接从 NuGet 内置程序集嵌入资源流式加载，零文件落盘与解压
                _ocrEngine = await PaddleOcrAll.LoadAsync(ChineseV6SmallModels.Default, options);
                AndroidCrashLogger.Log($"[OCR SERVICE] PaddleOcrAll 加载成功，当前生效后端: {backend}");
            }
            catch (Exception ex) when (backend != OcrBackend.Cpu)
            {
                AndroidCrashLogger.LogException(ex, $"[OCR SERVICE] GPU/Auto ({backend}) 初始化失败，自动安全降级至 CPU 模式");
                var cpuOptions = CreateOptions(OcrBackend.Cpu);
                _ocrEngine = await PaddleOcrAll.LoadAsync(ChineseV6SmallModels.Default, cpuOptions);
                AndroidCrashLogger.Log("[OCR SERVICE] PaddleOcrAll CPU 降级加载成功");
            }

            _isInitialized = true;

            // 对齐官方实测 --warmup 1：自动预热编译 Vulkan 计算管线，彻底消除用户首次点击的冷启动卡顿
            if (_ocrEngine != null)
            {
                WarmupEngine(_ocrEngine);
            }
        }
        catch (Exception ex)
        {
            AndroidCrashLogger.LogException(ex, "[OCR SERVICE] EnsureInitializedAsync 发生严重异常");
            throw;
        }
        finally
        {
            s_gate.Release();
        }
    }

    public async Task<OcrExecutionResult> RecognizeAsync(ImageBuffer buffer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        await EnsureInitializedAsync();
        var engine = _ocrEngine ?? throw new InvalidOperationException("OCR 引擎尚未初始化就绪");

        return await Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();

            AndroidCrashLogger.Log($"[OCR INFERENCE] 开始推理图片尺寸: {buffer.Width}x{buffer.Height} (指定格式: {buffer.PixelFormat}, 像素字节数: {buffer.Pixels.Length})");

            // 零拷贝直通：直接向 SimdPaddleOCR 传入托管像素缓冲区
            var ocrResult = engine.Run(
                buffer.Pixels,
                buffer.Width,
                buffer.Height,
                format: buffer.PixelFormat);

            // 智能双通道容错：若 RGBA 格式检测结果为 0 行，自动使用 BGRA 格式尝试一次（适配不同图形芯片底层像素排列）
            if ((ocrResult.Lines == null || ocrResult.Lines.Length == 0) && buffer.PixelFormat == ImagePixelFormat.Rgba32)
            {
                var retryResult = engine.Run(
                    buffer.Pixels,
                    buffer.Width,
                    buffer.Height,
                    format: ImagePixelFormat.Bgra32);

                if (retryResult.Lines != null && retryResult.Lines.Length > 0)
                {
                    AndroidCrashLogger.Log($"[OCR INFERENCE] Bgra32 容错重试成功！原 Rgba32 检出 0 行，Bgra32 成功检出 {retryResult.Lines.Length} 行文字");
                    ocrResult = retryResult;
                }
            }

            sw.Stop();

            var lines = new List<OcrTextLine>();
            var sb = new StringBuilder();

            if (ocrResult.Lines != null)
            {
                foreach (var line in ocrResult.Lines)
                {
                    if (!string.IsNullOrWhiteSpace(line.Text))
                    {
                        lines.Add(new OcrTextLine(line.Text, line.RecognitionScore));
                        sb.AppendLine(line.Text);
                    }
                }
            }

            string fullText = sb.Length > 0 ? sb.ToString().TrimEnd() : (ocrResult.Text ?? string.Empty);

            AndroidCrashLogger.Log($"[OCR INFERENCE] 推理完成，耗时: {sw.ElapsedMilliseconds} ms (当前后端: {_currentSettings.Backend}, 方向分类: {_currentSettings.EnableAngle})，检测框数: {ocrResult.DetectedCount}，有效识别行数: {lines.Count}");

            return new OcrExecutionResult(
                fullText,
                lines,
                sw.ElapsedMilliseconds,
                ActiveModelName,
                _currentSettings.Backend);
        }, cancellationToken);
    }

    private static void WarmupEngine(PaddleOcrAll engine)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            byte[] dummy = new byte[64 * 64 * 4];
            Array.Fill(dummy, (byte)255);
            engine.Run(dummy, 64, 64, format: ImagePixelFormat.Rgba32);
            sw.Stop();
            AndroidCrashLogger.Log($"[OCR WARMUP] 预热着色器管线就绪，耗时: {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            AndroidCrashLogger.Log($"[OCR WARMUP] 预热异常，已跳过: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _ocrEngine?.Dispose();
        _ocrEngine = null;
        _isInitialized = false;
    }
}
