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
        _core = new MuClipCore(dbPath);

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

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _peerRefreshTimer?.Dispose();
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
                _sync.Dispose();
                _sync = null;
            }
            _core?.Dispose();
            _core = null;
        }
        GC.SuppressFinalize(this);
    }
}
