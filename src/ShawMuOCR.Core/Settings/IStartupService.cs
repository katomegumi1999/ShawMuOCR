namespace ShawMuOCR.Core.Settings;

public interface IStartupService
{
    bool IsEnabled();
    void SetEnabled(bool enable);
}
