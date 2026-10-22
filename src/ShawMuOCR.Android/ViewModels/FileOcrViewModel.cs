using System.Runtime.InteropServices;
using System.Text;
using AndroidContext = global::Android.Content.Context;
using AndroidUri = global::Android.Net.Uri;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShawMuOCR.Android.OCR;
using ShawMuOCR.Android.Platform;

namespace ShawMuOCR.Android.ViewModels;

public sealed partial class FileOcrViewModel : ViewModelBase
{
    private readonly AndroidOcrService _ocrService;
    private readonly IAndroidPlatformBridge _platformBridge;
    private readonly Func<AndroidContext> _contextProvider;

    [ObservableProperty]
    private string _statusMessage = "请点击下方按钮选择图片或 PDF 文档";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _recognizedText = string.Empty;

    [ObservableProperty]
    private string _fileSummary = "未选择文件";

    [ObservableProperty]
    private Bitmap? _previewImage;

    [ObservableProperty]
    private bool _isPdfDocument;

    [ObservableProperty]
    private int _currentPageIndex = 1;

    [ObservableProperty]
    private int _totalPdfPages = 1;

    [ObservableProperty]
    private string _executionTimeInfo = string.Empty;

    private AndroidUri? _selectedUri;

    public FileOcrViewModel(
        AndroidOcrService ocrService,
        IAndroidPlatformBridge platformBridge,
        Func<AndroidContext> contextProvider)
    {
        _ocrService = ocrService;
        _platformBridge = platformBridge;
        _contextProvider = contextProvider;
    }

    [RelayCommand]
    private async Task PickAndRecognizeFileAsync()
    {
        if (IsBusy) return;

        try
        {
            var uri = await _platformBridge.PickDocumentAsync();
            if (uri == null) return;

            _selectedUri = uri;
            string fileName = _platformBridge.GetFileName(uri);
            IsPdfDocument = _platformBridge.IsPdf(uri);

            IsBusy = true;
            RecognizedText = string.Empty;
            PreviewImage?.Dispose();
            PreviewImage = null;

            var context = _contextProvider();

            if (IsPdfDocument)
            {
                TotalPdfPages = AndroidPdfRenderer.GetPageCount(context, uri);
                CurrentPageIndex = 1;
                FileSummary = $"PDF: {fileName} (共 {TotalPdfPages} 页)";
                StatusMessage = $"正在渲染并识别第 {CurrentPageIndex} / {TotalPdfPages} 页...";

                await ProcessPdfPageAsync(CurrentPageIndex);
            }
            else
            {
                FileSummary = $"图片: {fileName}";
                StatusMessage = "正在通过原生 ImageDecoder 解码并进行 OCR 推理...";

                using var buffer = AndroidImageDecoder.DecodeFromUri(context, uri, _ocrService.CurrentSettings.MaxSideLen);
                UpdatePreview(buffer);

                var result = await _ocrService.RecognizeAsync(buffer);
                RecognizedText = result.FullText;
                ExecutionTimeInfo = $"耗时: {result.ElapsedMilliseconds} ms · 模式: {result.BackendUsed}";
                StatusMessage = $"识别完成！共 {result.Lines.Count} 行文字";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"识别异常: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        if (!IsPdfDocument || _selectedUri == null || CurrentPageIndex <= 1 || IsBusy) return;
        CurrentPageIndex--;
        await ProcessPdfPageAsync(CurrentPageIndex);
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (!IsPdfDocument || _selectedUri == null || CurrentPageIndex >= TotalPdfPages || IsBusy) return;
        CurrentPageIndex++;
        await ProcessPdfPageAsync(CurrentPageIndex);
    }

    [RelayCommand]
    private async Task RecognizeAllPdfPagesAsync()
    {
        if (!IsPdfDocument || _selectedUri == null || IsBusy) return;

        IsBusy = true;
        try
        {
            var context = _contextProvider();
            var sb = new StringBuilder();
            long totalElapsed = 0;

            await foreach (var (pageNum, buffer) in AndroidPdfRenderer.RenderAllPagesAsync(context, _selectedUri))
            {
                using (buffer)
                {
                    StatusMessage = $"正在识别第 {pageNum} / {TotalPdfPages} 页...";
                    var result = await _ocrService.RecognizeAsync(buffer);
                    totalElapsed += result.ElapsedMilliseconds;

                    sb.AppendLine($"--- [第 {pageNum} 页] ---");
                    sb.AppendLine(result.FullText);
                    sb.AppendLine();
                }
            }

            RecognizedText = sb.ToString();
            ExecutionTimeInfo = $"整份 PDF 识别完成 · 总耗时: {totalElapsed} ms";
            StatusMessage = $"全部 {TotalPdfPages} 页识别完成！";
        }
        catch (Exception ex)
        {
            StatusMessage = $"批量识别失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CopyText()
    {
        if (string.IsNullOrWhiteSpace(RecognizedText)) return;
        _platformBridge.CopyToClipboard(RecognizedText);
        StatusMessage = "识别文字已复制到剪贴板！";
    }

    [RelayCommand]
    private void ClearText()
    {
        RecognizedText = string.Empty;
        ExecutionTimeInfo = string.Empty;
        StatusMessage = "结果已清空";
    }

    private async Task ProcessPdfPageAsync(int pageNumber)
    {
        if (_selectedUri == null) return;

        IsBusy = true;
        try
        {
            var context = _contextProvider();
            StatusMessage = $"正在渲染并识别第 {pageNumber} / {TotalPdfPages} 页...";

            // 零落盘：直接光栅化到位图内存字节
            using var buffer = AndroidPdfRenderer.RenderPage(context, _selectedUri, pageNumber - 1);
            UpdatePreview(buffer);

            var result = await _ocrService.RecognizeAsync(buffer);
            RecognizedText = result.FullText;
            ExecutionTimeInfo = $"耗时: {result.ElapsedMilliseconds} ms · 模式: {result.BackendUsed}";
            StatusMessage = $"第 {pageNumber} 页识别完成 (共 {result.Lines.Count} 行)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"第 {pageNumber} 页处理失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdatePreview(ImageBuffer buffer)
    {
        try
        {
            PreviewImage?.Dispose();

            // 关键：使用 AlphaFormat.Opaque 防止透明通道将画面滤成全黑，并自适应 RowBytes 跨步对齐
            var wb = new WriteableBitmap(
                new Avalonia.PixelSize(buffer.Width, buffer.Height),
                new Avalonia.Vector(96, 96),
                Avalonia.Platform.PixelFormat.Rgba8888,
                Avalonia.Platform.AlphaFormat.Opaque);

            using (var fb = wb.Lock())
            {
                int srcStride = buffer.Width * 4;
                if (fb.RowBytes == srcStride)
                {
                    Marshal.Copy(buffer.Pixels, 0, fb.Address, buffer.Pixels.Length);
                }
                else
                {
                    for (int y = 0; y < buffer.Height; y++)
                    {
                        IntPtr destRow = IntPtr.Add(fb.Address, y * fb.RowBytes);
                        Marshal.Copy(buffer.Pixels, y * srcStride, destRow, srcStride);
                    }
                }
            }

            PreviewImage = wb;
            AndroidCrashLogger.Log($"[PREVIEW] 文件识别预览图更新完成: {buffer.Width}x{buffer.Height}");
        }
        catch (Exception ex)
        {
            AndroidCrashLogger.LogException(ex, "[PREVIEW] 生成预览图异常");
        }
    }
}
