namespace ShawMuOCR.Core.Logging;

public interface ILoggerService
{
    bool IsEnabled { get; }
    string LogDirectory { get; }
    string CurrentLogFilePath { get; }

    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}
