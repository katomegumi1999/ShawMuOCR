using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ShawMuOCR.Core.Editor;
using ShawMuOCR.Core.Settings;

namespace ShawMuOCR.Views;

public partial class SaveImageDialog : Window
{
    private readonly ImageDocument _doc;
    private readonly ISettingsService? _settingsService;
    private readonly int _origWidth;
    private readonly int _origHeight;
    private bool _isUpdatingDimensions;
    private CancellationTokenSource? _sizeEstimateCts;

    public bool SavedSuccessfully { get; private set; }
    public bool ShouldCloseEditor { get; private set; } = true;

    public SaveImageDialog() : this(null!, null)
    {
    }

    public SaveImageDialog(ImageDocument doc, ISettingsService? settingsService = null)
    {
        _doc = doc;
        _settingsService = settingsService;

        InitializeComponent();

        if (doc != null)
        {
            _origWidth = doc.OriginalImage.Width;
            _origHeight = doc.OriginalImage.Height;
            OriginalSizeText.Text = $"原始: {_origWidth} × {_origHeight} px";
        }

        // 读取持久化或默认偏好配置
        var settings = settingsService?.Current ?? new AppSettings();
        InitFromSettings(settings);

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    private void InitFromSettings(AppSettings settings)
    {
        _isUpdatingDimensions = true;
        try
        {
            // 格式
            var fmt = settings.DefaultSaveFormat?.ToUpperInvariant() ?? "PNG";
            switch (fmt)
            {
                case "JPEG":
                case "JPG":
                    FormatJpgBtn.IsChecked = true;
                    break;
                case "WEBP":
                    FormatWebpBtn.IsChecked = true;
                    break;
                case "BMP":
                    FormatBmpBtn.IsChecked = true;
                    break;
                default:
                    FormatPngBtn.IsChecked = true;
                    break;
            }

            // 画质
            QualitySlider.Value = Math.Clamp(settings.DefaultSaveQuality, 10, 100);

            // 尺寸
            float scale = settings.DefaultSaveScale > 0 ? settings.DefaultSaveScale / 100f : 1.0f;
            int targetW = Math.Max(1, (int)Math.Round(_origWidth * scale));
            int targetH = Math.Max(1, (int)Math.Round(_origHeight * scale));

            WidthBox.Text = targetW.ToString();
            HeightBox.Text = targetH.ToString();

            // 保存位置记录展示
            string? lastDir = settings.LastSaveDirectory;
            if (!string.IsNullOrWhiteSpace(lastDir) && Directory.Exists(lastDir))
            {
                SaveDirectoryText.Text = $"默认保存至: {lastDir}";
                ToolTip.SetTip(QuickSaveBtn, $"直接保存至: {lastDir} (无需弹出选择框)");
            }
            else
            {
                SaveDirectoryText.Text = "默认保存至: 系统图片文件夹（保存时将自动记录选择的文件夹）";
                ToolTip.SetTip(QuickSaveBtn, "尚未记录路径，点击将打开文件夹选择器");
            }

            UpdateQualityUi();
            UpdateSummary();
        }
        finally
        {
            _isUpdatingDimensions = false;
        }
    }

    private ImageSaveFormat CurrentFormat
    {
        get
        {
            if (FormatJpgBtn.IsChecked == true) return ImageSaveFormat.Jpeg;
            if (FormatWebpBtn.IsChecked == true) return ImageSaveFormat.Webp;
            if (FormatBmpBtn.IsChecked == true) return ImageSaveFormat.Bmp;
            return ImageSaveFormat.Png;
        }
    }

    private void OnFormatChanged(object? sender, RoutedEventArgs e)
    {
        UpdateQualityUi();
        UpdateSummary();
    }

    private void UpdateQualityUi()
    {
        var fmt = CurrentFormat;
        if (fmt is ImageSaveFormat.Png or ImageSaveFormat.Bmp)
        {
            QualitySlider.IsEnabled = false;
            QualityValueText.Text = "100% (无损)";
            QualityHintText.Text = "ℹ️ 当前选中的格式采用无损编码，自动保持 100% 最高画质。";
        }
        else if (fmt == ImageSaveFormat.Jpeg)
        {
            QualitySlider.IsEnabled = true;
            QualityValueText.Text = $"{(int)QualitySlider.Value}%";
            QualityHintText.Text = "💡 JPEG 适用于常规图片，推荐 85% ~ 95% 获得极佳画质体积比。";
        }
        else if (fmt == ImageSaveFormat.Webp)
        {
            QualitySlider.IsEnabled = true;
            QualityValueText.Text = $"{(int)QualitySlider.Value}%";
            QualityHintText.Text = "💡 现代化高效格式，在极高画质下比 JPEG 体积显著减少约 30%。";
        }
    }

    private void OnQualitySliderChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (QualityValueText == null) return;
        var fmt = CurrentFormat;
        if (fmt is ImageSaveFormat.Jpeg or ImageSaveFormat.Webp)
        {
            QualityValueText.Text = $"{(int)e.NewValue}%";
        }
        UpdateSummary();
    }

    private void OnPresetScaleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tagStr && double.TryParse(tagStr, CultureInfo.InvariantCulture, out double scale))
        {
            _isUpdatingDimensions = true;
            try
            {
                int targetW = Math.Max(1, (int)Math.Round(_origWidth * scale));
                int targetH = Math.Max(1, (int)Math.Round(_origHeight * scale));
                WidthBox.Text = targetW.ToString();
                HeightBox.Text = targetH.ToString();
                UpdateSummary();
            }
            finally
            {
                _isUpdatingDimensions = false;
            }
        }
    }

    private void OnWidthBoxChanged(object? sender, TextChangedEventArgs e)
    {
        if (_isUpdatingDimensions) return;
        if (int.TryParse(WidthBox.Text, out int w) && w > 0)
        {
            if (KeepAspectBox.IsChecked == true && _origWidth > 0)
            {
                _isUpdatingDimensions = true;
                try
                {
                    int h = Math.Max(1, (int)Math.Round((double)w * _origHeight / _origWidth));
                    HeightBox.Text = h.ToString();
                }
                finally
                {
                    _isUpdatingDimensions = false;
                }
            }
            UpdateSummary();
        }
    }

    private void OnHeightBoxChanged(object? sender, TextChangedEventArgs e)
    {
        if (_isUpdatingDimensions) return;
        if (int.TryParse(HeightBox.Text, out int h) && h > 0)
        {
            if (KeepAspectBox.IsChecked == true && _origHeight > 0)
            {
                _isUpdatingDimensions = true;
                try
                {
                    int w = Math.Max(1, (int)Math.Round((double)h * _origWidth / _origHeight));
                    WidthBox.Text = w.ToString();
                }
                finally
                {
                    _isUpdatingDimensions = false;
                }
            }
            UpdateSummary();
        }
    }

    private void UpdateSummary()
    {
        if (ExportSummaryText == null) return;

        int w = int.TryParse(WidthBox?.Text, out int parsedW) ? parsedW : _origWidth;
        int h = int.TryParse(HeightBox?.Text, out int parsedH) ? parsedH : _origHeight;

        double scaleX = _origWidth > 0 ? (double)w / _origWidth * 100 : 100;
        string scaleStr = Math.Abs(scaleX - 100) < 0.1 ? "100% 原始尺寸" : $"{Math.Round(scaleX)}%";

        ExportSummaryText.Text = $"最终导出: {w} × {h} 像素 ({scaleStr}) | {CurrentFormat}";

        TriggerEstimatedSizeUpdate();
    }

    private async void OnChangeDirectoryClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        IStorageFolder? startFolder = null;
        string? currentDir = _settingsService?.Current.LastSaveDirectory;
        if (!string.IsNullOrWhiteSpace(currentDir) && Directory.Exists(currentDir))
        {
            try
            {
                startFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(currentDir);
            }
            catch
            {
                // 忽略异常
            }
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择截图默认保存文件夹",
            SuggestedStartLocation = startFolder,
            AllowMultiple = false
        });

        if (folders.Count > 0 && folders[0] != null)
        {
            string selectedPath = folders[0].Path.LocalPath;
            SaveDirectoryText.Text = $"默认保存至: {selectedPath}";
            ToolTip.SetTip(QuickSaveBtn, $"直接保存至: {selectedPath} (无需弹出选择框)");

            if (_settingsService != null)
            {
                var cur = _settingsService.Current;
                cur.LastSaveDirectory = selectedPath;
                _settingsService.Save(cur);
            }
        }
    }

    private async void OnQuickSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_doc == null) return;

        string? lastDir = _settingsService?.Current.LastSaveDirectory;
        if (string.IsNullOrWhiteSpace(lastDir) || !Directory.Exists(lastDir))
        {
            // 尚未配置有效路径，平滑引导至选择保存位置
            ToastNotificationWindow.Show("尚未记录上次保存路径，请先选择一次保存位置");
            OnConfirmSaveClick(sender, e);
            return;
        }

        int targetW = int.TryParse(WidthBox.Text, out int w) && w > 0 ? Math.Clamp(w, 1, 16384) : _origWidth;
        int targetH = int.TryParse(HeightBox.Text, out int h) && h > 0 ? Math.Clamp(h, 1, 16384) : _origHeight;
        var fmt = CurrentFormat;
        int quality = (int)Math.Clamp(QualitySlider.Value, 10, 100);

        var options = new ImageExportOptions
        {
            Format = fmt,
            Quality = quality,
            TargetWidth = targetW,
            TargetHeight = targetH
        };

        string extension = ImageExportOptions.GetExtension(fmt);
        string suggestedName = $"ShawMuOCR_{DateTime.Now:yyyyMMdd_HHmmss}{extension}";
        string filePath = Path.Combine(lastDir, suggestedName);

        try
        {
            byte[] bytes = _doc.Encode(options);
            await File.WriteAllBytesAsync(filePath, bytes);

            // 保存当前偏好配置
            if (_settingsService != null)
            {
                var cur = _settingsService.Current;
                cur.DefaultSaveFormat = fmt.ToString().ToUpperInvariant();
                cur.DefaultSaveQuality = quality;
                cur.DefaultSaveScale = (int)Math.Round((double)targetW / _origWidth * 100);
                _settingsService.Save(cur);
            }

            ToastNotificationWindow.Show(
                $"文件: {suggestedName}\n目录: {lastDir}",
                TimeSpan.FromSeconds(3),
                "✓ 已自动保存到上次路径");

            SavedSuccessfully = true;
            ShouldCloseEditor = CloseEditorBox.IsChecked == true;

            Close(true);
        }
        catch (Exception ex)
        {
            ToastNotificationWindow.Show($"自动保存图片失败: {ex.Message}");
        }
    }

    private void TriggerEstimatedSizeUpdate()
    {
        _sizeEstimateCts?.Cancel();
        _sizeEstimateCts?.Dispose();
        _sizeEstimateCts = new CancellationTokenSource();
        var token = _sizeEstimateCts.Token;

        int w = int.TryParse(WidthBox?.Text, out int parsedW) ? parsedW : _origWidth;
        int h = int.TryParse(HeightBox?.Text, out int parsedH) ? parsedH : _origHeight;
        w = Math.Clamp(w, 1, 16384);
        h = Math.Clamp(h, 1, 16384);
        var fmt = CurrentFormat;
        int quality = (int)Math.Clamp(QualitySlider?.Value ?? 90, 10, 100);

        // 1. 立即根据色彩与分辨率给出快速理论预估（0ms延迟响应）
        long fastEstimateBytes = EstimateImageSizeBytes(w, h, fmt, quality);
        UpdateEstimatedSizeUi(fastEstimateBytes, isApproximation: true);

        // 2. 在后台 Task 异步通过真实渲染获取精确压缩后体积（防抖 80ms）
        if (_doc != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(80, token);
                    if (token.IsCancellationRequested) return;

                    var options = new ImageExportOptions
                    {
                        Format = fmt,
                        Quality = quality,
                        TargetWidth = w,
                        TargetHeight = h
                    };

                    byte[] sampleBytes = _doc.Encode(options);
                    if (token.IsCancellationRequested) return;

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (!token.IsCancellationRequested)
                        {
                            UpdateEstimatedSizeUi(sampleBytes.Length, isApproximation: false);
                        }
                    });
                }
                catch
                {
                    // 容错保留理论预估值
                }
            }, token);
        }
    }

    private void UpdateEstimatedSizeUi(long bytes, bool isApproximation)
    {
        if (EstimatedSizeText == null) return;
        string prefix = isApproximation ? "预估大小: ~" : "预估大小: ";
        EstimatedSizeText.Text = prefix + FormatFileSize(bytes);
    }

    private static long EstimateImageSizeBytes(int width, int height, ImageSaveFormat format, int quality)
    {
        long pixels = (long)width * height;
        switch (format)
        {
            case ImageSaveFormat.Bmp:
                return 54 + (pixels * 4);

            case ImageSaveFormat.Jpeg:
                double bppJpg = 0.08 + (quality / 100.0) * 0.35;
                return Math.Max(1024, (long)(pixels * bppJpg));

            case ImageSaveFormat.Webp:
                double bppWebp = 0.06 + (quality / 100.0) * 0.25;
                return Math.Max(1024, (long)(pixels * bppWebp));

            case ImageSaveFormat.Png:
            default:
                return Math.Max(1024, (long)(pixels * 0.40));
        }
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F2} MB";
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private async void OnConfirmSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_doc == null) return;

        int targetW = int.TryParse(WidthBox.Text, out int w) && w > 0 ? Math.Clamp(w, 1, 16384) : _origWidth;
        int targetH = int.TryParse(HeightBox.Text, out int h) && h > 0 ? Math.Clamp(h, 1, 16384) : _origHeight;
        var fmt = CurrentFormat;
        int quality = (int)Math.Clamp(QualitySlider.Value, 10, 100);

        var options = new ImageExportOptions
        {
            Format = fmt,
            Quality = quality,
            TargetWidth = targetW,
            TargetHeight = targetH
        };

        string extension = ImageExportOptions.GetExtension(fmt);
        string filterName = ImageExportOptions.GetFilterName(fmt);
        string suggestedName = $"ShawMuOCR_{DateTime.Now:yyyyMMdd_HHmmss}{extension}";

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        IStorageFolder? startFolder = null;
        string? lastDir = _settingsService?.Current.LastSaveDirectory;
        if (!string.IsNullOrWhiteSpace(lastDir) && Directory.Exists(lastDir))
        {
            try
            {
                startFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(lastDir);
            }
            catch
            {
                // 降级使用系统默认位置
            }
        }

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存截图标注图片",
            SuggestedStartLocation = startFolder,
            DefaultExtension = extension.TrimStart('.'),
            SuggestedFileName = suggestedName,
            FileTypeChoices = new[]
            {
                new FilePickerFileType(filterName)
                {
                    Patterns = new[] { $"*{extension}" }
                },
                new FilePickerFileType("所有文件 (*.*)")
                {
                    Patterns = new[] { "*.*" }
                }
            }
        });

        if (file == null)
        {
            // 用户取消了文件选择器
            return;
        }

        try
        {
            // 编码并写入磁盘
            byte[] bytes = _doc.Encode(options);
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();

            // 保存偏好配置与上次保存目录
            if (_settingsService != null)
            {
                var cur = _settingsService.Current;
                cur.DefaultSaveFormat = fmt.ToString().ToUpperInvariant();
                cur.DefaultSaveQuality = quality;
                cur.DefaultSaveScale = (int)Math.Round((double)targetW / _origWidth * 100);

                string? savedDir = Path.GetDirectoryName(file.Path.LocalPath);
                if (!string.IsNullOrWhiteSpace(savedDir))
                {
                    cur.LastSaveDirectory = savedDir;
                }

                _settingsService.Save(cur);
            }

            ToastNotificationWindow.Show($"✓ 图片已成功保存至 {file.Name}");

            SavedSuccessfully = true;
            ShouldCloseEditor = CloseEditorBox.IsChecked == true;

            Close(true);
        }
        catch (Exception ex)
        {
            ToastNotificationWindow.Show($"保存图片失败: {ex.Message}");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _sizeEstimateCts?.Cancel();
        _sizeEstimateCts?.Dispose();
        _sizeEstimateCts = null;
        base.OnClosed(e);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close(false);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && !WidthBox.IsFocused && !HeightBox.IsFocused)
        {
            OnConfirmSaveClick(sender, e);
            e.Handled = true;
        }
    }
}
