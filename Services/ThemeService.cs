using System;
using System.IO;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ShawMuOCR.Core.Settings;
using SkiaSharp;

namespace ShawMuOCR.Services;

public interface IThemeService
{
    Bitmap? CurrentBackgroundBitmap { get; }
    bool HasBackgroundImage { get; }
    bool HasBackgroundColor { get; }
    double BlurRadius { get; }
    double ComponentOpacity { get; }
    double ButtonOpacity { get; }
    string? BackgroundColorHex { get; }
    string? CardColorHex { get; }
    string? TextColorHex { get; }
    string? FloatingWindowBackgroundColorHex { get; }
    string? FloatingWindowTextColorHex { get; }

    event Action? ThemeVisualChanged;

    bool ApplyNewBackgroundImage(string sourceFilePath);
    void RemoveBackgroundImage();
    void SetBlurRadius(double radius);
    void SetComponentOpacity(double opacity);
    void SetButtonOpacity(double opacity);
    void SetBackgroundColor(string? hexColor);
    void SetCardColor(string? hexColor);
    void SetTextColor(string? hexColor);
    void SetFloatingWindowBackgroundColor(string? hexColor);
    void SetFloatingWindowTextColor(string? hexColor);
    void ResetToDefaults();
    void InitializeTheme();
}

public sealed class ThemeService : IThemeService
{
    private readonly ISettingsService _settingsService;
    private readonly string _themeCacheDirectory;
    private Bitmap? _cachedDisplayBitmap;
    private SKBitmap? _cachedBaseSkBitmap;
    private bool _isInternalSaving;
    private System.Threading.CancellationTokenSource? _saveCts;
    private System.Threading.CancellationTokenSource? _blurCts;

    public Bitmap? CurrentBackgroundBitmap => _cachedDisplayBitmap;
    public bool HasBackgroundImage => !string.IsNullOrEmpty(_settingsService.Current.ThemeBackgroundImagePath) && File.Exists(_settingsService.Current.ThemeBackgroundImagePath);
    public bool HasBackgroundColor => !string.IsNullOrEmpty(_settingsService.Current.ThemeBackgroundColorHex);
    public double BlurRadius => _settingsService.Current.ThemeBackgroundBlurRadius;
    public double ComponentOpacity => _settingsService.Current.ThemeComponentOpacity;
    public double ButtonOpacity => _settingsService.Current.ThemeButtonOpacity;
    public string? BackgroundColorHex => _settingsService.Current.ThemeBackgroundColorHex;
    public string? CardColorHex => _settingsService.Current.ThemeCardColorHex;
    public string? TextColorHex => _settingsService.Current.ThemeTextColorHex;
    public string? FloatingWindowBackgroundColorHex => _settingsService.Current.ThemeFloatingWindowBackgroundColorHex;
    public string? FloatingWindowTextColorHex => _settingsService.Current.ThemeFloatingWindowTextColorHex;

    public event Action? ThemeVisualChanged;

    public ThemeService(ISettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _themeCacheDirectory = Path.Combine(localAppData, "ShawMuOCR", "Theme");
        Directory.CreateDirectory(_themeCacheDirectory);
        _settingsService.SettingsChanged += _ =>
        {
            if (!_isInternalSaving)
            {
                InitializeTheme();
            }
        };
    }

    public void InitializeTheme()
    {
        var s = _settingsService.Current;
        if (!string.IsNullOrEmpty(s.ThemeBackgroundImagePath) && File.Exists(s.ThemeBackgroundImagePath))
        {
            LoadBaseBitmap(s.ThemeBackgroundImagePath);
            RenderDisplayBitmap(s.ThemeBackgroundBlurRadius);
        }
        else
        {
            RunOnUIThread(() =>
            {
                _cachedDisplayBitmap?.Dispose();
                _cachedDisplayBitmap = null;
            });
        }

        RunOnUIThread(() =>
        {
            UpdateApplicationBackgroundColor(s.ThemeBackgroundColorHex);
            UpdateApplicationThemeBrushes(s.ThemeComponentOpacity, s.ThemeButtonOpacity);
            UpdateApplicationTextColor(s.ThemeTextColorHex);
            ThemeVisualChanged?.Invoke();
        });
    }

    public bool ApplyNewBackgroundImage(string sourceFilePath)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
        {
            return false;
        }

        try
        {
            // 1. 清理旧缓存背景图
            DeleteCurrentCachedImage();

            // 2. 解码并压缩图片（上限 1920x1080，限制文件体积，自动优化为高质量 JPEG）
            using var rawBmp = SKBitmap.Decode(sourceFilePath);
            if (rawBmp == null) return false;

            int targetW = rawBmp.Width;
            int targetH = rawBmp.Height;
            const int maxDimension = 1920;

            SKBitmap processedBmp;
            if (targetW > maxDimension || targetH > maxDimension)
            {
                float ratio = Math.Min((float)maxDimension / targetW, (float)maxDimension / targetH);
                targetW = Math.Max(1, (int)(targetW * ratio));
                targetH = Math.Max(1, (int)(targetH * ratio));

                processedBmp = new SKBitmap(targetW, targetH);
                using var canvas = new SKCanvas(processedBmp);
                canvas.DrawBitmap(rawBmp, new SKRect(0, 0, targetW, targetH), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            }
            else
            {
                processedBmp = rawBmp;
            }

            string newCacheFileName = $"bg_{DateTime.UtcNow.Ticks}.jpg";
            string newCacheFilePath = Path.Combine(_themeCacheDirectory, newCacheFileName);

            using (var image = SKImage.FromBitmap(processedBmp))
            using (var data = image.Encode(SKEncodedImageFormat.Jpeg, 85))
            using (var stream = File.OpenWrite(newCacheFilePath))
            {
                data.SaveTo(stream);
            }

            if (processedBmp != rawBmp)
            {
                processedBmp.Dispose();
            }

            // 3. 更新配置持久化
            var s = _settingsService.Current.Clone();
            s.ThemeBackgroundImagePath = newCacheFilePath;
            _settingsService.Save(s);

            // 4. 重载与渲染（UI 对象严格调度至 UI 线程，彻底杜绝跨线程异常）
            LoadBaseBitmap(newCacheFilePath);
            RenderDisplayBitmap(s.ThemeBackgroundBlurRadius);

            RunOnUIThread(() =>
            {
                UpdateApplicationBackgroundColor(s.ThemeBackgroundColorHex);
                UpdateApplicationThemeBrushes(s.ThemeComponentOpacity, s.ThemeButtonOpacity);
                UpdateApplicationTextColor(s.ThemeTextColorHex);
                ThemeVisualChanged?.Invoke();
            });

            return true;
        }
        catch
        {
            return false;
        }
    }

    public void RemoveBackgroundImage()
    {
        DeleteCurrentCachedImage();

        var s = _settingsService.Current.Clone();
        s.ThemeBackgroundImagePath = null;
        _settingsService.Save(s);

        _cachedBaseSkBitmap?.Dispose();
        _cachedBaseSkBitmap = null;

        RunOnUIThread(() =>
        {
            _cachedDisplayBitmap?.Dispose();
            _cachedDisplayBitmap = null;
            UpdateApplicationBackgroundColor(s.ThemeBackgroundColorHex);
            UpdateApplicationThemeBrushes(s.ThemeComponentOpacity, s.ThemeButtonOpacity);
            UpdateApplicationTextColor(s.ThemeTextColorHex);
            ThemeVisualChanged?.Invoke();
        });
    }

    private void DebounceSaveSettings()
    {
        _saveCts?.Cancel();
        var cts = new System.Threading.CancellationTokenSource();
        _saveCts = cts;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, cts.Token);
                if (!cts.Token.IsCancellationRequested)
                {
                    _isInternalSaving = true;
                    try
                    {
                        _settingsService.Save(_settingsService.Current);
                    }
                    finally
                    {
                        _isInternalSaving = false;
                    }
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    public void SetBlurRadius(double radius)
    {
        double clamped = Math.Clamp(radius, 0.0, 30.0);
        _settingsService.Current.ThemeBackgroundBlurRadius = clamped;
        DebounceSaveSettings();

        if (_cachedBaseSkBitmap == null) return;

        _blurCts?.Cancel();
        var cts = new System.Threading.CancellationTokenSource();
        _blurCts = cts;

        Task.Run(async () =>
        {
            try
            {
                // 30ms 节流，滑动时跳过中间冗余渲染
                await Task.Delay(30, cts.Token);
                if (cts.Token.IsCancellationRequested) return;

                RenderDisplayBitmap(clamped);
                RunOnUIThread(() => ThemeVisualChanged?.Invoke());
            }
            catch (OperationCanceledException) { }
        });
    }

    public void SetComponentOpacity(double opacity)
    {
        double clamped = Math.Clamp(opacity, 0.0, 1.0);
        _settingsService.Current.ThemeComponentOpacity = clamped;
        DebounceSaveSettings();

        RunOnUIThread(() =>
        {
            UpdateApplicationThemeBrushes(clamped, _settingsService.Current.ThemeButtonOpacity);
            ThemeVisualChanged?.Invoke();
        });
    }

    public void SetButtonOpacity(double opacity)
    {
        double clamped = Math.Clamp(opacity, 0.0, 1.0);
        _settingsService.Current.ThemeButtonOpacity = clamped;
        DebounceSaveSettings();

        RunOnUIThread(() =>
        {
            UpdateApplicationThemeBrushes(_settingsService.Current.ThemeComponentOpacity, clamped);
            ThemeVisualChanged?.Invoke();
        });
    }

    public void SetBackgroundColor(string? hexColor)
    {
        string? normalized = string.IsNullOrWhiteSpace(hexColor) ? null : hexColor.Trim();
        _settingsService.Current.ThemeBackgroundColorHex = normalized;
        DebounceSaveSettings();

        RunOnUIThread(() =>
        {
            UpdateApplicationBackgroundColor(normalized);
            UpdateApplicationThemeBrushes(_settingsService.Current.ThemeComponentOpacity, _settingsService.Current.ThemeButtonOpacity);
            ThemeVisualChanged?.Invoke();
        });
    }

    public void SetCardColor(string? hexColor)
    {
        string? normalized = string.IsNullOrWhiteSpace(hexColor) ? null : hexColor.Trim();
        _settingsService.Current.ThemeCardColorHex = normalized;
        DebounceSaveSettings();

        RunOnUIThread(() =>
        {
            UpdateApplicationThemeBrushes(_settingsService.Current.ThemeComponentOpacity, _settingsService.Current.ThemeButtonOpacity);
            ThemeVisualChanged?.Invoke();
        });
    }

    public void SetTextColor(string? hexColor)
    {
        string? normalized = string.IsNullOrWhiteSpace(hexColor) ? null : hexColor.Trim();
        _settingsService.Current.ThemeTextColorHex = normalized;
        DebounceSaveSettings();

        RunOnUIThread(() =>
        {
            UpdateApplicationTextColor(normalized);
            ThemeVisualChanged?.Invoke();
        });
    }

    public void SetFloatingWindowBackgroundColor(string? hexColor)
    {
        string? normalized = string.IsNullOrWhiteSpace(hexColor) ? null : hexColor.Trim();
        _settingsService.Current.ThemeFloatingWindowBackgroundColorHex = normalized;
        DebounceSaveSettings();

        RunOnUIThread(() =>
        {
            ThemeVisualChanged?.Invoke();
        });
    }

    public void SetFloatingWindowTextColor(string? hexColor)
    {
        string? normalized = string.IsNullOrWhiteSpace(hexColor) ? null : hexColor.Trim();
        _settingsService.Current.ThemeFloatingWindowTextColorHex = normalized;
        DebounceSaveSettings();

        RunOnUIThread(() =>
        {
            ThemeVisualChanged?.Invoke();
        });
    }

    public void ResetToDefaults()
    {
        _isInternalSaving = true;
        try
        {
            var s = _settingsService.Current;
            s.ThemeBackgroundBlurRadius = 0.0;
            s.ThemeComponentOpacity = 0.12;
            s.ThemeButtonOpacity = 0.12;
            s.ThemeBackgroundColorHex = null;
            s.ThemeCardColorHex = null;
            s.ThemeTextColorHex = null;
            s.ThemeFloatingWindowBackgroundColorHex = null;
            s.ThemeFloatingWindowTextColorHex = null;
            _settingsService.Save(s);
        }
        finally
        {
            _isInternalSaving = false;
        }

        if (_cachedBaseSkBitmap != null)
        {
            RenderDisplayBitmap(0.0);
        }

        RunOnUIThread(() =>
        {
            UpdateApplicationBackgroundColor(null);
            UpdateApplicationThemeBrushes(0.12, 0.12);
            UpdateApplicationTextColor(null);
            ThemeVisualChanged?.Invoke();
        });
    }

    private void LoadBaseBitmap(string filePath)
    {
        try
        {
            _cachedBaseSkBitmap?.Dispose();
            _cachedBaseSkBitmap = SKBitmap.Decode(filePath);
        }
        catch
        {
            _cachedBaseSkBitmap = null;
        }
    }

    private void RenderDisplayBitmap(double blurRadius)
    {
        if (_cachedBaseSkBitmap == null) return;

        try
        {
            SKBitmap resultBmp;
            if (blurRadius > 0.5)
            {
                int w = _cachedBaseSkBitmap.Width;
                int h = _cachedBaseSkBitmap.Height;
                resultBmp = new SKBitmap(w, h);

                using (var canvas = new SKCanvas(resultBmp))
                using (var paint = new SKPaint())
                {
                    paint.ImageFilter = SKImageFilter.CreateBlur((float)blurRadius, (float)blurRadius);
                    canvas.DrawBitmap(_cachedBaseSkBitmap, 0, 0, SKSamplingOptions.Default, paint);
                }
            }
            else
            {
                resultBmp = _cachedBaseSkBitmap;
            }

            using var image = SKImage.FromBitmap(resultBmp);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
            byte[] bytes = data.ToArray();

            RunOnUIThread(() =>
            {
                using var ms = new MemoryStream(bytes);
                _cachedDisplayBitmap?.Dispose();
                _cachedDisplayBitmap = new Bitmap(ms);
            });

            if (resultBmp != _cachedBaseSkBitmap)
            {
                resultBmp.Dispose();
            }
        }
        catch
        {
            // 回退直接读取
            try
            {
                if (File.Exists(_settingsService.Current.ThemeBackgroundImagePath))
                {
                    string path = _settingsService.Current.ThemeBackgroundImagePath;
                    RunOnUIThread(() =>
                    {
                        _cachedDisplayBitmap?.Dispose();
                        _cachedDisplayBitmap = new Bitmap(path);
                    });
                }
            }
            catch { }
        }
    }

    private void DeleteCurrentCachedImage()
    {
        string? oldPath = _settingsService.Current.ThemeBackgroundImagePath;
        if (!string.IsNullOrEmpty(oldPath) && File.Exists(oldPath))
        {
            try
            {
                File.Delete(oldPath);
            }
            catch { }
        }
    }

    private void UpdateApplicationThemeBrushes(double componentOpacity, double buttonOpacity)
    {
        if (Application.Current == null) return;

        // 用户定义：0 为不透明 (Alpha=255)，100 (1.0) 为完全透明 (Alpha=0)
        byte cardAlpha = (byte)((1.0 - Math.Clamp(componentOpacity, 0.0, 1.0)) * 255);
        byte buttonAlpha = (byte)((1.0 - Math.Clamp(buttonOpacity, 0.0, 1.0)) * 255);

        // 解析自定义卡片底色（默认纯白 #FFFFFF）
        Color baseCardColor = Colors.White;
        if (ColorPaletteHelper.TryParseColor(_settingsService.Current.ThemeCardColorHex, out var parsedCardColor))
        {
            baseCardColor = parsedCardColor;
        }

        Color cardColor = Color.FromArgb(cardAlpha, baseCardColor.R, baseCardColor.G, baseCardColor.B);
        Color cardSecondaryColor = Color.FromArgb((byte)(cardAlpha * 0.9), (byte)(baseCardColor.R * 0.97), (byte)(baseCardColor.G * 0.97), (byte)(baseCardColor.B * 0.97));
        Color controlColor = Color.FromArgb(buttonAlpha, baseCardColor.R, baseCardColor.G, baseCardColor.B);

        // 彻底移除设置背景图片时覆盖在图片上方的半透明浅白蒙层，即使透明度为0也纯净无遮挡
        Color backdropOverlayColor = Colors.Transparent;

        RunOnUIThread(() =>
        {
            Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] = new SolidColorBrush(cardColor);
            Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"] = new SolidColorBrush(cardSecondaryColor);
            Application.Current.Resources["ControlFillColorDefaultBrush"] = new SolidColorBrush(controlColor);
            Application.Current.Resources["MainWindowBackdropOverlayBrush"] = new SolidColorBrush(backdropOverlayColor);
        });
    }

    private static void UpdateApplicationBackgroundColor(string? hexColor)
    {
        if (Application.Current == null) return;

        RunOnUIThread(() =>
        {
            if (ColorPaletteHelper.TryParseColor(hexColor, out var bgColor))
            {
                Application.Current.Resources["MainWindowBackdropSolidColorBrush"] = new SolidColorBrush(bgColor);
            }
            else
            {
                // 默认优雅纯净底色（微浅灰白），让半透明卡片在无背景图时也能呈现通透反差感
                Application.Current.Resources["MainWindowBackdropSolidColorBrush"] = new SolidColorBrush(Color.FromRgb(243, 244, 246));
            }
        });
    }

    private static readonly string[] TextResourceKeys =
    [
        "TextFillColorPrimaryBrush",
        "TextControlForeground",
        "TextControlCaretBrush",
        "ButtonForeground",
        "TextFillColorSecondaryBrush",
        "TextFillColorTertiaryBrush"
    ];

    private static void UpdateApplicationTextColor(string? hexColor)
    {
        if (Application.Current == null) return;

        RunOnUIThread(() =>
        {
            if (string.IsNullOrWhiteSpace(hexColor) || !ColorPaletteHelper.TryParseColor(hexColor, out var primaryColor))
            {
                foreach (var key in TextResourceKeys)
                {
                    Application.Current.Resources.Remove(key);
                }
                return;
            }

            var secondaryColor = Color.FromArgb((byte)(primaryColor.A * 0.75), primaryColor.R, primaryColor.G, primaryColor.B);
            var tertiaryColor = Color.FromArgb((byte)(primaryColor.A * 0.55), primaryColor.R, primaryColor.G, primaryColor.B);

            Application.Current.Resources["TextFillColorPrimaryBrush"] = new SolidColorBrush(primaryColor);
            Application.Current.Resources["TextControlForeground"] = new SolidColorBrush(primaryColor);
            Application.Current.Resources["TextControlCaretBrush"] = new SolidColorBrush(primaryColor);
            Application.Current.Resources["ButtonForeground"] = new SolidColorBrush(primaryColor);
            Application.Current.Resources["TextFillColorSecondaryBrush"] = new SolidColorBrush(secondaryColor);
            Application.Current.Resources["TextFillColorTertiaryBrush"] = new SolidColorBrush(tertiaryColor);
        });
    }

    private static void RunOnUIThread(Action action)
    {
        if (Application.Current == null) return;
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Invoke(action);
        }
    }
}
