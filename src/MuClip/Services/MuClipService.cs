using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MuClip.Interop;
using MuClip.Models;
using ShawMuOCR.Core.Clipboard;
using ShawMuOCR.Windows.Clipboard;

namespace MuClip.Services;

public class MuClipService : IMuClipService
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
    private string _pairingPin = string.Empty;
    private string _localDeviceId = "muclip-win-" + Environment.MachineName;
    private string _localDeviceName = Environment.MachineName;

    public MuClipService(IClipboardService? clipboardService = null)
    {
        _clipboardService = clipboardService ?? WindowsClipboardService.Shared;
    }

    public bool IsRunning => _core != null;
    public bool IsListening => _clipboardService.IsListening;
    public bool IsSyncEnabled => _sync != null;
    public ushort SyncPort => _sync?.BoundPort ?? 42424;

    public string PairingPin
    {
        get
        {
            if (string.IsNullOrEmpty(_pairingPin))
            {
                throw new InvalidOperationException("MuClipService.PairingPin 尚未初始化，请先调用 Initialize()。");
            }
            return _pairingPin;
        }
        private set => _pairingPin = value;
    }

    private bool _autoDiscoveryEnabled = true;

    public bool AutoDiscoveryEnabled
    {
        get => _sync?.IsAutoDiscoveryEnabled() ?? _autoDiscoveryEnabled;
        set
        {
            _autoDiscoveryEnabled = value;
            _sync?.SetAutoDiscovery(value);
            try
            {
                _core?.SetConfig("auto_discovery", value ? "1" : "0");
            }
            catch { }
        }
    }

    public void TriggerDiscoveryScan()
    {
        _sync?.TriggerScan();
        RefreshPeers();
    }

    public int TestPeerConnection(string peerIp, int peerPort)
    {
        if (_sync == null || string.IsNullOrWhiteSpace(peerIp)) return -1;
        var (host, port) = ParseEndpoint(peerIp, peerPort > 0 ? peerPort : 42424);
        string addr = $"{host}:{port}";
        int latency = _sync.TestPeer(addr);
        RefreshPeers();
        return latency;
    }

    private int _totalHistoryCount;

    public IReadOnlyList<MuClipItem> HistoryItems
    {
        get
        {
            lock (_lock)
            {
                return _items.ToList();
            }
        }
    }

    public int TotalHistoryCount
    {
        get
        {
            lock (_lock)
            {
                return Math.Max(_items.Count, _totalHistoryCount);
            }
        }
    }

    public IReadOnlyList<MuClipPhraseItem> Phrases
    {
        get
        {
            lock (_lock)
            {
                return _phrases.ToList();
            }
        }
    }

    public IReadOnlyList<PeerDeviceInfo> ActivePeers
    {
        get
        {
            lock (_lock)
            {
                return _peers.ToList();
            }
        }
    }

    public IReadOnlyList<PairedDeviceInfo> PairedDevices
    {
        get
        {
            lock (_lock)
            {
                return _pairedDevices.ToList();
            }
        }
    }

    public event Action? HistoryChanged;
    public event Action? PhrasesChanged;
    public event Action? PeersChanged;
    public event Action? PairedDevicesChanged;
    public event Action<string>? ClipboardReceived;
    public event Action<string, string, string>? PairRequestReceived;
    public event Action<string, string, string, string>? IncomingPairRequested;
    public event Action<string, string>? PairEstablished;
    public event Action<string, string>? RemotePeerUnpaired;
    public event Action? QuickPanelHotkeyTriggered;
    public event Action<FileProgressEvent>? FileProgress;
    public event Action<FileCompletedEvent>? FileCompleted;
    public event Action<FileOfferEvent>? FileOfferReceived;

    private string _fileSaveDirectory = string.Empty;

    public static string GenerateRandomPin()
    {
        return Random.Shared.Next(100000, 1000000).ToString("D6");
    }

    private static readonly byte[] s_dpapiEntropy = System.Text.Encoding.UTF8.GetBytes("ShawMuOCR_MuClip_SecureStorage_Entropy_v1");

    public static string EncryptSecret(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                byte[] plainBytes = System.Text.Encoding.UTF8.GetBytes(plainText);
                byte[] cipherBytes = System.Security.Cryptography.ProtectedData.Protect(plainBytes, s_dpapiEntropy, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(cipherBytes);
            }
        }
        catch { }
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plainText));
    }

    public static string? DecryptSecret(string cipherText)
    {
        if (string.IsNullOrWhiteSpace(cipherText)) return null;
        try
        {
            byte[] cipherBytes = Convert.FromBase64String(cipherText);
            if (OperatingSystem.IsWindows())
            {
                byte[] plainBytes = System.Security.Cryptography.ProtectedData.Unprotect(cipherBytes, s_dpapiEntropy, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return System.Text.Encoding.UTF8.GetString(plainBytes);
            }
            return System.Text.Encoding.UTF8.GetString(cipherBytes);
        }
        catch
        {
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

    public void Initialize(string? dbPath = null, bool startSync = true, ushort syncPort = 42424, string? pairingPin = null, bool startListener = true, string? customDeviceId = null, string? customDeviceName = null)
    {
        if (_core != null) return;

        if (pairingPin != null && (string.IsNullOrWhiteSpace(pairingPin) || pairingPin == "000000"))
        {
            throw new ArgumentException("不允许使用空配对码或弱默认配对码 '000000'。", nameof(pairingPin));
        }

        if (!string.IsNullOrWhiteSpace(customDeviceId)) _localDeviceId = customDeviceId;
        if (!string.IsNullOrWhiteSpace(customDeviceName)) _localDeviceName = customDeviceName;

        if (string.IsNullOrWhiteSpace(dbPath))
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            dbPath = Path.Combine(appData, "ShawMuOCR", "muclip.db");
        }

        string? dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string deviceIdFilePath = Path.Combine(dir ?? "", "muclip_device_id.txt");
        if (string.IsNullOrWhiteSpace(customDeviceId))
        {
            if (File.Exists(deviceIdFilePath))
            {
                try
                {
                    string savedId = File.ReadAllText(deviceIdFilePath).Trim();
                    if (!string.IsNullOrWhiteSpace(savedId))
                    {
                        _localDeviceId = savedId;
                    }
                }
                catch { }
            }
            else
            {
                try
                {
                    string suffix = Guid.NewGuid().ToString("N")[..6];
                    _localDeviceId = $"muclip-win-{Environment.MachineName}-{suffix}";
                    File.WriteAllText(deviceIdFilePath, _localDeviceId);
                }
                catch { }
            }
        }

        _core = new MuClipCore(dbPath);

        // 1. 优先从 SQLite 数据库 secure_config 表中解密读取协商好的 256 位长期主密钥 (Long-Term Key)
        string? activeSessionKey = null;
        string? encryptedLtk = _core.GetConfig("long_term_key");
        if (!string.IsNullOrWhiteSpace(encryptedLtk))
        {
            string? decrypted = DecryptSecret(encryptedLtk);
            if (!string.IsNullOrWhiteSpace(decrypted) && decrypted.Length == 64)
            {
                activeSessionKey = decrypted;
            }
        }

        // 2. 若尚未协商长期密钥，解密读取首次配对 PIN 码
        if (string.IsNullOrEmpty(activeSessionKey))
        {
            string? encryptedPinFromDb = _core.GetConfig("pairing_pin");
            if (!string.IsNullOrWhiteSpace(encryptedPinFromDb))
            {
                string? decrypted = DecryptSecret(encryptedPinFromDb);
                if (!string.IsNullOrWhiteSpace(decrypted) && decrypted.Length == 6 && decrypted != "000000")
                {
                    activeSessionKey = decrypted;
                }
            }
        }

        // 3. 初次安装启动若无任何记录，则使用传入的有效 PIN 或动态生成随机 6 位 PIN 并加密写入数据库
        if (string.IsNullOrEmpty(activeSessionKey))
        {
            activeSessionKey = !string.IsNullOrWhiteSpace(pairingPin)
                ? pairingPin
                : GenerateRandomPin();
            _core.SetConfig("pairing_pin", EncryptSecret(activeSessionKey));
        }

        PairingPin = activeSessionKey.Length == 6 ? activeSessionKey : "已信任 (长期密钥)";
        try
        {
            string? savedAutoDisc = _core.GetConfig("auto_discovery");
            if (savedAutoDisc == "0" || string.Equals(savedAutoDisc, "false", StringComparison.OrdinalIgnoreCase))
            {
                _autoDiscoveryEnabled = false;
            }
        }
        catch { }
        LoadPairedDevices();

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

                // 将已配对设备自动注入底层 Rust 广播白名单（携带最近 IP/端口以支持单播与心跳保活）
                lock (_lock)
                {
                    foreach (var dev in _pairedDevices)
                    {
                        string spec = !string.IsNullOrEmpty(dev.Ip)
                            ? $"{dev.DeviceId}|{dev.Ip}|{(dev.SyncPort > 0 ? dev.SyncPort : 42424)}|{dev.DeviceName}"
                            : dev.DeviceId;
                        _sync.AddPairedDevice(spec);
                    }
                }

                // 恢复独立配置的静态/端口映射节点
                LoadManualPeersIntoSync();
                RefreshPeers();

                _peerRefreshTimer = new Timer(_ => RefreshPeers(), null, 2000, 3000);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MuClip] 同步引擎初始化异常: {ex.Message}");
            }
        }

        if (startListener)
        {
            try
            {
                _clipboardService.ExternalTextChanged -= OnSystemClipboardTextChanged;
                _clipboardService.ExternalTextChanged += OnSystemClipboardTextChanged;
                _clipboardService.StartListening();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MuClip] 剪贴板监听器初始化异常: {ex.Message}");
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
    }

    private void OnSystemClipboardTextChanged(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        long currentTick = Environment.TickCount64;

        lock (_lock)
        {
            if (string.Equals(text, _lastCapturedText, StringComparison.Ordinal) && (currentTick - _lastCapturedTick < 200))
            {
                return;
            }

            _lastCapturedText = text;
            _lastCapturedTick = currentTick;
        }

        // 存入 Rust 核心与 LRU 缓存：
        // 若内容在历史中已存在（重复项），Rust 核心会自动将其从原位置移除并插入到首位（更新 last_used_at）
        _core?.AddHistory(text);

        // 本地优先：立即刷新列表并触发事件（0 延迟，毫秒级响应并置顶）
        RefreshHistory();

        // 即使内容重复，也推送到局域网配对设备（异步派发至后台线程，绝不阻塞剪贴板监听器与面板响应）
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

    public void CopyToSystemClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        lock (_lock)
        {
            _lastCapturedText = text;
            _lastCapturedTick = Environment.TickCount64;
        }

        _clipboardService.SetText(text, suppressNotification: true);

        // 用户在 MuClip 界面中主动点击某项历史记录复制时，该项在历史中同样前移至首位
        _core?.AddHistory(text);
        RefreshHistory();
    }

    private void OnRemoteClipboardReceived(object? sender, RemoteClipboardEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Content)) return;

        // 存入 Rust 核心与 LRU 缓存（重复项自动前移）
        _core?.AddHistory(e.Content);

        // 写入本机系统剪贴板供用户直接 Ctrl+V 使用，同时通过序列号精确抑制回显防止监听器重复广播
        lock (_lock)
        {
            _lastCapturedText = e.Content;
            _lastCapturedTick = Environment.TickCount64;

            var paired = _pairedDevices.FirstOrDefault(p =>
                (!string.IsNullOrEmpty(e.DeviceId) && p.DeviceId == e.DeviceId) ||
                (!string.IsNullOrEmpty(e.DeviceName) && p.DeviceName == e.DeviceName));
            if (paired != null)
            {
                paired.IsOnline = true;
                paired.LastSyncAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                SavePairedDevices();
            }
        }
        _clipboardService.SetText(e.Content, suppressNotification: true);

        // 刷新列表并触发事件
        RefreshHistory();
        PairedDevicesChanged?.Invoke();
        ClipboardReceived?.Invoke(e.Content);
    }

    private void OnRemotePairRequestReceived(object? sender, PairRequestEventArgs e)
    {
        if (string.Equals(e.DeviceId, _localDeviceId, StringComparison.OrdinalIgnoreCase)) return;

        if (!string.IsNullOrWhiteSpace(e.Pin))
        {
            lock (_lock)
            {
                var match = _peers.FirstOrDefault(p => p.DeviceId == e.DeviceId);
                string ip = match?.Ip ?? string.Empty;
                int port = match?.SyncPort > 0 ? match.SyncPort : 42424;

                var existing = _pairedDevices.FirstOrDefault(p => p.DeviceId == e.DeviceId);
                if (existing != null)
                {
                    existing.DeviceName = !string.IsNullOrEmpty(e.DeviceName) ? e.DeviceName : existing.DeviceName;
                    if (!string.IsNullOrEmpty(ip)) existing.Ip = ip;
                    if (port > 0) existing.SyncPort = port;
                    if (match != null) existing.IsManual = match.IsManual || existing.IsManual;
                    existing.IsOnline = true;
                    existing.LastSyncAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                    SavePairedDevices();

                    RefreshPeers();
                    PairedDevicesChanged?.Invoke();
                }
            }

            PairRequestReceived?.Invoke(e.DeviceId, e.DeviceName, e.Pin);
        }
    }

    private void OnIncomingPairRequestReceived(object? sender, IncomingPairRequestEventArgs e)
    {
        if (string.Equals(e.DeviceId, _localDeviceId, StringComparison.OrdinalIgnoreCase)) return;

        IncomingPairRequested?.Invoke(e.RequestId, e.DeviceId, e.DeviceName, e.PeerIp);
    }

    public static (string Host, int Port) ParseEndpoint(string endpoint, int defaultPort = 42424)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return (string.Empty, defaultPort > 0 ? defaultPort : 42424);

        string trimmed = endpoint.Trim();
        int fallbackPort = defaultPort > 0 ? defaultPort : 42424;

        if (trimmed.StartsWith('['))
        {
            int closeIdx = trimmed.IndexOf(']');
            if (closeIdx > 1)
            {
                string ipv6Host = trimmed[1..closeIdx].Trim();
                string rest = trimmed[(closeIdx + 1)..].Trim();
                if (rest.StartsWith(':') && int.TryParse(rest[1..].Trim(), out int p6) && p6 is > 0 and <= 65535)
                {
                    return (ipv6Host, p6);
                }
                return (ipv6Host, fallbackPort);
            }
        }

        int colonIdx = trimmed.LastIndexOf(':');
        if (colonIdx > 0 && colonIdx < trimmed.Length - 1)
        {
            string hostPart = trimmed[..colonIdx].Trim();
            string portPart = trimmed[(colonIdx + 1)..].Trim();
            if (!string.IsNullOrEmpty(hostPart) && int.TryParse(portPart, out int parsedPort) && parsedPort is > 0 and <= 65535)
            {
                return (hostPart, parsedPort);
            }
        }

        return (trimmed, fallbackPort);
    }

    private void OnPairEstablished(object? sender, PairEstablishedEventArgs e)
    {
        if (string.Equals(e.DeviceId, _localDeviceId, StringComparison.OrdinalIgnoreCase)) return;

        if (!string.IsNullOrWhiteSpace(e.Pin))
        {
            UpdatePairingPin(e.Pin);
        }

        var (parsedHost, parsedPort) = ParseEndpoint(e.PeerIp, 42424);
        lock (_lock)
        {
            var peerMatch = _peers.FirstOrDefault(p =>
                (!string.IsNullOrEmpty(e.DeviceId) && p.DeviceId == e.DeviceId) ||
                (!string.IsNullOrEmpty(parsedHost) && string.Equals(p.Ip, parsedHost, StringComparison.OrdinalIgnoreCase)));
            if (peerMatch != null)
            {
                if (!string.IsNullOrEmpty(peerMatch.Ip)) parsedHost = peerMatch.Ip;
                if (peerMatch.SyncPort > 0) parsedPort = peerMatch.SyncPort;
            }
        }

        AddOrUpdatePairedDevice(e.DeviceId, e.DeviceName, parsedHost, parsedPort);
        PairEstablished?.Invoke(e.DeviceId, e.DeviceName);
    }

    private void OnRemotePeerUnpaired(object? sender, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) ||
            string.Equals(deviceId, _localDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string removedName = deviceId;
        bool removed = false;
        lock (_lock)
        {
            var item = _pairedDevices.FirstOrDefault(p =>
                string.Equals(p.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            if (item != null)
            {
                if (!string.IsNullOrEmpty(item.DeviceName))
                {
                    removedName = item.DeviceName;
                }
                _pairedDevices.Remove(item);
                SavePairedDevices();
                removed = true;
            }
            else
            {
                var peer = _peers.FirstOrDefault(p =>
                    string.Equals(p.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
                if (peer != null && !string.IsNullOrEmpty(peer.DeviceName))
                {
                    removedName = peer.DeviceName;
                }
            }
        }

        RefreshPeers();
        PairedDevicesChanged?.Invoke();
        if (removed)
        {
            RemotePeerUnpaired?.Invoke(deviceId, removedName);
        }
    }

    public void AddRecord(string content, bool broadcast = true)
    {
        if (_core == null || string.IsNullOrWhiteSpace(content)) return;

        _core.AddHistory(content);
        RefreshHistory();

        if (broadcast && _sync != null)
        {
            Task.Run(() =>
            {
                try
                {
                    _sync.Broadcast(content, "text");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MuClip] 后台广播异常: {ex.Message}");
                }
            });
        }
    }

    public void DeleteRecord(long id)
    {
        if (_core == null) return;
        _core.DeleteHistory(id);
        RefreshHistory();
    }

    public void ClearRecords()
    {
        if (_core == null) return;
        _core.ClearHistory();
        RefreshHistory();
    }

    public List<MuClipItem> QueryHistoryPage(int offset, int limit = 100)
    {
        if (_core == null || offset < 0 || limit <= 0) return new List<MuClipItem>();

        lock (_lock)
        {
            if (offset + limit <= _items.Count)
            {
                return _items.Skip(offset).Take(limit).ToList();
            }
        }

        var records = _core.QueryHistoryPage(offset, limit);
        return records.Select(r => MuClipItem.FromRecord(r)).ToList();
    }

    public List<MuClipItem> Search(string query, int limit = 500)
    {
        if (_core == null) return new List<MuClipItem>();
        if (string.IsNullOrWhiteSpace(query))
        {
            return QueryHistoryPage(0, limit);
        }

        var records = _core.SearchHistory(query, limit);
        return records.Select(r => MuClipItem.FromRecord(r)).ToList();
    }

    public int Broadcast(string content)
    {
        if (_sync == null || string.IsNullOrWhiteSpace(content)) return 0;
        return _sync.Broadcast(content, "text");
    }

    public void UpdatePairingPin(string pin)
    {
        if (string.IsNullOrWhiteSpace(pin) || pin == "000000")
        {
            throw new ArgumentException("不允许使用空配对码或弱默认配对码 '000000'。", nameof(pin));
        }
        if (pin.Trim() == "已信任 (长期密钥)")
        {
            return;
        }
        if (pin.Length == 64)
        {
            PairingPin = "已信任 (长期密钥)";
            _sync?.SetPairingPin(pin);
            try
            {
                if (_core != null)
                {
                    _core.SetConfig("long_term_key", EncryptSecret(pin));
                }
            }
            catch { }
        }
        else
        {
            PairingPin = pin;
            _sync?.SetPairingPin(pin);
            try
            {
                if (_core != null)
                {
                    _core.SetConfig("pairing_pin", EncryptSecret(pin));
                }
            }
            catch { }
        }
    }

    public async Task<(bool Success, string DeviceId, string DeviceName, string Message)> RequestPairAsync(string peerIp, int peerPort, string pin, uint timeoutSecs = 60)
    {
        if (_sync == null || string.IsNullOrWhiteSpace(pin))
        {
            return (false, string.Empty, string.Empty, "同步引擎未启动或配对码为空");
        }

        var (host, port) = ParseEndpoint(peerIp, peerPort > 0 ? peerPort : 42424);
        string addr = $"{host}:{port}";
        var res = await Task.Run(() => _sync.RequestPair(addr, pin, timeoutSecs)).ConfigureAwait(false);

        if (res.Success)
        {
            if (!string.IsNullOrEmpty(res.LongTermKey))
            {
                UpdatePairingPin(res.LongTermKey);
            }
            else
            {
                UpdatePairingPin(pin);
            }
            AddOrUpdatePairedDevice(res.ResponderId, res.ResponderName, host, port);
            return (true, res.ResponderId, res.ResponderName, "配对成功！");
        }
        else
        {
            return (false, string.Empty, string.Empty, res.ErrorMessage);
        }
    }

    public int RespondPair(string requestId, string pin, bool accept)
    {
        if (_sync == null) return -1;
        int code = _sync.RespondPair(requestId, pin, accept);
        return code;
    }

    public bool SendPairRequest(string peerIp, int peerPort, string pin)
    {
        return SendPairRequest(peerIp, peerPort, pin, null, null);
    }

    public bool SendPairRequest(string peerIp, int peerPort, string pin, string? deviceId = null, string? deviceName = null)
    {
        if (_sync == null || string.IsNullOrWhiteSpace(pin)) return false;
        var (host, port) = ParseEndpoint(peerIp, peerPort > 0 ? peerPort : 42424);
        string addr = $"{host}:{port}";
        var res = _sync.RequestPair(addr, pin, 60);
        if (res.Success)
        {
            UpdatePairingPin(!string.IsNullOrEmpty(res.LongTermKey) ? res.LongTermKey : pin);
            AddOrUpdatePairedDevice(res.ResponderId, res.ResponderName, host, port);
        }
        return res.Success;
    }

    public void AddOrUpdatePairedDevice(string deviceId, string deviceName, string ip, int port)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;
        var (host, resolvedPort) = ParseEndpoint(ip, port > 0 ? port : 42424);
        lock (_lock)
        {
            var peerMatch = _peers.FirstOrDefault(p =>
                p.DeviceId == deviceId ||
                (!string.IsNullOrEmpty(host) && string.Equals(p.Ip, host, StringComparison.OrdinalIgnoreCase)));
            bool isManual = peerMatch?.IsManual ?? false;
            if (isManual && peerMatch != null)
            {
                if (!string.IsNullOrEmpty(peerMatch.Ip)) host = peerMatch.Ip;
                if (peerMatch.SyncPort > 0) resolvedPort = peerMatch.SyncPort;
            }

            var existing = _pairedDevices.FirstOrDefault(p => p.DeviceId == deviceId);
            if (existing != null)
            {
                if (!string.IsNullOrEmpty(deviceName)) existing.DeviceName = deviceName;
                if (!string.IsNullOrEmpty(host)) existing.Ip = host;
                if (resolvedPort > 0) existing.SyncPort = resolvedPort;
                existing.IsManual = existing.IsManual || isManual;
                existing.IsOnline = true;
                existing.LastSyncAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            }
            else
            {
                _pairedDevices.Add(new PairedDeviceInfo
                {
                    DeviceId = deviceId,
                    DeviceName = !string.IsNullOrEmpty(deviceName) ? deviceName : $"局域网对端 ({host}:{resolvedPort})",
                    Ip = host,
                    SyncPort = resolvedPort > 0 ? resolvedPort : 42424,
                    PairedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                    LastSyncAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                    IsOnline = true,
                    IsManual = isManual
                });
            }
            SavePairedDevices();

            string spec = !string.IsNullOrEmpty(host)
                ? $"{deviceId}|{host}|{(resolvedPort > 0 ? resolvedPort : 42424)}|{deviceName}"
                : deviceId;
            _sync?.AddPairedDevice(spec);
        }
        RefreshPeers();
        PairedDevicesChanged?.Invoke();
    }

    public void RemovePairedDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;
        string endpointToRemove = string.Empty;
        string removeSpec = deviceId;
        lock (_lock)
        {
            var item = _pairedDevices.FirstOrDefault(p => p.DeviceId == deviceId);
            if (item != null)
            {
                endpointToRemove = item.Endpoint;
                if (!string.IsNullOrEmpty(item.Ip))
                {
                    removeSpec = $"{deviceId}|{item.Ip}|{(item.SyncPort > 0 ? item.SyncPort : 42424)}";
                }
                _pairedDevices.Remove(item);
                SavePairedDevices();
            }
        }
        _sync?.RemovePairedDevice(removeSpec);
        if (!string.IsNullOrEmpty(endpointToRemove))
        {
            _sync?.RemoveManualPeer(endpointToRemove);
        }
        _sync?.RemoveManualPeer(deviceId);
        SaveManualPeers();
        RefreshPeers();
        PairedDevicesChanged?.Invoke();
    }

    public void AddManualPeer(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return;
        _sync?.AddManualPeer(endpoint.Trim());
        SaveManualPeers();
        RefreshPeers();

        // 后台异步 TCP MSG_HEARTBEAT 探测返回 DiscoveryBeacon 后自动刷新真实设备名称
        if (_sync != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(500).ConfigureAwait(false);
                    if (!_disposed) RefreshPeers();
                    await Task.Delay(1200).ConfigureAwait(false);
                    if (!_disposed) RefreshPeers();
                }
                catch { }
            });
        }
    }

    public void RemoveManualPeer(string endpointOrDeviceId)
    {
        if (string.IsNullOrWhiteSpace(endpointOrDeviceId)) return;
        _sync?.RemoveManualPeer(endpointOrDeviceId.Trim());
        SaveManualPeers();
        RefreshPeers();
    }

    public IReadOnlyList<string> GetManualPeers()
    {
        return _sync?.GetManualPeers() ?? new List<string>();
    }

    public void RefreshHistory()
    {
        if (_core == null) return;

        // 预载最新前 100 条热点记录到内存，保持瞬时响应；剩余历史由 SQLite 持久化管理并按需分页加载
        var cached = _core.GetCachedItems();
        int totalCount = _core.GetHistoryCount();
        lock (_lock)
        {
            _items.Clear();
            foreach (var rec in cached)
            {
                _items.Add(MuClipItem.FromRecord(rec));
            }
            _totalHistoryCount = Math.Max(_items.Count, totalCount);
        }

        HistoryChanged?.Invoke();
    }

    public void RefreshPhrases()
    {
        if (_core == null) return;
        var records = _core.QueryPhrases();
        lock (_lock)
        {
            _phrases.Clear();
            foreach (var rec in records)
            {
                _phrases.Add(MuClipPhraseItem.FromRecord(rec));
            }
        }

        PhrasesChanged?.Invoke();
    }

    public List<MuClipPhraseItem> SearchPhrases(string query)
    {
        if (_core == null) return new List<MuClipPhraseItem>();
        if (string.IsNullOrWhiteSpace(query))
        {
            return Phrases.ToList();
        }
        var records = _core.SearchPhrases(query);
        return records.Select(MuClipPhraseItem.FromRecord).ToList();
    }

    public long AddPhrase(string content)
    {
        if (_core == null || string.IsNullOrWhiteSpace(content)) return -1;
        long id = _core.AddPhrase(content);
        if (id > 0)
        {
            RefreshPhrases();
        }
        return id;
    }

    public bool EditPhrase(long id, string newContent)
    {
        if (_core == null || string.IsNullOrWhiteSpace(newContent)) return false;
        bool ok = _core.EditPhrase(id, newContent);
        if (ok)
        {
            RefreshPhrases();
        }
        return ok;
    }

    public bool DeletePhrase(long id)
    {
        if (_core == null) return false;
        bool ok = _core.DeletePhrase(id);
        if (ok)
        {
            RefreshPhrases();
        }
        return ok;
    }

    public void UsePhrase(long id, string content)
    {
        if (_core == null) return;
        _core.UpdatePhraseUsed(id);
        CopyToSystemClipboard(content);
        RefreshPhrases();
    }

    public void ReorderPhrases(IReadOnlyList<long> orderedIds)
    {
        if (orderedIds == null || orderedIds.Count == 0) return;
        lock (_lock)
        {
            var dict = _phrases.ToDictionary(p => p.Id);
            var seen = new HashSet<long>();
            var newPhrases = new List<MuClipPhraseItem>();
            foreach (var id in orderedIds)
            {
                if (dict.TryGetValue(id, out var phrase))
                {
                    newPhrases.Add(phrase);
                    seen.Add(id);
                }
            }
            foreach (var phrase in _phrases)
            {
                if (!seen.Contains(phrase.Id))
                {
                    newPhrases.Add(phrase);
                }
            }
            _phrases.Clear();
            _phrases.AddRange(newPhrases);
        }
        PhrasesChanged?.Invoke();
    }

    public void RefreshPeers()
    {
        if (_sync == null) return;

        var peers = _sync.GetActivePeers();
        lock (_lock)
        {
            _peers.Clear();
            _peers.AddRange(peers);

            // 同步匹配更新已配对设备的实时在线状态与映射端点
            bool pairedStatusChanged = false;

            // 自动持久化同集群内通过密钥指纹匹配/Gossip自动互信的节点
            if (_pairedDevices.Count > 0)
            {
                foreach (var p in _peers)
                {
                    if (p.IsPaired &&
                        !string.IsNullOrWhiteSpace(p.DeviceId) &&
                        !p.DeviceId.StartsWith("manual-", StringComparison.OrdinalIgnoreCase) &&
                        _pairedDevices.All(d => d.DeviceId != p.DeviceId))
                    {
                        var nowStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                        var port = p.SyncPort > 0 ? p.SyncPort : 42424;
                        var name = !string.IsNullOrWhiteSpace(p.DeviceName) ? p.DeviceName : p.DeviceId;
                        _pairedDevices.Add(new PairedDeviceInfo
                        {
                            DeviceId = p.DeviceId,
                            DeviceName = name,
                            DeviceType = !string.IsNullOrWhiteSpace(p.DeviceType) ? p.DeviceType : "Windows",
                            Ip = p.Ip ?? string.Empty,
                            SyncPort = port,
                            PairedAt = nowStr,
                            LastSyncAt = nowStr,
                            IsOnline = p.IsOnline,
                            IsManual = p.IsManual
                        });
                        if (!string.IsNullOrWhiteSpace(p.Ip))
                        {
                            _sync.AddPairedDevice($"{p.DeviceId}|{p.Ip}|{port}|{name}");
                        }
                        pairedStatusChanged = true;
                    }
                }
            }

            foreach (var paired in _pairedDevices)
            {
                var match = _peers.FirstOrDefault(p =>
                    (!string.IsNullOrEmpty(paired.DeviceId) && p.DeviceId == paired.DeviceId))
                    ?? _peers.FirstOrDefault(p =>
                        !string.IsNullOrEmpty(paired.Ip) &&
                        string.Equals(p.Ip, paired.Ip, StringComparison.OrdinalIgnoreCase) &&
                        (paired.SyncPort <= 0 || p.SyncPort == paired.SyncPort));

                bool isOnline = match != null && match.IsOnline;
                if (match != null)
                {
                    bool isPlaceholder = match.DeviceId.StartsWith("manual-", StringComparison.OrdinalIgnoreCase)
                                         || match.DeviceName.StartsWith("映射节点", StringComparison.Ordinal);

                    bool endpointChanged = false;
                    if (!string.IsNullOrEmpty(match.Ip) && paired.Ip != match.Ip)
                    {
                        paired.Ip = match.Ip;
                        pairedStatusChanged = true;
                        endpointChanged = true;
                    }
                    if (match.SyncPort > 0 && paired.SyncPort != match.SyncPort)
                    {
                        paired.SyncPort = match.SyncPort;
                        pairedStatusChanged = true;
                        endpointChanged = true;
                    }
                    if (!isPlaceholder && !string.IsNullOrEmpty(match.DeviceName) && paired.DeviceName != match.DeviceName)
                    {
                        paired.DeviceName = match.DeviceName;
                        pairedStatusChanged = true;
                    }
                    if (!isPlaceholder && !string.IsNullOrEmpty(match.DeviceType) && match.DeviceType != "Manual" && paired.DeviceType != match.DeviceType)
                    {
                        paired.DeviceType = match.DeviceType;
                        pairedStatusChanged = true;
                    }
                    if (paired.IsManual != match.IsManual)
                    {
                        paired.IsManual = match.IsManual;
                        pairedStatusChanged = true;
                    }
                    if (endpointChanged && !string.IsNullOrEmpty(paired.DeviceId) && !string.IsNullOrEmpty(paired.Ip))
                    {
                        string spec = $"{paired.DeviceId}|{paired.Ip}|{(paired.SyncPort > 0 ? paired.SyncPort : 42424)}|{paired.DeviceName}";
                        _sync.AddPairedDevice(spec);
                    }
                }

                if (paired.IsOnline != isOnline)
                {
                    paired.IsOnline = isOnline;
                    pairedStatusChanged = true;
                }
            }

            if (pairedStatusChanged)
            {
                SavePairedDevices();
                PairedDevicesChanged?.Invoke();
            }
        }

        PeersChanged?.Invoke();
    }

    private void SaveManualPeers()
    {
        if (_sync == null || _core == null) return;
        try
        {
            var list = _sync.GetManualPeers();
            _core.SetConfig("manual_peers", string.Join('\n', list));
        }
        catch { }
    }

    private void LoadManualPeersIntoSync()
    {
        if (_sync == null || _core == null) return;
        try
        {
            string? raw = _core.GetConfig("manual_peers");
            if (!string.IsNullOrEmpty(raw))
            {
                var parts = raw.Split(new[] { '\r', '\n', ',', '[', ']', '"' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var ep in parts)
                {
                    string trimmed = ep.Trim();
                    if (!string.IsNullOrWhiteSpace(trimmed))
                    {
                        _sync.AddManualPeer(trimmed);
                    }
                }
            }
        }
        catch { }
    }

    private void SavePairedDevices()
    {
        try
        {
            using var ms = new MemoryStream();
            Span<byte> buf4 = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buf4, (uint)_pairedDevices.Count);
            ms.Write(buf4);

            static void WriteBinaryStr(MemoryStream stream, string s)
            {
                byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(s ?? string.Empty);
                Span<byte> lenBuf = stackalloc byte[4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(lenBuf, utf8.Length);
                stream.Write(lenBuf);
                if (utf8.Length > 0) stream.Write(utf8);
            }

            foreach (var d in _pairedDevices)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf4, d.SyncPort);
                ms.Write(buf4);
                WriteBinaryStr(ms, d.DeviceId);
                WriteBinaryStr(ms, d.DeviceName);
                WriteBinaryStr(ms, d.DeviceType);
                WriteBinaryStr(ms, d.Ip);
                WriteBinaryStr(ms, d.PairedAt);
                WriteBinaryStr(ms, d.LastSyncAt);
            }

            string payload = "BIN1:" + Convert.ToBase64String(ms.ToArray());
            _core?.SetConfig("paired_devices", payload);
        }
        catch
        {
            // 忽略持久化写入异常
        }
    }

    private void LoadPairedDevices()
    {
        try
        {
            // 纯数据库 secure_config 表读取，使用 BinarySpanReader 解码二进制帧
            string? raw = _core?.GetConfig("paired_devices");
            if (string.IsNullOrEmpty(raw) || !raw.StartsWith("BIN1:", StringComparison.Ordinal))
                return;

            byte[] bytes = Convert.FromBase64String(raw.Substring(5));
            var reader = new BinarySpanReader(bytes);
            uint count = reader.ReadUInt32();
            var list = new List<PairedDeviceInfo>((int)count);
            for (uint i = 0; i < count; i++)
            {
                int syncPort = reader.ReadInt32();
                string deviceId = reader.ReadString();
                string deviceName = reader.ReadString();
                string deviceType = reader.ReadString();
                string ip = reader.ReadString();
                string pairedAt = reader.ReadString();
                string lastSyncAt = reader.ReadString();
                list.Add(new PairedDeviceInfo
                {
                    DeviceId = deviceId,
                    DeviceName = deviceName,
                    DeviceType = string.IsNullOrEmpty(deviceType) ? "Windows" : deviceType,
                    Ip = ip,
                    SyncPort = syncPort > 0 ? syncPort : 42424,
                    PairedAt = pairedAt,
                    LastSyncAt = lastSyncAt,
                    IsOnline = false
                });
            }

            lock (_lock)
            {
                _pairedDevices.Clear();
                _pairedDevices.AddRange(list);
            }
        }
        catch
        {
            // 忽略反序列化异常
        }
    }

    public string FileSaveDirectory => _fileSaveDirectory;

    public void SetFileSaveDirectory(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        try
        {
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }
        catch { }
        _fileSaveDirectory = dir;
        _sync?.SetFileSaveDirectory(dir);
    }

    public string? SendFile(string peerEndpoint, string filePath)
    {
        if (_sync == null) return null;
        return _sync.SendFile(peerEndpoint, filePath);
    }

    public bool CancelFileTransfer(string transferId)
    {
        if (_sync == null) return false;
        return _sync.CancelFileTransfer(transferId);
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
                _sync.Dispose();
                _sync = null;
            }
            _core?.Dispose();
            _core = null;
        }
    }
}
