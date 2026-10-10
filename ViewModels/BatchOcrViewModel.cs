using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShawMuOCR.Core.Batch;
using ShawMuOCR.Core.Logging;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.Services.Batch;

namespace ShawMuOCR.ViewModels;

/// <summary>
/// 批量 OCR 转换页面主 ViewModel
/// </summary>
public partial class BatchOcrViewModel : ViewModelBase
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".pdf"
    };

    private readonly BatchOcrProcessor _processor;
    private readonly ISettingsService _settingsService;
    private readonly ILoggerService? _logger;

    private CancellationTokenSource? _batchCts;
    private DispatcherTimer? _cpuMonitorTimer;
    private TimeSpan _prevTotalProcTime = TimeSpan.Zero;
    private DateTime _prevSampleTime = DateTime.UtcNow;
    private bool _isLoadingSettings;

    public ObservableCollection<BatchOcrItemViewModel> Items { get; } = new();
    public ObservableCollection<string> Logs { get; } = new();

    public bool HasLogs => Logs.Count > 0;

    #region 输出格式与配置项 (需求 2, 3, 4, 5)

    [ObservableProperty]
    private bool _exportTxt = true;

    [ObservableProperty]
    private bool _exportXlsx = true;

    [ObservableProperty]
    private bool _exportDocx;

    [ObservableProperty]
    private bool _splitColumnsByBlocks = true;

    [ObservableProperty]
    private bool _isSuffixEnabled = true;

    [ObservableProperty]
    private string _suffixText = "_ocr";

    [ObservableProperty]
    private bool _useOriginalFolder = true;

    [ObservableProperty]
    private string _customOutputDir = string.Empty;

    [ObservableProperty]
    private int _maxCores = Math.Max(1, Environment.ProcessorCount / 2);

    [ObservableProperty]
    private int _cpuLimitPercent = 50;

    [ObservableProperty]
    private int _imageRotationIndex = 0;

    public ImageRotationMode SelectedImageRotation => ImageRotationIndex switch
    {
        1 => ImageRotationMode.None,
        2 => ImageRotationMode.Rotate90,
        3 => ImageRotationMode.Rotate180,
        4 => ImageRotationMode.Rotate270,
        _ => ImageRotationMode.Auto
    };

    public int MaxProcessorCount => Environment.ProcessorCount;

    partial void OnExportTxtChanged(bool value) => SaveSettings();
    partial void OnExportXlsxChanged(bool value) => SaveSettings();
    partial void OnExportDocxChanged(bool value) => SaveSettings();
    partial void OnSplitColumnsByBlocksChanged(bool value) => SaveSettings();
    partial void OnImageRotationIndexChanged(int value) => SaveSettings();
    partial void OnIsSuffixEnabledChanged(bool value) => SaveSettings();
    partial void OnSuffixTextChanged(string value) => SaveSettings();
    partial void OnUseOriginalFolderChanged(bool value) => SaveSettings();
    partial void OnCustomOutputDirChanged(string value) => SaveSettings();

    partial void OnMaxCoresChanged(int value)
    {
        if (IsProcessing)
        {
            _processor.UpdateCpuSettings(value, CpuLimitPercent);
        }
        SaveSettings();
    }

    partial void OnCpuLimitPercentChanged(int value)
    {
        if (IsProcessing)
        {
            _processor.UpdateCpuSettings(MaxCores, value);
        }
        SaveSettings();
    }

    private void SaveSettings()
    {
        if (_isLoadingSettings) return;

        var s = _settingsService.Current.Clone();
        s.BatchExportTxt = ExportTxt;
        s.BatchExportXlsx = ExportXlsx;
        s.BatchExportDocx = ExportDocx;
        s.BatchSplitColumnsByBlocks = SplitColumnsByBlocks;
        s.BatchImageRotationIndex = ImageRotationIndex;
        s.BatchIsSuffixEnabled = IsSuffixEnabled;
        s.BatchSuffixText = SuffixText;
        s.BatchUseOriginalFolder = UseOriginalFolder;
        s.BatchCustomOutputDir = CustomOutputDir;
        s.BatchMaxCores = MaxCores;
        s.BatchCpuLimitPercent = CpuLimitPercent;
        _settingsService.Save(s);
    }

    #endregion

    #region 统计与状态指示

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(HasItems))]
    private bool _isProcessing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(HasItems))]
    private int _totalCount;

    [ObservableProperty]
    private int _completedCount;

    [ObservableProperty]
    private int _failedCount;

    [ObservableProperty]
    private int _pendingCount;

    [ObservableProperty]
    private double _overallProgress;

    [ObservableProperty]
    private string _statusMessage = "就绪：可批量添加图片/PDF文档，或直接将文件夹拖拽至此处";

    [ObservableProperty]
    private string _overallElapsedText = "总耗时: --";

    [ObservableProperty]
    private string _cpuUsageText = "CPU: 0%";

    public bool CanStart => !IsProcessing && TotalCount > 0;
    public bool HasItems => TotalCount > 0;

    #endregion

    #region 对话框请求事件 (供 View 订阅并唤起系统 Picker)

    public event Action? RequestOpenFilePicker;
    public event Action? RequestOpenFolderPicker;
    public event Action? RequestOpenOutputDirPicker;

    #endregion

    public BatchOcrViewModel(
        IOcrEngineFactory engineFactory,
        ISettingsService settingsService,
        ILoggerService? logger = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _logger = logger;
        _processor = new BatchOcrProcessor(engineFactory, settingsService, logger);
        _processor.LogProduced += AddLog;

        _isLoadingSettings = true;
        var s = _settingsService.Current;
        ExportTxt = s.BatchExportTxt;
        ExportXlsx = s.BatchExportXlsx;
        ExportDocx = s.BatchExportDocx;
        SplitColumnsByBlocks = s.BatchSplitColumnsByBlocks;
        ImageRotationIndex = Math.Clamp(s.BatchImageRotationIndex, 0, 4);
        IsSuffixEnabled = s.BatchIsSuffixEnabled;
        SuffixText = string.IsNullOrEmpty(s.BatchSuffixText) ? "_ocr" : s.BatchSuffixText;
        UseOriginalFolder = s.BatchUseOriginalFolder;

        string defaultDocDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        CustomOutputDir = string.IsNullOrWhiteSpace(s.BatchCustomOutputDir)
            ? Path.Combine(defaultDocDir, "ShawMuOCR_Export")
            : s.BatchCustomOutputDir;

        MaxCores = Math.Clamp(s.BatchMaxCores, 1, Environment.ProcessorCount);
        CpuLimitPercent = Math.Clamp(s.BatchCpuLimitPercent, 10, 100);
        _isLoadingSettings = false;

        Items.CollectionChanged += (_, _) => UpdateCounters();
        AddLog("就绪：支持批量导入图片/PDF或拖拽文件夹，加入队列后默认自动勾选");
    }

    public void AddLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        string entry = $"[{DateTime.Now:HH:mm:ss}] {message}";

        if (Avalonia.Application.Current == null || Dispatcher.UIThread.CheckAccess())
        {
            InsertLogCore(entry);
        }
        else
        {
            Dispatcher.UIThread.Post(() => InsertLogCore(entry));
        }
    }

    private void InsertLogCore(string entry)
    {
        Logs.Insert(0, entry);
        if (Logs.Count > 200)
        {
            Logs.RemoveAt(Logs.Count - 1);
        }
        OnPropertyChanged(nameof(HasLogs));
    }

    #region 命令

    [RelayCommand]
    public void ClearLogs()
    {
        Logs.Clear();
        OnPropertyChanged(nameof(HasLogs));
    }

    [RelayCommand]
    public void ToggleSelectAll()
    {
        if (IsProcessing || Items.Count == 0) return;

        bool allSelected = Items.All(x => x.IsSelected);
        bool targetState = !allSelected;
        foreach (var item in Items)
        {
            item.IsSelected = targetState;
        }
    }

    [RelayCommand]
    public void OpenFilePicker()
    {
        RequestOpenFilePicker?.Invoke();
    }

    [RelayCommand]
    public void OpenFolderPicker()
    {
        RequestOpenFolderPicker?.Invoke();
    }

    [RelayCommand]
    public void BrowseOutputDir()
    {
        RequestOpenOutputDirPicker?.Invoke();
    }

    [RelayCommand]
    public void RemoveItem(BatchOcrItemViewModel? item)
    {
        if (item != null && !IsProcessing)
        {
            Items.Remove(item);
        }
    }

    [RelayCommand]
    public void RemoveSelected()
    {
        if (IsProcessing) return;

        var selected = Items.Where(x => x.IsSelected).ToList();
        foreach (var item in selected)
        {
            Items.Remove(item);
        }

        if (selected.Count > 0)
        {
            AddLog($"已从队列中移除 {selected.Count} 个选中文件");
        }
    }

    [RelayCommand]
    public void ClearAll()
    {
        if (IsProcessing) return;

        Items.Clear();
        UpdateCounters();
        StatusMessage = "列表已清空";
        AddLog("已清空待转换任务列表");
    }

    [RelayCommand]
    public async Task StartBatchAsync()
    {
        if (IsProcessing || Items.Count == 0) return;

        var targetItems = Items.Where(x => x.IsSelected).ToList();
        if (targetItems.Count == 0)
        {
            StatusMessage = "请至少勾选一个需要转换的文件！";
            AddLog("⚠️ 启动拦截：请至少勾选一个待转换的文件");
            return;
        }

        // 校验至少选中一种导出格式
        if (!ExportTxt && !ExportXlsx && !ExportDocx)
        {
            StatusMessage = "请至少选择一种导出格式（TXT、XLSX 或 DOCX）！";
            AddLog("⚠️ 启动拦截：请至少选择一种导出格式");
            return;
        }

        // 校验自定义目录
        if (!UseOriginalFolder)
        {
            if (string.IsNullOrWhiteSpace(CustomOutputDir))
            {
                StatusMessage = "请先选择或输入自定义导出目录！";
                AddLog("⚠️ 启动拦截：未指定自定义导出目录");
                return;
            }
            try
            {
                Directory.CreateDirectory(CustomOutputDir);
            }
            catch (Exception ex)
            {
                StatusMessage = $"创建导出目录失败: {ex.Message}";
                AddLog($"✕ 创建导出目录失败: {ex.Message}");
                return;
            }
        }

        IsProcessing = true;
        _batchCts = new CancellationTokenSource();
        var ct = _batchCts.Token;

        // 重置所有勾选待转换任务状态
        foreach (var item in targetItems)
        {
            item.Reset();
        }

        UpdateCounters();
        StatusMessage = $"正在批量转换 (并发数: {MaxCores}, CPU限流: {CpuLimitPercent}%)...";
        AddLog($"🚀 开始批量转换: 共 {targetItems.Count} 项 | 并发: {MaxCores}核 | CPU限流: {CpuLimitPercent}%");
        StartCpuMonitoring();

        var totalSw = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            OverallElapsedText = $"总耗时: {totalSw.Elapsed.TotalSeconds:F1}s";
        };
        timer.Start();

        try
        {
            var options = new BatchOcrOptions
            {
                ExportTxt = ExportTxt,
                ExportXlsx = ExportXlsx,
                ExportDocx = ExportDocx,
                SplitColumnsByBlocks = SplitColumnsByBlocks,
                IsSuffixEnabled = IsSuffixEnabled,
                SuffixText = SuffixText,
                UseOriginalFolder = UseOriginalFolder,
                CustomOutputDir = CustomOutputDir,
                MaxCores = MaxCores,
                CpuLimitPercent = CpuLimitPercent,
                ImageRotation = SelectedImageRotation
            };

            await _processor.ProcessBatchAsync(
                targetItems,
                options,
                (completed, total) =>
                {
                    if (Avalonia.Application.Current == null || Dispatcher.UIThread.CheckAccess())
                    {
                        CompletedCount = Items.Count(x => x.Status == Core.Batch.BatchItemStatus.Completed);
                        FailedCount = Items.Count(x => x.Status == Core.Batch.BatchItemStatus.Failed);
                        PendingCount = Items.Count(x => x.Status == Core.Batch.BatchItemStatus.Pending || x.Status == Core.Batch.BatchItemStatus.Processing);
                        OverallProgress = total > 0 ? (double)completed / total * 100.0 : 0;
                    }
                    else
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            CompletedCount = Items.Count(x => x.Status == Core.Batch.BatchItemStatus.Completed);
                            FailedCount = Items.Count(x => x.Status == Core.Batch.BatchItemStatus.Failed);
                            PendingCount = Items.Count(x => x.Status == Core.Batch.BatchItemStatus.Pending || x.Status == Core.Batch.BatchItemStatus.Processing);
                            OverallProgress = total > 0 ? (double)completed / total * 100.0 : 0;
                        });
                    }
                },
                ct);

            totalSw.Stop();
            timer.Stop();

            int finalCompleted = targetItems.Count(x => x.Status == Core.Batch.BatchItemStatus.Completed);
            int finalFailed = targetItems.Count(x => x.Status == Core.Batch.BatchItemStatus.Failed);

            StatusMessage = $"✓ 批量转换完成: 成功 {finalCompleted} 项，失败 {finalFailed} 项 (耗时 {totalSw.Elapsed.TotalSeconds:F1}s)";
            AddLog($"🎉 批量转换完成: 成功 {finalCompleted} 项，失败 {finalFailed} 项 (总耗时 {totalSw.Elapsed.TotalSeconds:F1}s)");
        }
        catch (OperationCanceledException)
        {
            totalSw.Stop();
            timer.Stop();
            StatusMessage = "批量转换任务已由用户手动停止";
            AddLog("⏹ 批量转换任务已由用户手动停止");
            foreach (var item in targetItems.Where(x => x.Status == Core.Batch.BatchItemStatus.Pending || x.Status == Core.Batch.BatchItemStatus.Processing))
            {
                item.MarkCancelled();
            }
        }
        catch (Exception ex)
        {
            totalSw.Stop();
            timer.Stop();
            _logger?.Error("批量任务总调度异常", ex);
            StatusMessage = $"批量处理异常: {ex.Message}";
            AddLog($"✕ 批量处理异常: {ex.Message}");
        }
        finally
        {
            StopCpuMonitoring();
            IsProcessing = false;
            UpdateCounters();
            _batchCts?.Dispose();
            _batchCts = null;
        }
    }

    [RelayCommand]
    public void CancelBatch()
    {
        if (!IsProcessing || _batchCts == null) return;

        StatusMessage = "正在请求停止批量任务...";
        _batchCts.Cancel();
    }

    [RelayCommand]
    public void OpenOutputDir(BatchOcrItemViewModel? item)
    {
        string? targetDir = null;

        if (item != null && item.GeneratedFiles.Count > 0)
        {
            targetDir = Path.GetDirectoryName(item.GeneratedFiles[0]);
        }
        else if (UseOriginalFolder && item != null)
        {
            targetDir = Path.GetDirectoryName(item.FilePath);
        }
        else if (!UseOriginalFolder && !string.IsNullOrWhiteSpace(CustomOutputDir) && Directory.Exists(CustomOutputDir))
        {
            targetDir = CustomOutputDir;
        }
        else if (item != null)
        {
            targetDir = Path.GetDirectoryName(item.FilePath);
        }

        if (!string.IsNullOrEmpty(targetDir) && Directory.Exists(targetDir))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = targetDir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _logger?.Error($"打开目录失败: {targetDir}", ex);
            }
        }
    }

    [RelayCommand]
    public void OpenFile(string? filePath)
    {
        if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _logger?.Error($"打开文件失败: {filePath}", ex);
            }
        }
    }

    #endregion

    #region 文件与目录批量添加

    public void AddFiles(IEnumerable<string> filePaths)
    {
        if (IsProcessing) return;

        int added = 0;
        foreach (var path in filePaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;

            string ext = Path.GetExtension(path);
            if (!SupportedExtensions.Contains(ext)) continue;

            // 查重：避免重复添加完全一致的路径
            if (Items.Any(x => x.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Items.Add(BatchOcrItemViewModel.FromFilePath(path));
            added++;
        }

        UpdateCounters();
        if (added > 0)
        {
            StatusMessage = $"已添加 {added} 个文件，当前列表中共 {Items.Count} 项";
            AddLog($"📁 已添加 {added} 个文件并自动勾选 (当前队列共 {Items.Count} 项)");
        }
    }

    public void AddDirectory(string directoryPath)
    {
        if (IsProcessing || string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath)) return;

        try
        {
            var files = Directory.EnumerateFiles(directoryPath, "*.*", SearchOption.AllDirectories)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)));

            AddFiles(files);
        }
        catch (Exception ex)
        {
            _logger?.Error($"遍历目录添加文件失败: {directoryPath}", ex);
            StatusMessage = $"遍历目录出错: {ex.Message}";
        }
    }

    private void UpdateCounters()
    {
        TotalCount = Items.Count;
        CompletedCount = Items.Count(x => x.Status == Core.Batch.BatchItemStatus.Completed);
        FailedCount = Items.Count(x => x.Status == Core.Batch.BatchItemStatus.Failed);
        PendingCount = Items.Count(x => x.Status == Core.Batch.BatchItemStatus.Pending || x.Status == Core.Batch.BatchItemStatus.Processing);
        OverallProgress = TotalCount > 0 ? (double)(CompletedCount + FailedCount) / TotalCount * 100.0 : 0;
    }

    #endregion

    #region CPU 实时利用率监视

    private void StartCpuMonitoring()
    {
        _prevTotalProcTime = Process.GetCurrentProcess().TotalProcessorTime;
        _prevSampleTime = DateTime.UtcNow;

        _cpuMonitorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _cpuMonitorTimer.Tick += (_, _) =>
        {
            try
            {
                var now = DateTime.UtcNow;
                var curTotalProcTime = Process.GetCurrentProcess().TotalProcessorTime;

                var procTimeDeltaMs = (curTotalProcTime - _prevTotalProcTime).TotalMilliseconds;
                var wallTimeDeltaMs = (now - _prevSampleTime).TotalMilliseconds * Environment.ProcessorCount;

                _prevTotalProcTime = curTotalProcTime;
                _prevSampleTime = now;

                if (wallTimeDeltaMs > 0)
                {
                    double cpuPercent = Math.Clamp((procTimeDeltaMs / wallTimeDeltaMs) * 100.0, 0, 100);
                    CpuUsageText = $"CPU: {cpuPercent:F0}%";
                }
            }
            catch
            {
                CpuUsageText = "CPU: --";
            }
        };
        _cpuMonitorTimer.Start();
    }

    private void StopCpuMonitoring()
    {
        _cpuMonitorTimer?.Stop();
        _cpuMonitorTimer = null;
        CpuUsageText = "CPU: 0%";
    }

    #endregion
}
