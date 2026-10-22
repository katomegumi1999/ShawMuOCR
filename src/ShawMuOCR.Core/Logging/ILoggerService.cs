namespace ShawMuOCR.Core.Logging;

public interface ILoggerService
{
    bool IsEnabled { get; }
    string LogDirectory { get; }
    string CurrentLogFilePath { get; }

    event Action<string>? LogWritten;

    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);

    /// <summary>
    /// 根据保留天数清理历史过期日志文件，返回成功清理的文件数量
    /// </summary>
    int CleanOldLogs(int retentionDays);
}
