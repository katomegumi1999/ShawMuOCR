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
        if (!IsEnabled) return;

        try
        {
            lock (_writeLock)
            {
                if (!Directory.Exists(_logDirectory))
                {
                    Directory.CreateDirectory(_logDirectory);
                }

                string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                string line = ex != null
                    ? $"[{time}] [{level}] {message} | Exception: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}"
                    : $"[{time}] [{level}] {message}";

                File.AppendAllText(CurrentLogFilePath, line + Environment.NewLine);
            }
        }
        catch (Exception writeEx)
        {
            Debug.WriteLine($"[FileLoggerService] Write failed: {writeEx.Message}");
        }
    }
}
