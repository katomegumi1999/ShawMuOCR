using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShawMuOCR.Android.OCR;
using ShawMuOCR.Android.Platform;

namespace ShawMuOCR.Android.ViewModels;

public sealed partial class CameraOcrViewModel : ViewModelBase
{
    private readonly AndroidOcrService _ocrService;
    private readonly IAndroidPlatformBridge _platformBridge;

    [ObservableProperty]
    private string _statusMessage = "请点击“立即拍照”拍摄文字";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _recognizedText = string.Empty;

    [ObservableProperty]
    private Bitmap? _capturedPreview;

    [ObservableProperty]
    private string _executionTimeInfo = string.Empty;

    public CameraOcrViewModel(
        AndroidOcrService ocrService,
        IAndroidPlatformBridge platformBridge)
    {
        _ocrService = ocrService;
        _platformBridge = platformBridge;
    }

    [RelayCommand]
    private async Task TakePhotoAndRecognizeAsync()
    {
        if (IsBusy) return;

        try
        {
            StatusMessage = "正在启动系统相机...";
            using var buffer = await _platformBridge.CapturePhotoAsync();

            if (buffer == null)
            {
                StatusMessage = "已取消拍照";
                return;
            }

            IsBusy = true;
            StatusMessage = "拍摄完成，正在进行纯内存零拷贝 OCR 推理...";
            RecognizedText = string.Empty;

            UpdatePreview(buffer);

            var result = await _ocrService.RecognizeAsync(buffer);
            RecognizedText = result.FullText;
            ExecutionTimeInfo = $"耗时: {result.ElapsedMilliseconds} ms · 后端: {result.BackendUsed}";
            StatusMessage = $"识别完成！共检测到 {result.Lines.Count} 行文字";
        }
        catch (Exception ex)
        {
            StatusMessage = $"拍照识别失败: {ex.Message}";
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

    private void UpdatePreview(ImageBuffer buffer)
    {
        try
        {
            CapturedPreview?.Dispose();

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

            CapturedPreview = wb;
            AndroidCrashLogger.Log($"[PREVIEW] 拍照识别预览图更新完成: {buffer.Width}x{buffer.Height}");
        }
        catch (Exception ex)
        {
            AndroidCrashLogger.LogException(ex, "[PREVIEW] 生成拍照预览图异常");
        }
    }
}
