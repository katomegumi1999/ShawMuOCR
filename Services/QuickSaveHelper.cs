using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ShawMuOCR.Core.Editor;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.Views;
using SkiaSharp;

namespace ShawMuOCR.Services;

/// <summary>
/// 全局统一的截图快存与图像导出服务帮助类。
/// 确保「截屏工具」与「截图标注」100% 共享并引用同一套快存目录、格式、画质及缩放参数配置，
/// 彻底消除两处快存逻辑不一致、路径未同步或状态引用脱节的问题。
/// </summary>
public static class QuickSaveHelper
{
    /// <summary>
    /// 解析全局有效单例设置服务，若局部为 null 则自动从 App 容器解析
    /// </summary>
    public static ISettingsService? ResolveSettingsService(ISettingsService? localService)
    {
        return localService ?? App.CurrentApp?.SettingsService;
    }

    /// <summary>
    /// 确保快存目录有效。若未配置或目录不存在，则弹窗选择文件夹并自动持久化写入设置。
    /// </summary>
    public static async Task<string?> EnsureQuickSaveDirectoryAsync(ISettingsService? settingsService, TopLevel? topLevel = null)
    {
        var settings = ResolveSettingsService(settingsService);
        string? lastDir = settings?.Current.LastSaveDirectory;
        if (!string.IsNullOrWhiteSpace(lastDir) && Directory.Exists(lastDir))
        {
            return lastDir;
        }

        ToastNotificationWindow.Show("尚未配置快存路径，请选择目标文件夹", TimeSpan.FromSeconds(2), "截图快存");

        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var hostTopLevel = topLevel ?? ResolveTopLevel();
            Window? tempHost = null;
            try
            {
                if (hostTopLevel == null || hostTopLevel.TryGetPlatformHandle() == null)
                {
                    tempHost = new Window
                    {
                        Width = 1,
                        Height = 1,
                        ShowInTaskbar = false,
                        Opacity = 0,
                        WindowStartupLocation = WindowStartupLocation.CenterScreen
                    };
                    tempHost.Show();
                    hostTopLevel = tempHost;
                }

                var folders = await hostTopLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "选择截图快存保存目录",
                    AllowMultiple = false
                });

                if (folders.Count > 0 && folders[0] != null)
                {
                    string? selectedPath = folders[0].TryGetLocalPath() ?? folders[0].Path.LocalPath;
                    if (!string.IsNullOrWhiteSpace(selectedPath) && Directory.Exists(selectedPath))
                    {
                        if (settings != null)
                        {
                            var cur = settings.Current;
                            cur.LastSaveDirectory = selectedPath;
                            settings.Save(cur);
                        }
                        return selectedPath;
                    }
                }
            }
            finally
            {
                tempHost?.Close();
            }

            return null;
        });
    }

    /// <summary>
    /// 获取统一的导出参数与扩展名
    /// </summary>
    public static (ImageExportOptions Options, string Extension) GetExportOptions(
        ISettingsService? settingsService,
        int originalWidth,
        int originalHeight,
        ImageSaveFormat? overrideFormat = null,
        int? overrideQuality = null,
        int? overrideTargetWidth = null,
        int? overrideTargetHeight = null)
    {
        var settings = ResolveSettingsService(settingsService);
        var cur = settings?.Current ?? new AppSettings();

        var fmt = overrideFormat ?? (Enum.TryParse<ImageSaveFormat>(cur.DefaultSaveFormat, true, out var f) ? f : ImageSaveFormat.Png);
        int quality = overrideQuality ?? Math.Clamp(cur.DefaultSaveQuality, 10, 100);

        int targetW;
        int targetH;
        if (overrideTargetWidth.HasValue && overrideTargetHeight.HasValue && overrideTargetWidth.Value > 0 && overrideTargetHeight.Value > 0)
        {
            targetW = overrideTargetWidth.Value;
            targetH = overrideTargetHeight.Value;
        }
        else
        {
            float scale = cur.DefaultSaveScale > 0 ? cur.DefaultSaveScale / 100f : 1.0f;
            targetW = Math.Max(1, (int)Math.Round(originalWidth * scale));
            targetH = Math.Max(1, (int)Math.Round(originalHeight * scale));
        }

        var options = new ImageExportOptions
        {
            Format = fmt,
            Quality = quality,
            TargetWidth = targetW,
            TargetHeight = targetH
        };

        string extension = ImageExportOptions.GetExtension(fmt);
        return (options, extension);
    }

    /// <summary>
    /// 对 ImageDocument 执行统一快存（带标注图元无损渲染导出）
    /// </summary>
    public static async Task<string?> QuickSaveAsync(
        ImageDocument doc,
        ISettingsService? settingsService,
        TopLevel? topLevel = null,
        string successTitle = "✓ 截图快存成功")
    {
        ArgumentNullException.ThrowIfNull(doc);

        string? targetDir = await EnsureQuickSaveDirectoryAsync(settingsService, topLevel);
        if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
        {
            return null;
        }

        var (options, extension) = GetExportOptions(settingsService, doc.OriginalImage.Width, doc.OriginalImage.Height);
        string fileName = $"ShawMuOCR_{DateTime.Now:yyyyMMdd_HHmmss}{extension}";
        string filePath = Path.Combine(targetDir, fileName);

        byte[] bytes = doc.Encode(options);
        await File.WriteAllBytesAsync(filePath, bytes);

        ShowSuccessToast(filePath, fileName, targetDir, successTitle);
        return filePath;
    }

    /// <summary>
    /// 对 OcrImage 执行统一快存
    /// </summary>
    public static async Task<string?> QuickSaveAsync(
        OcrImage image,
        ISettingsService? settingsService,
        TopLevel? topLevel = null,
        string successTitle = "✓ 截图快存成功")
    {
        ArgumentNullException.ThrowIfNull(image);

        using var doc = new ImageDocument(image);
        return await QuickSaveAsync(doc, settingsService, topLevel, successTitle);
    }

    public static void ShowSuccessToast(string filePath, string fileName, string targetDir, string title)
    {
        ToastNotificationWindow.Show(
            $"已保存至: {fileName}\n目录: {targetDir}",
            TimeSpan.FromSeconds(3),
            title,
            displaySeconds: 3.5,
            onClick: () =>
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = $"/select,\"{filePath}\"",
                            UseShellExecute = true
                        });
                    }
                    else if (Directory.Exists(targetDir))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = $"\"{targetDir}\"",
                            UseShellExecute = true
                        });
                    }
                }
                catch { }
            });
    }

    private static TopLevel? ResolveTopLevel()
    {
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.Windows.FirstOrDefault(w => w.IsVisible && w is not ToastNotificationWindow) 
                   ?? desktop.MainWindow;
        }
        return null;
    }
}
