using System.Text;
using System.Text.Json;
using ShawMuOCR.Core.Security;

namespace ShawMuOCR.Core.Settings;

public sealed class JsonSettingsService : ISettingsService
{
    private const string EncryptedPrefix = "enc:";

    private readonly string _settingsFilePath;
    private readonly object _lock = new();

    /// <summary>
    /// 记录"无法解密但绝不能销毁"的旧密文（按字段名索引）。
    /// 场景：settings.json 被复制到另一台机器/另一个 Windows 账户后 DPAPI 解密失败，
    /// 此时内存里只能给出空凭据，但落盘时必须把原密文写回，否则用户换回原机后凭据永久丢失。
    /// </summary>
    private readonly Dictionary<string, string> _unreadableCiphertexts = new(StringComparer.Ordinal);

    private AppSettings _current;

    public AppSettings Current => _current;
    public string SettingsFilePath => _settingsFilePath;

    /// <summary>
    /// 最近一次加载失败的原因（null 表示加载正常）。
    /// </summary>
    public string? LastLoadError { get; private set; }

    /// <summary>
    /// 最近一次保存失败的原因（null 表示保存成功）。
    /// </summary>
    public string? LastSaveError { get; private set; }

    /// <summary>
    /// 配置加载失败（JSON 损坏、字段非法、整文件不可读）时触发。
    /// 触发时配置文件已尽力备份为 *.bad-时间戳，便于用户回溯。
    /// </summary>
    public event Action<string, Exception?>? LoadFailed;

    /// <summary>
    /// 配置保存失败（目录无权限、磁盘满、文件被占用）时触发。
    /// </summary>
    public event Action<string, Exception?>? SaveFailed;

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
                        DecryptSensitiveFields(settings);
                        LastLoadError = null;
                        _current = settings;
                        return settings;
                    }

                    // 反序列化得到 null：按损坏处理，走备份 + 回退分支
                    throw new InvalidDataException("settings.json 内容为空或无法反序列化为配置对象");
                }

                // 尝试兼容迁移原 AppData/ShawMuOCR 路径配置至 exe 同目录
                string appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShawMuOCR", "settings.json");
                if (File.Exists(appDataPath))
                {
                    string json = File.ReadAllText(appDataPath);
                    var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
                    if (settings != null)
                    {
                        DecryptSensitiveFields(settings);
                        LastLoadError = null;
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
                        DecryptSensitiveFields(settings);
                        LastLoadError = null;
                        _current = settings;
                        Save(settings);
                        return settings;
                    }
                }
            }
            catch (Exception ex)
            {
                // 不再静默回退：留存原因、备份损坏文件、并通知上层（日志/UI）告警。
                LastLoadError = ex.Message;
                BackupCorruptSettingsFile();
                LoadFailed?.Invoke($"加载配置文件失败，已回退默认配置：{ex.Message}", ex);
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
            // 内存版本保留纯明文供运行期直接使用
            _current = settings.Clone();
            DecryptSensitiveFields(_current);

            // 落盘版本加密敏感凭据
            var persistClone = _current.Clone();
            EncryptSensitiveFields(persistClone);

            string json = JsonSerializer.Serialize(persistClone, AppSettingsJsonContext.Default.AppSettings);

            Exception? primaryError = null;
            try
            {
                WriteAllTextAtomic(_settingsFilePath, json);
                LastSaveError = null;
            }
            catch (Exception ex)
            {
                primaryError = ex;
            }

            if (primaryError != null)
            {
                // 程序目录无写入权限（如 Program Files 非管理员运行）时回退至 AppData
                try
                {
                    string appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShawMuOCR");
                    WriteAllTextAtomic(Path.Combine(appDataDir, "settings.json"), json);
                    LastSaveError = null;
                }
                catch (Exception fallbackEx)
                {
                    // 两处都失败：绝不静默，留痕并通知上层由 UI 明确提示
                    LastSaveError = $"写入 {_settingsFilePath} 失败：{primaryError.Message}；回退 AppData 亦失败：{fallbackEx.Message}";
                    SaveFailed?.Invoke(LastSaveError, fallbackEx);
                }
            }

            SettingsChanged?.Invoke(_current);
        }
    }

    /// <summary>
    /// 原子写入：先写同目录临时文件并刷盘，再用 File.Replace 原子替换目标文件。
    /// 避免进程崩溃/断电时留下被截断的 settings.json（那会导致下次启动全部配置回默认）。
    /// </summary>
    private static void WriteAllTextAtomic(string path, string content)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = path + ".tmp";
        byte[] bytes = Encoding.UTF8.GetBytes(content);

        using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }

        if (!File.Exists(path))
        {
            File.Move(tempPath, path);
            return;
        }

        try
        {
            File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        catch (Exception)
        {
            // 少数文件系统（FAT32 / 部分网络盘）不支持 File.Replace，退化为覆盖写
            File.Copy(tempPath, path, overwrite: true);
            TryDelete(tempPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理临时文件失败不影响主流程
        }
    }

    /// <summary>
    /// 把损坏的配置文件另存为 *.bad-时间戳，保留用户手改或回溯的可能。
    /// </summary>
    private void BackupCorruptSettingsFile()
    {
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                return;
            }

            string backupPath = $"{_settingsFilePath}.bad-{DateTime.Now:yyyyMMddHHmmss}";
            File.Copy(_settingsFilePath, backupPath, overwrite: true);
        }
        catch
        {
            // 备份失败不阻断启动
        }
    }

    private void DecryptSensitiveFields(AppSettings settings)
    {
        settings.BaiduAppId = DecryptOrPreserve(nameof(settings.BaiduAppId), settings.BaiduAppId);
        settings.BaiduSecretKey = DecryptOrPreserve(nameof(settings.BaiduSecretKey), settings.BaiduSecretKey);
        settings.BaiduApiKey = DecryptOrPreserve(nameof(settings.BaiduApiKey), settings.BaiduApiKey);
        settings.GoogleApiKey = DecryptOrPreserve(nameof(settings.GoogleApiKey), settings.GoogleApiKey);
        settings.OpenAiApiKey = DecryptOrPreserve(nameof(settings.OpenAiApiKey), settings.OpenAiApiKey);
        settings.VolcengineAccessKey = DecryptOrPreserve(nameof(settings.VolcengineAccessKey), settings.VolcengineAccessKey);
        settings.VolcengineSecretKey = DecryptOrPreserve(nameof(settings.VolcengineSecretKey), settings.VolcengineSecretKey);
        settings.AliyunAccessKeyId = DecryptOrPreserve(nameof(settings.AliyunAccessKeyId), settings.AliyunAccessKeyId);
        settings.AliyunAccessKeySecret = DecryptOrPreserve(nameof(settings.AliyunAccessKeySecret), settings.AliyunAccessKeySecret);
        settings.HttpApiAccessToken = DecryptOrPreserve(nameof(settings.HttpApiAccessToken), settings.HttpApiAccessToken);
    }

    /// <summary>
    /// 解密单个字段：
    /// 1) 空值 → 空；
    /// 2) 无 enc: 前缀（旧版明文）→ 原样返回，下次保存时自动加密升级；
    /// 3) 密文但解密失败 → 记住原密文、内存返回空，落盘时原样写回，绝不销毁。
    /// </summary>
    private string DecryptOrPreserve(string fieldName, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (!value.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
        {
            return value;
        }

        string plain = DataProtectionHelper.Unprotect(value);
        if (plain.Length == 0)
        {
            _unreadableCiphertexts[fieldName] = value;
        }
        else
        {
            _unreadableCiphertexts.Remove(fieldName);
        }

        return plain;
    }

    private void EncryptSensitiveFields(AppSettings settings)
    {
        settings.BaiduAppId = ProtectOrKeep(nameof(settings.BaiduAppId), settings.BaiduAppId);
        settings.BaiduSecretKey = ProtectOrKeep(nameof(settings.BaiduSecretKey), settings.BaiduSecretKey);
        settings.BaiduApiKey = ProtectOrKeep(nameof(settings.BaiduApiKey), settings.BaiduApiKey);
        settings.GoogleApiKey = ProtectOrKeep(nameof(settings.GoogleApiKey), settings.GoogleApiKey);
        settings.OpenAiApiKey = ProtectOrKeep(nameof(settings.OpenAiApiKey), settings.OpenAiApiKey);
        settings.VolcengineAccessKey = ProtectOrKeep(nameof(settings.VolcengineAccessKey), settings.VolcengineAccessKey);
        settings.VolcengineSecretKey = ProtectOrKeep(nameof(settings.VolcengineSecretKey), settings.VolcengineSecretKey);
        settings.AliyunAccessKeyId = ProtectOrKeep(nameof(settings.AliyunAccessKeyId), settings.AliyunAccessKeyId);
        settings.AliyunAccessKeySecret = ProtectOrKeep(nameof(settings.AliyunAccessKeySecret), settings.AliyunAccessKeySecret);
        settings.HttpApiAccessToken = ProtectOrKeep(nameof(settings.HttpApiAccessToken), settings.HttpApiAccessToken);
    }

    /// <summary>
    /// 明文非空 → 加密；明文为空但存在无法解密的旧密文 → 原样写回（不销毁凭据）。
    /// </summary>
    private string ProtectOrKeep(string fieldName, string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            if (_unreadableCiphertexts.TryGetValue(fieldName, out string? preserved))
            {
                return preserved;
            }

            return string.Empty;
        }

        // 用户重新录入了凭据，丢弃之前的无法解密副本
        _unreadableCiphertexts.Remove(fieldName);
        return DataProtectionHelper.Protect(plainText);
    }
}
