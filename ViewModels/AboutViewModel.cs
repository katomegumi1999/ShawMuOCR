using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShawMuOCR.Core.Clipboard;
using ShawMuOCR.Services;
using ShawMuOCR.Views;

namespace ShawMuOCR.ViewModels;

public partial class AboutViewModel : ViewModelBase, IDisposable
{
    private readonly IModelMemoryDiagnosticsService _diagnosticsService;
    private readonly IClipboardService? _clipboardService;
    private readonly DispatcherTimer? _timer;
    private bool _disposed;

    [ObservableProperty]
    private bool _isLogoModalOpen;

    [RelayCommand]
    public void ShowLogoModal() => IsLogoModalOpen = true;

    [RelayCommand]
    public void CloseLogoModal() => IsLogoModalOpen = false;

    [ObservableProperty]
    private string _gitHubUrl = "https://github.com/katomegumi1999/ShawMuOCR";

    [ObservableProperty]
    private string _gitHubRepoText = "katomegumi1999/ShawMuOCR";

    [ObservableProperty]
    private string _websiteUrl = "https://www.23771139.xyz/ShawMuOCR/";

    [ObservableProperty]
    private string _websiteText = "https://www.23771139.xyz/ShawMuOCR/";

    [ObservableProperty]
    private string _authorText = "沐洵羽 (B站 @沐洵羽)";

    [ObservableProperty]
    private string _bilibiliUrl = "https://space.bilibili.com/192158726";

    [ObservableProperty]
    private string _appName = "小沐 OCR (ShawMuOCR)";

    [ObservableProperty]
    private string _appVersion = GetAppVersion();

    [ObservableProperty]
    private string _windowsVersionText = DetectWindowsVersion();

    [ObservableProperty]
    private string _dotNetRuntimeText = DetectDotNetRuntimeVersion();

    [ObservableProperty]
    private string _avaloniaVersionText = DetectAvaloniaVersion();

    [ObservableProperty]
    private string _simdPaddleOcrVersionText = DetectSimdPaddleOcrVersion();

    private static string GetAppVersion()
    {
        try
        {
            string cleanVer = ExtractCleanAssemblyVersion(typeof(AboutViewModel).Assembly);
            return $"{cleanVer} (Native AOT)";
        }
        catch
        {
            return "v3.0.0 (Native AOT)";
        }
    }

    private static string DetectWindowsVersion()
    {
        try
        {
            string arch = RuntimeInformation.OSArchitecture.ToString();
            if (OperatingSystem.IsWindows())
            {
                const string regKey = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
                string? productName = Microsoft.Win32.Registry.GetValue(regKey, "ProductName", null) as string;
                string? displayVersion = Microsoft.Win32.Registry.GetValue(regKey, "DisplayVersion", null) as string;
                string? currentBuild = Microsoft.Win32.Registry.GetValue(regKey, "CurrentBuildNumber", null) as string;
                object? ubrObj = Microsoft.Win32.Registry.GetValue(regKey, "UBR", null);

                int buildNumber = Environment.OSVersion.Version.Build;
                if (!string.IsNullOrWhiteSpace(currentBuild) && int.TryParse(currentBuild, out int parsedBuild))
                {
                    buildNumber = parsedBuild;
                }

                if (!string.IsNullOrWhiteSpace(productName))
                {
                    if (buildNumber >= 22000 && productName.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
                    {
                        productName = productName.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
                    }

                    string verTag = !string.IsNullOrWhiteSpace(displayVersion) ? $" {displayVersion.Trim()}" : string.Empty;
                    string buildStr = ubrObj != null ? $"{buildNumber}.{ubrObj}" : $"{buildNumber}";
                    return $"{productName.Trim()}{verTag} (Build {buildStr} · {arch})";
                }
            }

            return $"{RuntimeInformation.OSDescription} ({arch})";
        }
        catch
        {
            return RuntimeInformation.OSDescription;
        }
    }

    private static string DetectDotNetRuntimeVersion()
    {
        try
        {
            string framework = RuntimeInformation.FrameworkDescription.Trim();
            string arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            string mode = RuntimeFeature.IsDynamicCodeSupported ? "CoreCLR JIT" : "Native AOT";
            return $"{framework} ({arch} · {mode})";
        }
        catch
        {
            return $".NET {Environment.Version}";
        }
    }

    private static string DetectAvaloniaVersion()
    {
        try
        {
            string ver = ExtractCleanAssemblyVersion(typeof(Avalonia.Application).Assembly);
            return $"{ver} (Skia / Win32)";
        }
        catch
        {
            return "v12.0.0 (Skia / Win32)";
        }
    }

    private static string DetectSimdPaddleOcrVersion()
    {
        try
        {
            string ver = ExtractCleanAssemblyVersion(typeof(Sdcb.SimdPaddleOCR.PaddleOcrAll).Assembly);
            return $"{ver} (纯 C# SIMD / Vulkan)";
        }
        catch
        {
            return "v2.0.0 (纯 C# SIMD / Vulkan)";
        }
    }

    private static string ExtractCleanAssemblyVersion(Assembly asm)
    {
        var infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVer))
        {
            int plusIdx = infoVer.IndexOf('+');
            string cleanVer = plusIdx > 0 ? infoVer.Substring(0, plusIdx).Trim() : infoVer.Trim();
            if (!string.IsNullOrEmpty(cleanVer))
            {
                return cleanVer.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? cleanVer : $"v{cleanVer}";
            }
        }

        var ver = asm.GetName().Version;
        if (ver != null)
        {
            string vStr = ver.Build >= 0 ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : $"{ver.Major}.{ver.Minor}";
            return $"v{vStr}";
        }

        return "v2.0.0";
    }

    [ObservableProperty]
    private string _modelName = "PP-OCRv6_small";

    [ObservableProperty]
    private string _modelStatus = "引擎就绪";

    [ObservableProperty]
    private string _modelWeightsDescription = "~15.9 MB (det 4.6MB + rec 11.2MB + dict 135KB)";

    [ObservableProperty]
    private string _engineDescription = "SimdPaddleOCR (纯C# SIMD / AVX2 NHWC加速)";

    [ObservableProperty]
    private string _cpuHardwareText = "--";

    [ObservableProperty]
    private string _cpuInstructionSetText = "--";

    [ObservableProperty]
    private string _gpuArchitectureText = "--";

    [ObservableProperty]
    private string _configuredBackendText = "--";

    [ObservableProperty]
    private string _effectiveBackendText = "--";

    [ObservableProperty]
    private string _effectiveBackendBadgeText = "--";

    [ObservableProperty]
    private string _physicalWorkingSetText = "-- MB";

    [ObservableProperty]
    private string _peakWorkingSetText = "-- MB";

    [ObservableProperty]
    private string _privateMemoryText = "-- MB";

    [ObservableProperty]
    private string _managedMemoryText = "-- MB";

    [ObservableProperty]
    private string _nativeEngineMemoryText = "-- MB";

    [ObservableProperty]
    private string _gdiHandlesText = "-- / 10,000";

    [ObservableProperty]
    private string _lastUpdatedText = "--";

    [ObservableProperty]
    private string _statusMessage = "就绪 (数据每 2.5 秒自动采样)";

    [ObservableProperty]
    private bool _isTrimming;

    public AboutViewModel(IModelMemoryDiagnosticsService diagnosticsService, IClipboardService? clipboardService = null)
    {
        _diagnosticsService = diagnosticsService;
        _clipboardService = clipboardService;

        RefreshMemory();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.5)
        };
        _timer.Tick += (s, e) =>
        {
            if (!_isTrimming)
            {
                RefreshMemory(isAutoRefresh: true);
            }
        };
        _timer.Start();
    }

    [RelayCommand]
    public void RefreshMemory()
    {
        RefreshMemory(isAutoRefresh: false);
    }

    private void RefreshMemory(bool isAutoRefresh)
    {
        try
        {
            var report = _diagnosticsService.GetReport();
            ApplyReport(report);

            if (!isAutoRefresh)
            {
                StatusMessage = $"✓ 已刷新 ({DateTime.Now:HH:mm:ss})";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"采样失败: {ex.Message}";
        }
    }

    [RelayCommand]
    public void TrimMemory()
    {
        if (IsTrimming) return;

        IsTrimming = true;
        StatusMessage = "正在深度释放空闲物理内存与对象堆...";

        try
        {
            var report = _diagnosticsService.TrimMemory();
            ApplyReport(report);
            StatusMessage = $"✓ 内存整理完成：物理工作集已压缩至 {report.PhysicalWorkingSetMb:F1} MB";
        }
        catch (Exception ex)
        {
            StatusMessage = $"释放出错: {ex.Message}";
        }
        finally
        {
            IsTrimming = false;
        }
    }

    private void ApplyReport(ModelMemoryReport report)
    {
        ModelName = report.ModelName;
        ModelStatus = report.ModelStatus;
        ModelWeightsDescription = report.ModelWeightsDescription;
        EngineDescription = report.EngineDescription;

        CpuHardwareText = report.CpuHardwareText;
        CpuInstructionSetText = report.CpuInstructionSetText;
        GpuArchitectureText = report.GpuArchitectureText;
        ConfiguredBackendText = report.ConfiguredBackendText;
        EffectiveBackendText = report.EffectiveBackendText;
        EffectiveBackendBadgeText = report.EffectiveBackendBadgeText;

        PhysicalWorkingSetText = $"{report.PhysicalWorkingSetMb:F1} MB";
        PeakWorkingSetText = $"{report.PeakWorkingSetMb:F1} MB";
        PrivateMemoryText = $"{report.PrivateMemoryMb:F1} MB";
        ManagedMemoryText = $"{report.ManagedMemoryMb:F1} MB";
        NativeEngineMemoryText = $"{report.NativeEngineMemoryMb:F1} MB";
        GdiHandlesText = $"{report.GdiHandles} / 10,000 (正常)";
        LastUpdatedText = report.Timestamp.ToString("HH:mm:ss");
    }

    private static void SafeShowToast(string text, string title = "复制成功")
    {
        try
        {
            if (Avalonia.Application.Current != null)
            {
                ToastNotificationWindow.Show(text, TimeSpan.FromMilliseconds(50), title);
            }
        }
        catch
        {
            // Unit test environment or no UI thread
        }
    }

    [RelayCommand]
    public async Task OpenGitHubAsync()
    {
        await CopyGitHubUrlAsync();
        OpenUrl(GitHubUrl);
    }

    [RelayCommand]
    public async Task OpenWebsiteAsync()
    {
        await CopyWebsiteUrlAsync();
        OpenUrl(WebsiteUrl);
    }

    [RelayCommand]
    public async Task OpenBilibiliAsync()
    {
        await CopyBilibiliUrlAsync();
        OpenUrl(BilibiliUrl);
    }

    [RelayCommand]
    public async Task CopyGitHubUrlAsync()
    {
        if (_clipboardService != null)
        {
            await _clipboardService.SetTextAsync(GitHubUrl);
            StatusMessage = "✓ 已复制 GitHub 仓库链接至剪贴板";
            SafeShowToast("GitHub 仓库链接已复制到剪贴板", "复制成功");
        }
        else
        {
            StatusMessage = "剪贴板服务暂不可用";
        }
    }

    [RelayCommand]
    public async Task CopyWebsiteUrlAsync()
    {
        if (_clipboardService != null)
        {
            await _clipboardService.SetTextAsync(WebsiteUrl);
            StatusMessage = "✓ 已复制官方网站链接至剪贴板";
            SafeShowToast("官方网站链接已复制到剪贴板", "复制成功");
        }
        else
        {
            StatusMessage = "剪贴板服务暂不可用";
        }
    }

    [RelayCommand]
    public async Task CopyBilibiliUrlAsync()
    {
        if (_clipboardService != null)
        {
            await _clipboardService.SetTextAsync(BilibiliUrl);
            StatusMessage = "✓ 已复制作者 B站主页链接至剪贴板";
            SafeShowToast("作者 B站主页链接已复制到剪贴板", "复制成功");
        }
        else
        {
            StatusMessage = "剪贴板服务暂不可用";
        }
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开链接失败: {ex.Message}";
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _timer?.Stop();
        }
    }
}
