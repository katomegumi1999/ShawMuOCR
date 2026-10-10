using System.Diagnostics;
using ShawMuOCR.Core.Settings;

namespace ShawMuOCR.Core.Logging;

public sealed class FileLoggerService : ILoggerService
{
    private readonly ISettingsService _settingsService;
    private readonly string _logDirectory;
    private readonly object _writeLock = new();

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
}
