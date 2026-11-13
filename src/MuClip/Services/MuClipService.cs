using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MuClip.Interop;
using MuClip.Models;
using ShawMuOCR.Core.Clipboard;
using ShawMuOCR.Windows.Clipboard;

namespace MuClip.Services;

/// <summary>
/// 小沐跨端剪贴板综合服务协调器 (Facade Coordinator)
/// 负责生命周期调度、底层 Rust Core 与 Sync 引擎管理、Win32 全局热键与剪贴板监听，
/// 并通过分部类 (History, Phrases, Sync, FileTransfer) 规整各业务领域的具体实现。
/// </summary>
public partial class MuClipService : IMuClipService
{
    private readonly IClipboardService _clipboardService;
    private MuClipCore? _core;
    private MuClipSync? _sync;
    private readonly List<MuClipItem> _items = new();
    private readonly List<MuClipPhraseItem> _phrases = new();
    private readonly List<PeerDeviceInfo> _peers = new();
    private readonly List<PairedDeviceInfo> _pairedDevices = new();
    private readonly object _lock = new();
    private Timer? _peerRefreshTimer;
    private Timer? _autoBackupTimer;

    /// <summary>是否开启滚动快照自动备份。</summary>
    private bool _autoBackupEnabled = true;

    /// <summary>滚动快照保留份数（默认 7 份）。</summary>
    private int _autoBackupKeep = 7;

    /// <summary>两次快照之间的最小间隔（小时，默认 24 小时）。</summary>
    private int _autoBackupIntervalHours = 24;
    private string? _lastCapturedText;
    private long _lastCapturedTick;
    private bool _disposed;
    private string _localDeviceId = "muclip-win-" + Environment.MachineName;
    private string _localDeviceName = Environment.MachineName;
    private readonly Dictionary<long, List<PhraseConditionalRule>> _phraseRules = new();
    private readonly List<PhraseRuleTemplate> _ruleTemplates = new();
    private int _totalHistoryCount;

    public MuClipService(IClipboardService? clipboardService = null)
    {
        _clipboardService = clipboardService ?? WindowsClipboardService.Shared;
    }

    public bool IsRunning => _core != null;
    public bool IsListening => _clipboardService.IsListening;
    public bool UseNativeCoreReorder { get; set; } = true;

    private string _dbPath = string.Empty;
    public string DatabasePath => _dbPath;
    public string DataDirectory => string.IsNullOrWhiteSpace(_dbPath)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShawMuOCR", "Muclip")
        : (Path.GetDirectoryName(_dbPath) ?? string.Empty);

    /// <inheritdoc />
    /// <remarks>
    /// 交由核心实测（<c>PRAGMA cipher_version</c>）而非按构建配置推断，因此明文构建的核心
    /// 即使被要求带密钥打开也会如实返回 <c>false</c>。核心未初始化或已释放时返回 <c>null</c>。
    /// </remarks>
    public bool? IsEncrypted => _core?.IsEncrypted;

    // === 细粒度子服务门面访问入口 ===
    public IClipboardHistoryService History => this;
    public IClipboardPhraseService PhrasesService => this;
    public IClipboardSyncService SyncService => this;
    public IClipboardFileTransferService FileTransferService => this;

    public event Action? QuickPanelHotkeyTriggered;

    public static string GenerateRandomPin()
    {
        return RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
    }

    public static string GenerateRandomLongTermKeyHex()
    {
        Span<byte> randomKey = stackalloc byte[32];
        RandomNumberGenerator.Fill(randomKey);
        return Convert.ToHexString(randomKey).ToLowerInvariant();
    }

    private static bool IsValidHexKey64(string? str)
    {
        if (string.IsNullOrWhiteSpace(str)) return false;
        str = str.Trim();
        if (str.Length != 64) return false;
        foreach (char c in str)
        {
            if (!Uri.IsHexDigit(c)) return false;
        }
        return true;
    }

    private static readonly byte[] s_dpapiEntropy = Encoding.UTF8.GetBytes("ShawMuOCR_MuClip_SecureStorage_Entropy_v1");

    public static string EncryptSecret(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;
        try
        {
            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
            byte[] encrypted = ProtectedData.Protect(plainBytes, s_dpapiEntropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MuClip] DPAPI 加密密钥异常: {ex.Message}");
            return string.Empty;
        }
    }

    public static string? DecryptSecret(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return null;
        try
        {
            byte[] cipherBytes = Convert.FromBase64String(cipherText);
            byte[] decrypted = ProtectedData.Unprotect(cipherBytes, s_dpapiEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MuClip] DPAPI 解密密钥异常: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 获取或生成绑定当前 Windows 账户的 DPAPI 加密数据库密钥 (256 位十六进制)
    /// </summary>
    public static string GetOrCreateDatabaseKey(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath) || dbPath == ":memory:")
        {
            return GenerateRandomLongTermKeyHex();
        }

        string keyFilePath = dbPath + ".key";
        try
        {
            if (File.Exists(keyFilePath))
            {
                string encryptedBase64 = File.ReadAllText(keyFilePath).Trim();
                string? decrypted = DecryptSecret(encryptedBase64);
                if (IsValidHexKey64(decrypted))
                {
                    return decrypted!;
                }
            }

            // 首次运行：随机生成 256 位高强度密钥，并通过 DPAPI (DataProtectionScope.CurrentUser) 加密保存
            string newKey = GenerateRandomLongTermKeyHex();
            string encrypted = EncryptSecret(newKey);
            if (!string.IsNullOrEmpty(encrypted))
            {
                File.WriteAllText(keyFilePath, encrypted);
            }
            return newKey;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MuClip] 获取/生成数据库加密密钥异常: {ex.Message}");
            return GenerateRandomLongTermKeyHex();
        }
    }

    /// <summary>
    /// 滚动自动备份：把库文件整体快照到 <c>&lt;库目录&gt;\auto-backup</c>，并按数量清理旧的。
    /// </summary>
    /// <summary>
    /// 自动快照倒计时调度：计算距上次备份剩余时间，设置单次精确倒计时（中间零唤醒、零轮询）。
    /// </summary>
    private void ScheduleNextAutoBackup()
    {
        if (!_autoBackupEnabled || _autoBackupIntervalHours <= 0)
        {
            _autoBackupTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        DateTime? latestBackupTime = GetLatestAutoBackupTime();
        TimeSpan delay;

        if (latestBackupTime == null)
        {
            // 从未备份过，立即执行备份
            delay = TimeSpan.Zero;
        }
        else
        {
            TimeSpan elapsed = DateTime.UtcNow - latestBackupTime.Value;
            TimeSpan interval = TimeSpan.FromHours(_autoBackupIntervalHours);

            if (elapsed >= interval)
            {
                // 已超过设定的备份周期，立即执行备份
                delay = TimeSpan.Zero;
            }
            else
            {
                // 距离下次备份还剩 remaining 时间，精确单次倒计时至目标时刻（中途彻底休眠）
                TimeSpan remaining = interval - elapsed;
                delay = remaining > TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1);
            }
        }

        if (_autoBackupTimer == null)
        {
            _autoBackupTimer = new Timer(_ => OnAutoBackupCountdownFired(), null, delay, Timeout.InfiniteTimeSpan);
        }
        else
        {
            _autoBackupTimer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnAutoBackupCountdownFired()
    {
        if (!_autoBackupEnabled) return;

        try
        {
            TriggerAutoBackup();
        }
        finally
        {
            // 备份完成后，开启下一个周期的单次完整倒计时（中途彻底休眠不轮询）
            if (_autoBackupEnabled && _autoBackupIntervalHours > 0)
            {
                TimeSpan nextInterval = TimeSpan.FromHours(_autoBackupIntervalHours);
                _autoBackupTimer?.Change(nextInterval, Timeout.InfiniteTimeSpan);
            }
            else
            {
                _autoBackupTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private DateTime? GetLatestAutoBackupTime()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_dbPath)) return null;
            string? dbDir = Path.GetDirectoryName(_dbPath);
            if (string.IsNullOrWhiteSpace(dbDir)) return null;

            string backupDir = Path.Combine(dbDir, "auto-backup");
            if (!Directory.Exists(backupDir)) return null;

            var dirInfo = new DirectoryInfo(backupDir);
            var latestFile = dirInfo.GetFiles()
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();

            return latestFile?.LastWriteTimeUtc;
        }
        catch
        {
            return null;
        }
    }

    public void UpdateAutoBackupSettings(int keep, int intervalHours)
    {
        UpdateAutoBackupSettings(true, keep, intervalHours);
    }

    public void UpdateAutoBackupSettings(bool enabled, int keep, int intervalHours)
    {
        _autoBackupEnabled = enabled;
        _autoBackupKeep = Math.Max(1, keep);
        _autoBackupIntervalHours = Math.Max(0, intervalHours);
        ScheduleNextAutoBackup();
    }

    public AutoBackupResult TriggerAutoBackup(int? keep = null, int? intervalHours = null)
    {
        try
        {
            MuClipCore? core = _core;
            if (core == null || string.IsNullOrWhiteSpace(_dbPath))
                return new AutoBackupResult { Error = "核心服务未初始化或数据库路径为空" };

            string? dbDir = Path.GetDirectoryName(_dbPath);
            if (string.IsNullOrWhiteSpace(dbDir))
                return new AutoBackupResult { Error = "无法获取数据库上级目录" };

            int effectiveKeep = keep.HasValue ? Math.Max(1, keep.Value) : _autoBackupKeep;
            int effectiveInterval = intervalHours.HasValue ? Math.Max(0, intervalHours.Value) : _autoBackupIntervalHours;

            AutoBackupResult result = core.AutoBackup(
                Path.Combine(dbDir, "auto-backup"), effectiveKeep, effectiveInterval);

            if (!string.IsNullOrEmpty(result.Error))
            {
                Console.WriteLine($"[MuClip] 自动备份失败: {result.Error}");
            }
            else if (result.Created)
            {
                Console.WriteLine(
                    $"[MuClip] 自动备份完成: {result.Path} ({result.Size} B)，清理 {result.Pruned} 个，保留 {result.Kept} 个");

                if (_autoBackupEnabled && _autoBackupIntervalHours > 0)
                {
                    TimeSpan nextInterval = TimeSpan.FromHours(_autoBackupIntervalHours);
                    _autoBackupTimer?.Change(nextInterval, Timeout.InfiniteTimeSpan);
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MuClip] 自动备份异常: {ex.Message}");
            return new AutoBackupResult { Error = ex.Message };
        }
    }

    public bool RegisterQuickPanelHotkey(uint modifiers = 0x0002, uint vk = 0xC0)
    {
        if (_clipboardService.IsListening)
        {
            _clipboardService.HotkeyTriggered -= OnQuickPanelHotkeyTriggered;
            _clipboardService.HotkeyTriggered += OnQuickPanelHotkeyTriggered;
            return _clipboardService.RegisterHotkey(1, modifiers, vk);
        }
        return false;
    }

    private void OnQuickPanelHotkeyTriggered()
    {
        QuickPanelHotkeyTriggered?.Invoke();
    }

    public void Initialize(
        string? dbPath = null,
        bool startSync = true,
        ushort syncPort = 42424,
        string? pairingPin = null,
        bool startListener = true,
        string? customDeviceId = null,
        string? customDeviceName = null)
    {
        if (_core != null) return;

        if (pairingPin != null && (string.IsNullOrWhiteSpace(pairingPin) || pairingPin == "000000"))
        {
            throw new ArgumentException("不允许使用空配对码或弱默认配对码 '000000'。", nameof(pairingPin));
        }

        if (string.IsNullOrWhiteSpace(dbPath))
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = Path.Combine(appData, "ShawMuOCR", "Muclip");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            dbPath = Path.Combine(dir, "muclip.db");
        }
        else
        {
            string? dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        _dbPath = dbPath;
        string dbKey = GetOrCreateDatabaseKey(dbPath);
        try
        {
            _core = new MuClipCore(dbPath, dbKey);
        }
        catch (Exception ex)
        {
            // 核心数据库打开失败：极大概率是用户从其他电脑/系统迁移了旧库，
            // 导致当前机器的 DPAPI 无法解密旧密钥，或者旧数据库文件损坏。
            // 自动重命名归档损坏/无法解密的旧库，生成新的干净数据库自愈，防止程序永久闪退。
            Console.WriteLine($"[MuClip] 核心数据库打开失败，启动自动归档与自愈流程: {ex.Message}");
            if (File.Exists(dbPath))
            {
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string bakPath = dbPath + $".corrupted_{timestamp}.bak";
                try { File.Move(dbPath, bakPath, true); } catch { }

                string keyPath = dbPath + ".key";
                if (File.Exists(keyPath))
                {
                    try { File.Move(keyPath, keyPath + $".corrupted_{timestamp}.bak", true); } catch { }
                }

                // 重新生成新的合法密钥并重试创建新数据库
                dbKey = GetOrCreateDatabaseKey(dbPath);
                _core = new MuClipCore(dbPath, dbKey);
            }
            else
            {
                throw;
            }
        }

        // 启动精准单次倒计时调度（计算距下次备份剩余时间，中间零轮询，到点触发一次后进入下个完整周期）
        ScheduleNextAutoBackup();

        if (!string.IsNullOrEmpty(customDeviceId))
        {
            _localDeviceId = customDeviceId;
        }
        else
        {
            string? persistedDeviceId = _core.GetConfig("device_id");
            if (string.IsNullOrEmpty(persistedDeviceId))
            {
                persistedDeviceId = "muclip-win-" + Guid.NewGuid().ToString("N")[..8];
                _core.SetConfig("device_id", persistedDeviceId);
            }
            _localDeviceId = persistedDeviceId;
        }

        if (!string.IsNullOrEmpty(customDeviceName))
        {
            _localDeviceName = customDeviceName;
        }
        else
        {
            string? persistedDeviceName = _core.GetConfig("device_name");
            if (string.IsNullOrEmpty(persistedDeviceName))
            {
                persistedDeviceName = Environment.MachineName;
                _core.SetConfig("device_name", persistedDeviceName);
            }
            _localDeviceName = persistedDeviceName;
        }

        // 读取自定义同步端口（如果有持久化配置）
        string? persistedSyncPort = _core.GetConfig("custom_sync_port");
        if (ushort.TryParse(persistedSyncPort, out ushort customPort) && customPort > 0)
        {
            syncPort = customPort;
        }

        // 会话密钥 / PIN 恢复与派生
        string? ltk = _core.GetConfig("long_term_key");
        string? decryptedLtk = !string.IsNullOrEmpty(ltk) ? DecryptSecret(ltk) : null;
        string? savedPin = _core.GetConfig("pairing_pin");
        string? decryptedPin = !string.IsNullOrEmpty(savedPin) ? DecryptSecret(savedPin) : null;

        string activeSessionKey;
        if (!string.IsNullOrEmpty(pairingPin))
        {
            if (pairingPin.Length == 64)
            {
                activeSessionKey = pairingPin;
                PairingPin = "已信任 (长期密钥)";
                _core.SetConfig("long_term_key", EncryptSecret(pairingPin));
                _core.SetConfig("pairing_pin", "");
            }
            else
            {
                activeSessionKey = pairingPin;
                PairingPin = pairingPin;
                _core.SetConfig("pairing_pin", EncryptSecret(pairingPin));
            }
        }
        else if (!string.IsNullOrEmpty(decryptedLtk) && IsValidHexKey64(decryptedLtk))
        {
            activeSessionKey = decryptedLtk;
            PairingPin = "已信任 (长期密钥)";
        }
        else if (!string.IsNullOrEmpty(decryptedPin) && decryptedPin.Length == 6 && decryptedPin != "000000")
        {
            activeSessionKey = decryptedPin;
            PairingPin = decryptedPin;
        }
        else
        {
            activeSessionKey = GenerateRandomLongTermKeyHex();
            PairingPin = "已信任 (长期密钥)";
            _core.SetConfig("long_term_key", EncryptSecret(activeSessionKey));
            _core.SetConfig("pairing_pin", "");
        }

        string? autoDisc = _core.GetConfig("auto_discovery");
        if (!string.IsNullOrEmpty(autoDisc))
        {
            _autoDiscoveryEnabled = autoDisc != "0";
        }

        LoadPairedDevices();
        LoadPhraseRules();
        LoadRuleTemplates();

        if (startSync)
        {
            try
            {
                string deviceId = _localDeviceId;
                string deviceName = _localDeviceName;
                _sync = new MuClipSync(deviceId, deviceName, "Windows", syncPort, 42110, activeSessionKey);
                if (!_autoDiscoveryEnabled)
                {
                    _sync.SetAutoDiscovery(false);
                }
                _sync.ClipboardReceived += OnRemoteClipboardReceived;
                _sync.PairRequestReceived += OnRemotePairRequestReceived;
                _sync.IncomingPairRequestReceived += OnIncomingPairRequestReceived;
                _sync.PairEstablished += OnPairEstablished;
                _sync.PeerUnpaired += OnRemotePeerUnpaired;
                _sync.PeerAuthRejected += OnRemotePeerAuthRejected;
                _sync.FileProgress += (s, e) => FileProgress?.Invoke(e);
                _sync.FileCompleted += (s, e) => FileCompleted?.Invoke(e);
                _sync.FileOfferReceived += (s, e) => FileOfferReceived?.Invoke(e);

                if (string.IsNullOrEmpty(_fileSaveDirectory))
                {
                    string defaultSaveDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "Downloads",
                        "MuClip"
                    );
                    try
                    {
                        if (!Directory.Exists(defaultSaveDir)) Directory.CreateDirectory(defaultSaveDir);
                    }
                    catch { }
                    _fileSaveDirectory = defaultSaveDir;
                }
                _sync.SetFileSaveDirectory(_fileSaveDirectory);

                // 将设备表中已持久化的对端密钥注册至底层 Rust Keyring
                lock (_lock)
                {
                    foreach (var dev in _pairedDevices)
                    {
                        string spec = BuildPairedDeviceSpec(dev.DeviceId, dev.Ip, dev.SyncPort, dev.DeviceName, dev.DeviceType, dev.PeerKey);
                        _sync.AddPairedDevice(spec);
                    }
                }

                LoadManualPeersIntoSync();
                RefreshPeers();

                _peerRefreshTimer = new Timer(_ => RefreshPeers(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MuClip] 同步服务启动异常: {ex.Message}");
            }
        }

        RefreshHistory();
        RefreshPhrases();
        lock (_lock)
        {
            if (_phrases.Count == 0)
            {
                AddPhrase("您好，感谢您的来信！祝工作顺利。");
                AddPhrase("收到，我马上处理并给您反馈。");
                AddPhrase("https://github.com/katomegumi1999/ShawMuOCR");
            }
        }

        if (startListener)
        {
            _clipboardService.ExternalTextChanged += OnSystemClipboardTextChanged;
            _clipboardService.StartListening();
        }
    }

    private void OnSystemClipboardTextChanged(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        lock (_lock)
        {
            if (string.Equals(_lastCapturedText, text, StringComparison.Ordinal))
            {
                if (Environment.TickCount64 - _lastCapturedTick < 2000)
                {
                    return;
                }
            }
            _lastCapturedText = text;
            _lastCapturedTick = Environment.TickCount64;
        }

        _core?.AddHistory(text);
        RefreshHistory();

        // 即使内容重复，也推送到局域网配对设备（异步派发至后台线程，绝不阻塞剪贴板监听器）
        if (_sync != null)
        {
            Task.Run(() =>
            {
                try
                {
                    _sync.Broadcast(text, "text");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MuClip] 后台广播异常: {ex.Message}");
                }
            });
        }
    }

    public BackupSummary? ExportEncryptedBackup(string exportPath, string password)
    {
        if (_core == null) return null;
        return _core.ExportEncryptedBackup(exportPath, password);
    }

    public BackupSummary? ImportEncryptedBackup(string importPath, string password, bool replaceAll)
    {
        if (_core == null) return null;
        var summary = _core.ImportEncryptedBackup(importPath, password, replaceAll);
        if (summary != null)
        {
            RefreshHistory();
            RefreshPhrases();
            LoadPhraseRules();
            LoadRuleTemplates();
        }
        return summary;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _peerRefreshTimer?.Dispose();
            _autoBackupTimer?.Dispose();
            _clipboardService.ExternalTextChanged -= OnSystemClipboardTextChanged;
            _clipboardService.HotkeyTriggered -= OnQuickPanelHotkeyTriggered;
            _clipboardService.StopListening();
            if (_sync != null)
            {
                _sync.ClipboardReceived -= OnRemoteClipboardReceived;
                _sync.PairRequestReceived -= OnRemotePairRequestReceived;
                _sync.IncomingPairRequestReceived -= OnIncomingPairRequestReceived;
                _sync.PairEstablished -= OnPairEstablished;
                _sync.PeerUnpaired -= OnRemotePeerUnpaired;
                _sync.PeerAuthRejected -= OnRemotePeerAuthRejected;
                _sync.Dispose();
                _sync = null;
            }
            _core?.Dispose();
            _core = null;
        }
        GC.SuppressFinalize(this);
    }
}
