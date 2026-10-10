using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShawMuOCR.Core.Clipboard;
using ShawMuOCR.Services;

namespace ShawMuOCR.ViewModels;

public partial class AboutViewModel : ViewModelBase, IDisposable
{
    private readonly IModelMemoryDiagnosticsService _diagnosticsService;
    private readonly IClipboardService? _clipboardService;
    private readonly DispatcherTimer? _timer;
    private bool _disposed;

    [ObservableProperty]
    private string _gitHubUrl = "https://github.com/katomegumi1999/ShawMuOCR";

    [ObservableProperty]
    private string _gitHubRepoText = "katomegumi1999/ShawMuOCR";

    [ObservableProperty]
    private string _authorText = "沐洵羽 (B站 @沐洵羽)";

    [ObservableProperty]
    private string _bilibiliUrl = "https://space.bilibili.com/192158726";

    [ObservableProperty]
    private string _appName = "小沐 OCR (ShawMuOCR)";

    [ObservableProperty]
    private string _appVersion = GetAppVersion();

    private static string GetAppVersion()
    {
        var ver = typeof(AboutViewModel).Assembly.GetName().Version;
        if (ver == null) return "v1.0.0 (Native AOT)";
        string vStr = ver.Build >= 0 ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : $"{ver.Major}.{ver.Minor}";
        return $"v{vStr} (Native AOT)";
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

    [RelayCommand]
    public void OpenGitHub()
    {
        OpenUrl(GitHubUrl);
    }

    [RelayCommand]
    public void OpenBilibili()
    {
        OpenUrl(BilibiliUrl);
    }

    [RelayCommand]
    public async Task CopyGitHubUrlAsync()
    {
        if (_clipboardService != null)
        {
            await _clipboardService.SetTextAsync(GitHubUrl);
            StatusMessage = "✓ 已复制 GitHub 仓库链接至剪贴板";
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
