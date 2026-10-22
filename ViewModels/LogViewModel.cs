using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShawMuOCR.Core.Logging;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.Windows;
using ShawMuOCR.Windows.Clipboard;

namespace ShawMuOCR.ViewModels;

public partial class LogViewModel : ViewModelBase
{
    private readonly ILoggerService? _loggerService;
    private readonly ISettingsService? _settingsService;
    private const int MaxMemoryLogLines = 2000;

    public string LogDirectory => _loggerService?.LogDirectory ?? Path.Combine(AppContext.BaseDirectory, "logs");
    public string CurrentLogFilePath => _loggerService?.CurrentLogFilePath ?? Path.Combine(LogDirectory, $"shawmu_ocr_{DateTime.Now:yyyyMMdd}.log");

    public IReadOnlyList<string> AvailableLevels { get; } = ["全部", "INFO", "WARN", "ERROR"];

    [ObservableProperty]
    private string _selectedLevel = "全部";

    [ObservableProperty]
    private string _filterKeyword = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "就绪：实时显示本地运行审计与错误日志";

    [ObservableProperty]
    private int _filteredCount;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private string _displayedLogText = string.Empty;

    private readonly List<string> _allRawLines = new();

    public LogViewModel(ILoggerService? loggerService = null, ISettingsService? settingsService = null)
    {
        _loggerService = loggerService;
        _settingsService = settingsService;

        if (_loggerService != null)
        {
            _loggerService.LogWritten += OnLogWritten;
        }

        Refresh();
    }

    private void OnLogWritten(string line)
    {
        Dispatcher.UIThread.Post(() =>
        {
            lock (_allRawLines)
            {
                if (_allRawLines.Count >= MaxMemoryLogLines)
                {
                    _allRawLines.RemoveAt(0);
                }
                _allRawLines.Add(line);
                TotalCount = _allRawLines.Count;
            }

            ApplyFilter();
        });
    }

    partial void OnSelectedLevelChanged(string value) => ApplyFilter();
    partial void OnFilterKeywordChanged(string value) => ApplyFilter();

    [RelayCommand]
    public void Refresh()
    {
        try
        {
            string path = CurrentLogFilePath;
            if (File.Exists(path))
            {
                // 使用 FileShare.ReadWrite 打开，避免与后台写日志并发锁冲突
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs, Encoding.UTF8);

                var lines = new List<string>();
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    lines.Add(line);
                }

                lock (_allRawLines)
                {
                    _allRawLines.Clear();
                    // 保留末尾最多 MaxMemoryLogLines 行
                    if (lines.Count > MaxMemoryLogLines)
                    {
                        _allRawLines.AddRange(lines.Skip(lines.Count - MaxMemoryLogLines));
                    }
                    else
                    {
                        _allRawLines.AddRange(lines);
                    }
                    TotalCount = _allRawLines.Count;
                }

                StatusMessage = $"✓ 已载入最新日志记录 (共 {TotalCount} 行)";
            }
            else
            {
                lock (_allRawLines)
                {
                    _allRawLines.Clear();
                    TotalCount = 0;
                }
                StatusMessage = "暂无今日日志记录文件";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"读取日志文件异常: {ex.Message}";
        }

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        List<string> source;
        lock (_allRawLines)
        {
            source = [.. _allRawLines];
        }

        IEnumerable<string> filtered = source;

        if (!string.Equals(SelectedLevel, "全部", StringComparison.OrdinalIgnoreCase))
        {
            string levelTag = $"[{SelectedLevel}]";
            filtered = filtered.Where(x => x.Contains(levelTag, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(FilterKeyword))
        {
            string kw = FilterKeyword.Trim();
            filtered = filtered.Where(x => x.Contains(kw, StringComparison.OrdinalIgnoreCase));
        }

        var resultList = filtered.ToList();
        FilteredCount = resultList.Count;

        if (resultList.Count == 0)
        {
            DisplayedLogText = "(没有符合筛选条件的日志记录)";
        }
        else
        {
            DisplayedLogText = string.Join(Environment.NewLine, resultList);
        }
    }

    [RelayCommand]
    public async Task CopyAllLogsAsync()
    {
        if (string.IsNullOrEmpty(DisplayedLogText))
        {
            StatusMessage = "当前无日志内容可复制";
            return;
        }

        try
        {
            await WindowsClipboardService.Shared.SetTextAsync(DisplayedLogText);
            StatusMessage = $"✓ 已成功复制当前 {FilteredCount} 条日志内容至剪贴板";
        }
        catch (Exception ex)
        {
            StatusMessage = $"复制失败: {ex.Message}";
        }
    }

    [RelayCommand]
    public void ClearLogs()
    {
        try
        {
            string path = CurrentLogFilePath;
            if (File.Exists(path))
            {
                File.WriteAllText(path, string.Empty, Encoding.UTF8);
            }

            lock (_allRawLines)
            {
                _allRawLines.Clear();
                TotalCount = 0;
            }

            ApplyFilter();
            StatusMessage = "✓ 已清空当前日志文件内容";
        }
        catch (Exception ex)
        {
            StatusMessage = $"清空日志失败: {ex.Message}";
        }
    }

    [RelayCommand]
    public void CleanExpiredLogs()
    {
        if (_loggerService == null)
        {
            StatusMessage = "未提供日志服务";
            return;
        }

        try
        {
            int retentionDays = _settingsService?.Current.LogRetentionDays ?? 7;
            int deleted = _loggerService.CleanOldLogs(retentionDays);
            StatusMessage = deleted > 0
                ? $"✓ 已成功清理 {deleted} 个过期历史日志文件 (保留最近 {retentionDays} 天)"
                : $"✓ 历史日志检查完成，暂无超过 {retentionDays} 天的过期日志";
        }
        catch (Exception ex)
        {
            StatusMessage = $"清理过期日志失败: {ex.Message}";
        }
    }

    [RelayCommand]
    public void OpenLogFolder()
    {
        try
        {
            string dir = LogDirectory;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开日志目录失败: {ex.Message}";
        }
    }

    [RelayCommand]
    public void OpenLogFile()
    {
        try
        {
            string file = CurrentLogFilePath;
            if (!File.Exists(file))
            {
                string dir = Path.GetDirectoryName(file) ?? LogDirectory;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(file, string.Empty, Encoding.UTF8);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = file,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开日志文件失败: {ex.Message}";
        }
    }
}
