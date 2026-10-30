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
                        MigrateLegacyFlatJsonIfNeeded(json, settings);
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
                        MigrateLegacyFlatJsonIfNeeded(json, settings);
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
                        MigrateLegacyFlatJsonIfNeeded(json, settings);
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

    /// <summary>
    /// 兼容性迁移：若 JSON 根节点包含历史版本遗留的单层扁平字段，
    /// 单趟 O(N) 映射赋值给对应的 Section 分块对象，保障旧配置无感升级且零开销。
    /// </summary>
    private static void MigrateLegacyFlatJsonIfNeeded(string json, AppSettings settings)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            foreach (var prop in root.EnumerateObject())
            {
                var val = prop.Value;
                switch (prop.Name)
                {
                    // Hotkeys
                    case "Hotkey":
                        if (val.ValueKind == JsonValueKind.String) settings.Hotkeys.Ocr = val.GetString()!;
                        break;
                    case "CaptureImageHotkey":
                        if (val.ValueKind == JsonValueKind.String) settings.Hotkeys.CaptureImage = val.GetString()!;
                        break;
                    case "AnnotateHotkey":
                        if (val.ValueKind == JsonValueKind.String) settings.Hotkeys.Annotate = val.GetString()!;
                        break;
                    case "TranslateHotkey":
                        if (val.ValueKind == JsonValueKind.String) settings.Hotkeys.Translate = val.GetString()!;
                        break;
                    case "OcrPopupHotkey":
                        if (val.ValueKind == JsonValueKind.String) settings.Hotkeys.OcrPopup = val.GetString()!;
                        break;
                    case "QuickNoteHotkey":
                        if (val.ValueKind == JsonValueKind.String) settings.Hotkeys.QuickNote = val.GetString()!;
                        break;
                    case "ClipboardPanelHotkey":
                        if (val.ValueKind == JsonValueKind.String) settings.Hotkeys.ClipboardPanel = val.GetString()!;
                        break;
                    case "PhrasesPanelHotkey":
                        if (val.ValueKind == JsonValueKind.String) settings.Hotkeys.PhrasesPanel = val.GetString()!;
                        break;
                    case "LongScreenshotHotkey":
                        if (val.ValueKind == JsonValueKind.String) settings.Hotkeys.LongScreenshot = val.GetString()!;
                        break;
                    case "RecordingHotkey":
                        if (val.ValueKind == JsonValueKind.String) settings.Hotkeys.Recording = val.GetString()!;
                        break;

                    // General
                    case "AutoCopy":
                        if (val.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.General.AutoCopy = val.GetBoolean();
                        break;
                    case "AutoCopyTableAsCells":
                        if (val.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.General.AutoCopyTableAsCells = val.GetBoolean();
                        break;
                    case "SilentMode":
                        if (val.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.General.SilentMode = val.GetBoolean();
                        break;
                    case "SilentStartup":
                        if (val.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.General.SilentStartup = val.GetBoolean();
                        break;
                    case "ShowOcrNotification":
                        if (val.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.General.ShowOcrNotification = val.GetBoolean();
                        break;
                    case "ShowCaptureImageNotification":
                        if (val.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.General.ShowCaptureImageNotification = val.GetBoolean();
                        break;
                    case "ShowNotificationBadge":
                        if (val.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        {
                            settings.General.ShowOcrNotification = val.GetBoolean();
                            settings.General.ShowCaptureImageNotification = val.GetBoolean();
                        }
                        break;
                    case "ShowWindowAfterOcr":
                        if (val.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.General.ShowWindowAfterOcr = val.GetBoolean();
                        break;
                    case "StartWithWindows":
                        if (val.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.General.StartWithWindows = val.GetBoolean();
                        break;
                    case "EnableLogging":
                        if (val.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.General.EnableLogging = val.GetBoolean();
                        break;
                    case "EnableLogAutoClean":
                        if (val.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.General.EnableLogAutoClean = val.GetBoolean();
                        break;
                    case "LogRetentionDays":
                        if (val.TryGetInt32(out int lrd)) settings.General.LogRetentionDays = lrd;
                        break;
                    case "DefaultSaveFormat":
                        if (val.ValueKind == JsonValueKind.String) settings.General.DefaultSaveFormat = val.GetString()!;
                        break;
                    case "DefaultSaveQuality":
                        if (val.TryGetInt32(out int dsq)) settings.General.DefaultSaveQuality = dsq;
                        break;
                    case "DefaultSaveScale":
                        if (val.TryGetInt32(out int dss)) settings.General.DefaultSaveScale = dss;
                        break;
                    case "LastSaveDirectory":
                        settings.General.LastSaveDirectory = val.ValueKind == JsonValueKind.String ? val.GetString() : null;
                        break;
                    case "AnnotationStrokeWidth":
                        if (val.TryGetSingle(out float asw)) settings.General.AnnotationStrokeWidth = asw;
                        break;
                    case "AnnotationFontSize":
                        if (val.TryGetSingle(out float afs)) settings.General.AnnotationFontSize = afs;
                        break;
                    case "EnableClipboard":
                        if (val.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.General.EnableClipboard = val.GetBoolean();
                        break;
                    case "ClipboardPanelBackground":
                        if (val.ValueKind == JsonValueKind.String) settings.General.ClipboardPanelBackground = val.GetString()!;
                        break;

                    // OCR
                    case "Model":
                        if (val.ValueKind == JsonValueKind.String) settings.Ocr.Model = val.GetString()!;
                        break;
                    case "EmbeddedModel":
                        if (val.ValueKind == JsonValueKind.String) settings.Ocr.EmbeddedModel = val.GetString()!;
                        break;
                    case "ScanExternalModels":
                        if (val.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.Ocr.ScanExternalModels = val.GetBoolean();
                        break;
                    case "UseExternalModel":
                        if (val.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.Ocr.UseExternalModel = val.GetBoolean();
                        break;
                    case "ExternalModelType":
                        if (val.ValueKind == JsonValueKind.String) settings.Ocr.ExternalModelType = val.GetString()!;
                        break;
                    case "ExternalModelPath":
                        if (val.ValueKind == JsonValueKind.String) settings.Ocr.ExternalModelPath = val.GetString()!;
                        break;
                    case "ExternalApiUrl":
                        if (val.ValueKind == JsonValueKind.String) settings.Ocr.ExternalApiUrl = val.GetString()!;
                        break;
                    case "OcrBackend":
                        if (val.ValueKind == JsonValueKind.String) settings.Ocr.OcrBackend = val.GetString()!;
                        break;
                    case "SelectedGpuIndex":
                        if (val.TryGetInt32(out int sgi)) settings.Ocr.SelectedGpuIndex = sgi;
                        break;
                    case "EnableAngleDetection":
                        if (val.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.Ocr.EnableAngleDetection = val.GetBoolean();
                        break;
                    case "MaxSideLen":
                        if (val.TryGetInt32(out int msl)) settings.Ocr.MaxSideLen = msl;
                        break;
                    case "EnableIdleSleep":
                        if (val.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.Ocr.EnableIdleSleep = val.GetBoolean();
                        break;
                    case "IdleSleepMinutes":
                        if (val.TryGetInt32(out int ism)) settings.Ocr.IdleSleepMinutes = ism;
                        break;

                    // Recording
                    case "RecordingEncoder":
                        if (val.TryGetInt32(out int re)) settings.Recording.Encoder = re;
                        break;
                    case "RecordingFps":
                        if (val.TryGetUInt32(out uint rfps)) settings.Recording.Fps = rfps;
                        break;
                    case "RecordingBitrateKbps":
                        if (val.TryGetUInt32(out uint rbk)) settings.Recording.BitrateKbps = rbk;
                        break;
                    case "RecordingShowRegionBorder":
                        if (val.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.Recording.ShowRegionBorder = val.GetBoolean();
                        break;
                    case "RecordingOutputDir":
                        if (val.ValueKind == JsonValueKind.String) settings.Recording.OutputDir = val.GetString()!;
                        break;
                    case "RecordingFilePrefix":
                        if (val.ValueKind == JsonValueKind.String) settings.Recording.FilePrefix = val.GetString()!;
                        break;
                    case "RecordingAudioBitrateKbps":
                        if (val.TryGetUInt32(out uint rabk)) settings.Recording.AudioBitrateKbps = rabk;
                        break;
                    case "RecordingCaptureCursor":
                        if (val.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.Recording.CaptureCursor = val.GetBoolean();
                        break;
                    case "RecordingCursorClickEffect":
                        if (val.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.Recording.CursorClickEffect = val.GetBoolean();
                        break;
                    case "RecordingCursorTrail":
                        if (val.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.Recording.CursorTrail = val.GetBoolean();
                        break;
                    case "RecordingCursorTrailMs":
                        if (val.TryGetUInt32(out uint rctm)) settings.Recording.CursorTrailMs = rctm;
                        break;
                    case "RecordingExcludeFloatingBarFromCapture":
                        if (val.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.Recording.ExcludeFloatingBarFromCapture = val.GetBoolean();
                        break;
                    case "RecordingShowFloatingBar":
                        if (val.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.Recording.ShowFloatingBar = val.GetBoolean();
                        break;
                    case "RecordingLiveCursorEffect":
                        if (val.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.Recording.LiveCursorEffect = val.GetBoolean();
                        break;

                    // Translation
                    case "TranslateProvider":
                        if (val.ValueKind == JsonValueKind.String) settings.Translation.Provider = val.GetString()!;
                        break;
                    case "TranslateSourceLang":
                        if (val.ValueKind == JsonValueKind.String) settings.Translation.SourceLang = val.GetString()!;
                        break;
                    case "TranslateTargetLang":
                        if (val.ValueKind == JsonValueKind.String) settings.Translation.TargetLang = val.GetString()!;
                        break;
                    case "TranslationUsageMonth":
                        if (val.ValueKind == JsonValueKind.String) settings.Translation.UsageMonth = val.GetString()!;
                        break;
                    case "BaiduAppId":
                        if (val.ValueKind == JsonValueKind.String) settings.Translation.BaiduAppId = val.GetString()!;
                        break;
                    case "BaiduSecretKey":
                        if (val.ValueKind == JsonValueKind.String) settings.Translation.BaiduSecretKey = val.GetString()!;
                        break;
                    case "BaiduApiKey":
                        if (val.ValueKind == JsonValueKind.String) settings.Translation.BaiduApiKey = val.GetString()!;
                        break;
                    case "BaiduMonthlyLimit":
                        if (val.TryGetInt32(out int bml)) settings.Translation.BaiduMonthlyLimit = bml;
                        break;
                    case "BaiduUsedCount":
                        if (val.TryGetInt32(out int buc)) settings.Translation.BaiduUsedCount = buc;
                        break;
                    case "GoogleApiKey":
                        if (val.ValueKind == JsonValueKind.String) settings.Translation.GoogleApiKey = val.GetString()!;
                        break;
                    case "GoogleMonthlyLimit":
                        if (val.TryGetInt32(out int gml)) settings.Translation.GoogleMonthlyLimit = gml;
                        break;
                    case "GoogleUsedCount":
                        if (val.TryGetInt32(out int guc)) settings.Translation.GoogleUsedCount = guc;
                        break;
                    case "OpenAiApiEndpoint":
                        if (val.ValueKind == JsonValueKind.String) settings.Translation.OpenAiApiEndpoint = val.GetString()!;
                        break;
                    case "OpenAiApiKey":
                        if (val.ValueKind == JsonValueKind.String) settings.Translation.OpenAiApiKey = val.GetString()!;
                        break;
                    case "OpenAiModel":
                        if (val.ValueKind == JsonValueKind.String) settings.Translation.OpenAiModel = val.GetString()!;
                        break;
                    case "OpenAiMonthlyLimit":
                        if (val.TryGetInt32(out int oml)) settings.Translation.OpenAiMonthlyLimit = oml;
                        break;
                    case "OpenAiUsedCount":
                        if (val.TryGetInt32(out int ouc)) settings.Translation.OpenAiUsedCount = ouc;
                        break;
                    case "VolcengineAccessKey":
                        if (val.ValueKind == JsonValueKind.String) settings.Translation.VolcengineAccessKey = val.GetString()!;
                        break;
                    case "VolcengineSecretKey":
                        if (val.ValueKind == JsonValueKind.String) settings.Translation.VolcengineSecretKey = val.GetString()!;
                        break;
                    case "VolcengineRegion":
                        if (val.ValueKind == JsonValueKind.String) settings.Translation.VolcengineRegion = val.GetString()!;
                        break;
                    case "VolcengineMonthlyLimit":
                        if (val.TryGetInt32(out int vml)) settings.Translation.VolcengineMonthlyLimit = vml;
                        break;
                    case "VolcengineUsedCount":
                        if (val.TryGetInt32(out int vuc)) settings.Translation.VolcengineUsedCount = vuc;
                        break;
                    case "AliyunAccessKeyId":
                        if (val.ValueKind == JsonValueKind.String) settings.Translation.AliyunAccessKeyId = val.GetString()!;
                        break;
                    case "AliyunAccessKeySecret":
                        if (val.ValueKind == JsonValueKind.String) settings.Translation.AliyunAccessKeySecret = val.GetString()!;
                        break;
                    case "AliyunApiEndpoint":
                        if (val.ValueKind == JsonValueKind.String) settings.Translation.AliyunApiEndpoint = val.GetString()!;
                        break;
                    case "AliyunMonthlyLimit":
                        if (val.TryGetInt32(out int aml)) settings.Translation.AliyunMonthlyLimit = aml;
                        break;
                    case "AliyunUsedCount":
                        if (val.TryGetInt32(out int auc)) settings.Translation.AliyunUsedCount = auc;
                        break;

                    // Theme
                    case "IsSidebarExpanded":
                        if (val.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.Theme.IsSidebarExpanded = val.GetBoolean();
                        break;
                    case "ThemeBackgroundImagePath":
                        settings.Theme.BackgroundImagePath = val.ValueKind == JsonValueKind.String ? val.GetString() : null;
                        break;
                    case "ThemeBackgroundBlurRadius":
                        if (val.TryGetDouble(out double bbr)) settings.Theme.BackgroundBlurRadius = bbr;
                        break;
                    case "ThemeComponentOpacity":
                        if (val.TryGetDouble(out double co)) settings.Theme.ComponentOpacity = co;
                        break;
                    case "ThemeButtonOpacity":
                        if (val.TryGetDouble(out double bo)) settings.Theme.ButtonOpacity = bo;
                        break;
                    case "ThemeEnableAcrylic":
                        if (val.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.Theme.EnableAcrylic = val.GetBoolean();
                        break;
                    case "ThemeBackgroundColorHex":
                        settings.Theme.BackgroundColorHex = val.ValueKind == JsonValueKind.String ? val.GetString() : null;
                        break;
                    case "ThemeCardColorHex":
                        settings.Theme.CardColorHex = val.ValueKind == JsonValueKind.String ? val.GetString() : null;
                        break;
                    case "ThemeTextColorHex":
                        settings.Theme.TextColorHex = val.ValueKind == JsonValueKind.String ? val.GetString() : null;
                        break;
                    case "ThemeFloatingWindowBackgroundColorHex":
                        settings.Theme.FloatingWindowBackgroundColorHex = val.ValueKind == JsonValueKind.String ? val.GetString() : null;
                        break;
                    case "ThemeFloatingWindowTextColorHex":
                        settings.Theme.FloatingWindowTextColorHex = val.ValueKind == JsonValueKind.String ? val.GetString() : null;
                        break;
                    case "LastActivePage":
                        if (val.ValueKind == JsonValueKind.String) settings.Theme.LastActivePage = val.GetString()!;
                        break;

                    // Batch
                    case "BatchExportTxt":
                        if (val.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.Batch.ExportTxt = val.GetBoolean();
                        break;
                    case "BatchExportXlsx":
                        if (val.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.Batch.ExportXlsx = val.GetBoolean();
                        break;
                    case "BatchExportDocx":
                        if (val.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.Batch.ExportDocx = val.GetBoolean();
                        break;
                    case "BatchSplitColumnsByBlocks":
                        if (val.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.Batch.SplitColumnsByBlocks = val.GetBoolean();
                        break;
                    case "BatchImageRotationIndex":
                        if (val.TryGetInt32(out int biri)) settings.Batch.ImageRotationIndex = biri;
                        break;
                    case "BatchIsSuffixEnabled":
                        if (val.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.Batch.IsSuffixEnabled = val.GetBoolean();
                        break;
                    case "BatchSuffixText":
                        if (val.ValueKind == JsonValueKind.String) settings.Batch.SuffixText = val.GetString()!;
                        break;
                    case "BatchUseOriginalFolder":
                        if (val.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.Batch.UseOriginalFolder = val.GetBoolean();
                        break;
                    case "BatchCustomOutputDir":
                        if (val.ValueKind == JsonValueKind.String) settings.Batch.CustomOutputDir = val.GetString()!;
                        break;
                    case "BatchMaxCores":
                        if (val.TryGetInt32(out int bmc)) settings.Batch.MaxCores = bmc;
                        break;
                    case "BatchGpuStreams":
                        if (val.TryGetInt32(out int bgs)) settings.Batch.GpuStreams = bgs;
                        break;
                    case "BatchCpuLimitPercent":
                        if (val.TryGetInt32(out int bclp)) settings.Batch.CpuLimitPercent = bclp;
                        break;
                    case "BatchSchedulerMode":
                        if (val.ValueKind == JsonValueKind.String) settings.Batch.SchedulerMode = val.GetString()!;
                        break;

                    // HttpApi
                    case "HttpApiPort":
                        if (val.TryGetInt32(out int hap)) settings.HttpApi.Port = hap;
                        break;
                    case "HttpApiAllowRemote":
                        if (val.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.HttpApi.AllowRemote = val.GetBoolean();
                        break;
                    case "HttpApiAccessToken":
                        if (val.ValueKind == JsonValueKind.String) settings.HttpApi.AccessToken = val.GetString()!;
                        break;
                    case "HttpApiAllowAnonymousRemote":
                        if (val.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.HttpApi.AllowAnonymousRemote = val.GetBoolean();
                        break;
                    case "HttpApiMaxCores":
                        if (val.TryGetInt32(out int hamc)) settings.HttpApi.MaxCores = hamc;
                        break;
                    case "HttpApiGpuStreams":
                        if (val.TryGetInt32(out int hags)) settings.HttpApi.GpuStreams = hags;
                        break;
                    case "HttpApiCpuLimitPercent":
                        if (val.TryGetInt32(out int haclp)) settings.HttpApi.CpuLimitPercent = haclp;
                        break;
                    case "HttpApiSchedulerMode":
                        if (val.ValueKind == JsonValueKind.String) settings.HttpApi.SchedulerMode = val.GetString()!;
                        break;
                }
            }
        }
        catch
        {
            // 兼容性迁移遇异常时容错跳过
        }
    }
}
