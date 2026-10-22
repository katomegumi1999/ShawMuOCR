using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShawMuOCR.Android.OCR;

namespace ShawMuOCR.Android.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly AndroidOcrService _ocrService;

    [ObservableProperty]
    private string _statusMessage = "就绪";

    [ObservableProperty]
    private string _selectedBackend = "Auto";

    [ObservableProperty]
    private int _detIntraOpThreads = 4;

    [ObservableProperty]
    private int _lineWorkerCount = 4;

    [ObservableProperty]
    private int _selectedMaxSideLen = 960;

    [ObservableProperty]
    private bool _enableAngle = true;

    [ObservableProperty]
    private string _modelDescription = "PP-OCRv6 Small (内置集成，无需外部模型文件)";

    [ObservableProperty]
    private string _architectureInfo = $"架构: ARM64-v8a · 核心数: {Environment.ProcessorCount}";

    public IReadOnlyList<string> AvailableBackends { get; } = ["Auto", "CPU", "GPU"];
    public IReadOnlyList<int> AvailableThreadCounts { get; } = [1, 2, 4, 6, 8, 10, 12, 16];
    public IReadOnlyList<int> AvailableWorkerCounts { get; } = [1, 2, 4, 8];
    public IReadOnlyList<int> AvailableMaxSideLens { get; } = [720, 960, 1280, 1600];

    public SettingsViewModel(AndroidOcrService ocrService)
    {
        _ocrService = ocrService;
        var current = _ocrService.CurrentSettings;
        _selectedBackend = current.Backend;
        _detIntraOpThreads = current.DetIntraOpThreads;
        _lineWorkerCount = current.LineWorkerCount;
        _selectedMaxSideLen = current.MaxSideLen;
        _enableAngle = current.EnableAngle;
    }

    partial void OnSelectedBackendChanged(string value)
    {
        // 避坑防护：防止 ComboBox 数据源变化触发置空
        if (string.IsNullOrWhiteSpace(value)) return;
    }

    [RelayCommand]
    private async Task ApplySettingsAsync()
    {
        try
        {
            StatusMessage = "正在应用新配置并重启 OCR 引擎...";

            var newSettings = new AndroidOcrSettings
            {
                Backend = SelectedBackend,
                DetIntraOpThreads = DetIntraOpThreads,
                LineWorkerCount = LineWorkerCount,
                MaxSideLen = SelectedMaxSideLen,
                EnableAngle = EnableAngle
            };

            await _ocrService.EnsureInitializedAsync(newSettings);
            StatusMessage = $"配置已生效！当前引擎后端: {SelectedBackend}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"配置应用失败: {ex.Message}";
        }
    }
}
