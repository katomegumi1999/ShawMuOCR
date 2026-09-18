using Microsoft.Win32;
using ShawMuOCR.Core.Settings;

namespace ShawMuOCR.Windows.Startup;

public sealed class WindowsStartupService : IStartupService
{
    private const string RUN_KEY_PATH = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string APP_NAME = "ShawMuOCR";

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RUN_KEY_PATH, false);
            return key?.GetValue(APP_NAME) != null;
        }
        catch
        {
            return false;
        }
    }

    public void SetEnabled(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RUN_KEY_PATH, true);
            if (key == null) return;

            if (enable)
            {
                string? exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue(APP_NAME, $"\"{exePath}\"");
                }
            }
            else
            {
                if (key.GetValue(APP_NAME) != null)
                {
                    key.DeleteValue(APP_NAME, false);
                }
            }
        }
        catch
        {
            // 忽略非核心注册表访问错误
        }
    }
}
