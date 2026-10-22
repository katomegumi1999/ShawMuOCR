using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ShawMuOCR.Core.Batch;
using ShawMuOCR.Core.Export;
using ShawMuOCR.Core.Logging;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Scheduling;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.Core.Sorting;
using ShawMuOCR.ViewModels;
using SkiaSharp;
using UglyToad.PdfPig;

namespace ShawMuOCR.Services.Batch;

/// <summary>
/// 批量 OCR 转换调度执行器，支持纯 .NET 多核并行调度、CPU 利用率占空比限流与原生格式导出
/// </summary>
public sealed class BatchOcrProcessor
{
    private readonly IOcrEngineFactory _engineFactory;
    private readonly ISettingsService _settingsService;
    private readonly ILoggerService? _logger;
    private readonly IOcrTextSorter _textSorter = new DefaultOcrTextSorter();
    private CpuThrottleScheduler? _activeScheduler;

    public event Action<string>? LogProduced;

    public BatchOcrProcessor(
        IOcrEngineFactory engineFactory,
        ISettingsService settingsService,
        ILoggerService? logger = null)
    {
        _engineFactory = engineFactory ?? throw new ArgumentNullException(nameof(engineFactory));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _logger = logger;
    }

    /// <summary>
    /// 批量处理运行中实时动态更新 CPU 调度配置
    /// </summary>
    public void UpdateCpuSettings(int maxCores, int cpuLimitPercent)
    {
        _activeScheduler?.UpdateSettings(maxCores, cpuLimitPercent);
    }

    private static void RunOnUI(Action action)
    {
        if (Avalonia.Application.Current == null || Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    /// <summary>
    /// 执行整个批次的处理
    /// </summary>
    public async Task ProcessBatchAsync(
        IReadOnlyList<BatchOcrItemViewModel> items,
        BatchOcrOptions options,
        Action<int, int>? progressCallback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(options);

        int maxCores = Math.Clamp(options.MaxCores, 1, Math.Max(8, Environment.ProcessorCount));
        int cpuLimit = Math.Clamp(options.CpuLimitPercent, 10, 100);

        _logger?.Info($"开始批量 OCR 转换任务: 待处理文件数={items.Count}, 核心并发上限={maxCores}, CPU利用率上限={cpuLimit}%");

        using var enginePool = new OcrEnginePool(_engineFactory, _settingsService.Current, maxCores);
        using var scheduler = new CpuThrottleScheduler(maxCores, cpuLimit);
        _activeScheduler = scheduler;

        int completedCount = 0;
        int totalCount = items.Count;

        var tasks = items.Select(async item =>
        {
            CpuScheduleLease lease;
            try
            {
                lease = await scheduler.AcquireLeaseAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                RunOnUI(() => item.MarkCancelled());
                throw;
            }

            using (lease)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ProcessSingleItemAsync(item, options, enginePool, lease, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    RunOnUI(() => item.MarkCancelled());
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.Error($"批量处理单项未知异常: {item.FilePath}", ex);
                    RunOnUI(() => item.MarkFailed(ex.Message, TimeSpan.Zero));
                    LogProduced?.Invoke($"✕ 异常: {item.FileName} - {ex.Message}");
                }
                finally
                {
                    Interlocked.Increment(ref completedCount);
                    progressCallback?.Invoke(completedCount, totalCount);
                }
            }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            foreach (var item in items)
            {
                if (item.Status is BatchItemStatus.Pending or BatchItemStatus.Processing)
                {
                    RunOnUI(() => item.MarkCancelled());
                }
            }
            throw;
        }
        finally
        {
            _activeScheduler = null;
        }

        _logger?.Info($"批量 OCR 转换任务结束: 总计 {totalCount} 项，完成 {completedCount} 项");
    }

    private async Task ProcessSingleItemAsync(
        BatchOcrItemViewModel item,
        BatchOcrOptions options,
        OcrEnginePool enginePool,
        CpuScheduleLease lease,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(item.FilePath))
        {
            RunOnUI(() => item.MarkFailed("文件不存在或已被移除", TimeSpan.Zero));
            LogProduced?.Invoke($"✕ 失败: {item.FileName} (文件不存在或已被移除)");
            return;
        }

        var sw = Stopwatch.StartNew();
        RunOnUI(() => item.MarkProcessing("正在读取文件..."));
        LogProduced?.Invoke($"▶ 开始处理: {item.FileName}");

        try
        {
            string ext = Path.GetExtension(item.FilePath).ToLowerInvariant();
            string recognizedText;

            if (ext == ".pdf")
            {
                recognizedText = await ProcessPdfFileAsync(item, options, enginePool, lease, cancellationToken);
            }
            else
            {
                recognizedText = await ProcessImageFileAsync(item, options, enginePool, lease, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // 执行多格式文件导出
            RunOnUI(() => item.MarkProcessing("正在导出转换文件..."));
            await ExportResultFilesAsync(item, recognizedText, options, cancellationToken);

            sw.Stop();
            RunOnUI(() => item.MarkCompleted(recognizedText, sw.Elapsed));
            LogProduced?.Invoke($"✓ 完成: {item.FileName} ({recognizedText.Length} 字, {sw.Elapsed.TotalSeconds:F1}s)");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger?.Error($"文件 OCR 转换失败: {item.FilePath}", ex);
            RunOnUI(() => item.MarkFailed(ex.Message, sw.Elapsed));
            LogProduced?.Invoke($"✕ 失败: {item.FileName} - {ex.Message}");
        }
    }

    private async Task<string> ProcessImageFileAsync(
        BatchOcrItemViewModel item,
        BatchOcrOptions options,
        OcrEnginePool enginePool,
        CpuScheduleLease lease,
        CancellationToken cancellationToken)
    {
        var engine = enginePool.Rent();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RunOnUI(() => item.MarkProcessing("正在解码图片..."));

            using var stream = File.OpenRead(item.FilePath);
            using var normalizedBitmap = ImageOrientationHelper.DecodeAndOrient(stream, options.ImageRotation);
            if (normalizedBitmap == null)
            {
                throw new InvalidDataException("图片解码失败，请确认文件格式有效");
            }

            using var ocrImage = new OcrImage(normalizedBitmap);
            RunOnUI(() => item.MarkProcessing("正在执行 OCR 识别..."));

            var ocrSw = Stopwatch.StartNew();
            var result = await engine.RecognizeAsync(ocrImage, cancellationToken);
            ocrSw.Stop();

            // CPU 利用率占空比节流调节
            await lease.ThrottleAsync(ocrSw.ElapsedMilliseconds, cancellationToken);

            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(result.ErrorMessage ?? "OCR 识别未获得有效结果");
            }

            RunOnUI(() =>
            {
                item.ProcessedPages = 1;
                item.Progress = 100;
            });

            string text = options.SplitColumnsByBlocks && result.Blocks.Count > 0
                ? _textSorter.SortAndFormat(result.Blocks, separateColumns: true)
                : (result.Text ?? string.Empty);

            return text;
        }
        finally
        {
            enginePool.Return(engine);
        }
    }

    private async Task<string> ProcessPdfFileAsync(
        BatchOcrItemViewModel item,
        BatchOcrOptions options,
        OcrEnginePool enginePool,
        CpuScheduleLease lease,
        CancellationToken cancellationToken)
    {
        var engine = enginePool.Rent();
        try
        {
            var sb = new StringBuilder();

            await Task.Run(async () =>
            {
                using var document = PdfDocument.Open(item.FilePath);
                int totalPages = document.NumberOfPages;

                RunOnUI(() =>
                {
                    item.TotalPages = totalPages;
                    item.ProcessedPages = 0;
                });

                for (int pageNum = 1; pageNum <= totalPages; pageNum++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int current = pageNum;
                    RunOnUI(() =>
                    {
                        item.ProcessedPages = current;
                        item.Progress = (double)current / totalPages * 100.0;
                        item.MarkProcessing($"正在处理 (第 {current}/{totalPages} 页)...");
                    });

                    var page = document.GetPage(pageNum);
                    int pageRotation = (int)page.Rotation.Value;
                    int targetRotation = ImageOrientationHelper.ResolvePdfRotation(pageRotation, options.ImageRotation);

                    var pageText = page.Text?.Trim();
                    var images = page.GetImages().ToList();

                    sb.AppendLine($"--- 第 {pageNum} 页 ---");

                    if (!string.IsNullOrEmpty(pageText))
                    {
                        sb.AppendLine(pageText);
                    }

                    if (images.Count > 0)
                    {
                        foreach (var img in images)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                byte[]? bytes = null;
                                if (img.TryGetPng(out var png))
                                {
                                    bytes = png;
                                }
                                else
                                {
                                    bytes = img.RawBytes.ToArray();
                                }

                                if (bytes != null && bytes.Length > 0)
                                {
                                    using var rawBmp = SKBitmap.Decode(bytes);
                                    if (rawBmp != null)
                                    {
                                        using var orientedBmp = ImageOrientationHelper.Rotate(rawBmp, targetRotation);
                                        using var ocrImg = new OcrImage(orientedBmp.Copy());
                                        var ocrSw = Stopwatch.StartNew();
                                        var ocrRes = await engine.RecognizeAsync(ocrImg, cancellationToken);
                                        ocrSw.Stop();

                                        await lease.ThrottleAsync(ocrSw.ElapsedMilliseconds, cancellationToken);

                                        if (ocrRes.IsSuccess)
                                        {
                                            string pageTextFormatted = options.SplitColumnsByBlocks && ocrRes.Blocks.Count > 0
                                                ? _textSorter.SortAndFormat(ocrRes.Blocks, separateColumns: true)
                                                : (ocrRes.Text ?? string.Empty);

                                            if (!string.IsNullOrWhiteSpace(pageTextFormatted))
                                            {
                                                sb.AppendLine(pageTextFormatted);
                                            }
                                        }
                                    }
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch
                            {
                                // 单个内嵌图片解码容错，继续提取后续内容
                            }
                        }
                    }

                    sb.AppendLine();
                }
            }, cancellationToken);

            return sb.ToString().Trim();
        }
        finally
        {
            enginePool.Return(engine);
        }
    }

    private async Task ExportResultFilesAsync(
        BatchOcrItemViewModel item,
        string recognizedText,
        BatchOcrOptions options,
        CancellationToken cancellationToken)
    {
        string outDir;
        if (options.UseOriginalFolder)
        {
            outDir = Path.GetDirectoryName(item.FilePath)!;
        }
        else
        {
            outDir = string.IsNullOrWhiteSpace(options.CustomOutputDir)
                ? Path.GetDirectoryName(item.FilePath)!
                : options.CustomOutputDir;
        }

        Directory.CreateDirectory(outDir);

        string suffix = options.IsSuffixEnabled && !string.IsNullOrWhiteSpace(options.SuffixText)
            ? options.SuffixText.Trim()
            : string.Empty;

        string baseName = Path.GetFileNameWithoutExtension(item.FilePath);

        if (options.ExportTxt)
        {
            string txtPath = Path.Combine(outDir, $"{baseName}{suffix}.txt");
            await BatchExportHelper.ExportTxtAsync(recognizedText, txtPath, cancellationToken);
            RunOnUI(() => item.AddGeneratedFile(txtPath));
        }

        if (options.ExportXlsx)
        {
            string xlsxPath = Path.Combine(outDir, $"{baseName}{suffix}.xlsx");
            await BatchExportHelper.ExportXlsxAsync(recognizedText, xlsxPath, cancellationToken);
            RunOnUI(() => item.AddGeneratedFile(xlsxPath));
        }

        if (options.ExportDocx)
        {
            string docxPath = Path.Combine(outDir, $"{baseName}{suffix}.docx");
            await BatchExportHelper.ExportDocxAsync(recognizedText, docxPath, cancellationToken);
            RunOnUI(() => item.AddGeneratedFile(docxPath));
        }
    }
}
