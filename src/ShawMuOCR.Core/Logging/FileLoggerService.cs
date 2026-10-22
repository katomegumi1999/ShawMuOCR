using System.Diagnostics;
using ShawMuOCR.Core.Settings;

namespace ShawMuOCR.Core.Logging;

public sealed class FileLoggerService : ILoggerService
{
    private readonly ISettingsService _settingsService;
    private readonly string _logDirectory;
    private readonly object _writeLock = new();

    public event Action<string>? LogWritten;

    public bool IsEnabled => _settingsService.Current.EnableLogging;
    public string LogDirectory => _logDirectory;
    public string CurrentLogFilePath => Path.Combine(_logDirectory, $"shawmu_ocr_{DateTime.Now:yyyyMMdd}.log");

    public FileLoggerService(ISettingsService settingsService, string? customLogDir = null)
    {
        _settingsService = settingsService;
        if (!string.IsNullOrEmpty(customLogDir))
        {
            _logDirectory = customLogDir;
        }
        else
        {
            _logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
        }

        if (_settingsService.Current.EnableLogAutoClean)
        {
            Task.Run(() =>
            {
                try
                {
                    CleanOldLogs(_settingsService.Current.LogRetentionDays);
                }
                catch { }
            });
        }

        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged(AppSettings newSettings)
    {
        if (newSettings.EnableLogAutoClean)
        {
            Task.Run(() =>
            {
                try
                {
                    CleanOldLogs(newSettings.LogRetentionDays);
                }
                catch { }
            });
        }
    }

    public void Info(string message) => WriteLog("INFO", message, null);
    public void Warn(string message) => WriteLog("WARN", message, null);
    public void Error(string message, Exception? ex = null) => WriteLog("ERROR", message, ex);

    private void WriteLog(string level, string message, Exception? ex)
    {
        // ERROR 级别日志（如启动异常、崩溃）始终强制落盘，避免未开启日志时闪退无据可查
        if (!IsEnabled && !string.Equals(level, "ERROR", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string line = ex != null
            ? $"[{time}] [{level}] {message} | Exception: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex}"
            : $"[{time}] [{level}] {message}";

        try
        {
            LogWritten?.Invoke(line);
        }
        catch { }

        lock (_writeLock)
        {
            try
            {
                if (!Directory.Exists(_logDirectory))
                {
                    Directory.CreateDirectory(_logDirectory);
                }

                File.AppendAllText(CurrentLogFilePath, line + Environment.NewLine);
                return;
            }
            catch (Exception writeEx)
            {
                Debug.WriteLine($"[FileLoggerService] Primary log write failed: {writeEx.Message}");
            }

            // 若程序所在目录无写入权限（例如安装在 Program Files 且非管理员启动），自动回退至 LocalAppData
            try
            {
                string fallbackDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ShawMuOCR",
                    "logs");
                Directory.CreateDirectory(fallbackDir);
                string fallbackFile = Path.Combine(fallbackDir, $"shawmu_ocr_{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(fallbackFile, line + Environment.NewLine);
            }
            catch (Exception fallbackEx)
            {
                Debug.WriteLine($"[FileLoggerService] Fallback log write failed: {fallbackEx.Message}");
            }
        }
    }

    /// <summary>
    /// 根据保留天数清理历史过期日志文件，返回清理的文件数量
    /// </summary>
    public int CleanOldLogs(int retentionDays)
    {
        if (retentionDays <= 0) return 0;

        int deletedCount = 0;
        var thresholdDate = DateTime.Today.AddDays(-retentionDays);

        lock (_writeLock)
        {
            CleanDirectory(_logDirectory, thresholdDate, ref deletedCount);

            try
            {
                string fallbackDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ShawMuOCR",
                    "logs");
                if (Directory.Exists(fallbackDir) && !string.Equals(Path.GetFullPath(fallbackDir), Path.GetFullPath(_logDirectory), StringComparison.OrdinalIgnoreCase))
                {
                    CleanDirectory(fallbackDir, thresholdDate, ref deletedCount);
                }
            }
            catch { }
        }

        return deletedCount;
    }

    private void CleanDirectory(string dirPath, DateTime thresholdDate, ref int deletedCount)
    {
        if (!Directory.Exists(dirPath)) return;

        try
        {
            var files = Directory.GetFiles(dirPath, "*.log");
            string currentFile = CurrentLogFilePath;

            foreach (var file in files)
            {
                try
                {
                    // 绝不删除当前正在写入的今日日志
                    if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(currentFile), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string fileName = Path.GetFileNameWithoutExtension(file);
                    bool shouldDelete = false;

                    // 优先尝试从文件名解析日期 (例如 shawmu_ocr_20261010.log)
                    if (fileName.StartsWith("shawmu_ocr_", StringComparison.OrdinalIgnoreCase))
                    {
                        string datePart = fileName["shawmu_ocr_".Length..];
                        if (DateTime.TryParseExact(datePart, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var fileDate))
                        {
                            if (fileDate < thresholdDate)
                            {
                                shouldDelete = true;
                            }
                        }
                    }

                    // 如果文件名不是标准格式，以文件修改时间和创建时间为准
                    if (!shouldDelete)
                    {
                        var fileInfo = new FileInfo(file);
                        if (fileInfo.LastWriteTime.Date < thresholdDate && fileInfo.CreationTime.Date < thresholdDate)
                        {
                            shouldDelete = true;
                        }
                    }

                    if (shouldDelete)
                    {
                        File.Delete(file);
                        deletedCount++;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[FileLoggerService] Delete log file failed {file}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileLoggerService] Clean directory failed {dirPath}: {ex.Message}");
        }
    }
}
