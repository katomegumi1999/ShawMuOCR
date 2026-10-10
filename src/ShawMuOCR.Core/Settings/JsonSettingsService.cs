using System.Text.Json;

namespace ShawMuOCR.Core.Settings;

public sealed class JsonSettingsService : ISettingsService
{
    private readonly string _settingsFilePath;
    private readonly object _lock = new();
    private AppSettings _current;

    public AppSettings Current => _current;
    public string SettingsFilePath => _settingsFilePath;
    public event Action<AppSettings>? SettingsChanged;

    public JsonSettingsService(string? customPath = null)
    {
        if (!string.IsNullOrEmpty(customPath))
        {
            _settingsFilePath = customPath;
        }
        else
        {
            _settingsFilePath = Path.Combine(AppContext.BaseDirectory, "settings.json");
        }

        _current = Load();
    }

    public AppSettings Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    string json = File.ReadAllText(_settingsFilePath);
                    var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
                    if (settings != null)
                    {
                        _current = settings;
                        return settings;
                    }
                }

                // 尝试兼容迁移原 AppData/ShawMuOCR 路径配置至 exe 同目录
                string appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShawMuOCR", "settings.json");
                if (File.Exists(appDataPath))
                {
                    string json = File.ReadAllText(appDataPath);
                    var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
                    if (settings != null)
                    {
                        _current = settings;
                        Save(settings);
                        return settings;
                    }
                }

                // 尝试兼容迁移 MiniOCR 路径
                string miniOcrPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MiniOCR", "settings.json");
                if (File.Exists(miniOcrPath))
                {
                    string json = File.ReadAllText(miniOcrPath);
                    var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
                    if (settings != null)
                    {
                        _current = settings;
                        Save(settings);
                        return settings;
                    }
                }
            }
            catch
            {
                // 加载异常时回退到默认设置
            }

            _current = new AppSettings();
            return _current;
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_lock)
        {
            _current = settings.Clone();
            string json = JsonSerializer.Serialize(_current, AppSettingsJsonContext.Default.AppSettings);

            bool savedPrimary = false;
            try
            {
                string? dir = Path.GetDirectoryName(_settingsFilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(_settingsFilePath, json);
                savedPrimary = true;
            }
            catch
            {
                // 程序目录无写入权限（如 Program Files 非管理员运行）时回退至 AppData
            }

            if (!savedPrimary)
            {
                try
                {
                    string appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShawMuOCR");
                    Directory.CreateDirectory(appDataDir);
                    File.WriteAllText(Path.Combine(appDataDir, "settings.json"), json);
                }
                catch
                {
                    // 忽略写入失败
                }
            }

            SettingsChanged?.Invoke(_current);
        }
    }
}
