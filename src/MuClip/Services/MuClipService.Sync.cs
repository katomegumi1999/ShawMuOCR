using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MuClip.Interop;
using MuClip.Models;

namespace MuClip.Services;

public partial class MuClipService
{
    public bool IsSyncEnabled => _sync != null;
    public ushort SyncPort => _sync?.BoundPort ?? 42424;

    private string _pairingPin = string.Empty;

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

    public event Action? PeersChanged;
    public event Action? PairedDevicesChanged;
    public event Action<string>? ClipboardReceived;
    public event Action<string, string, string>? PairRequestReceived;
    public event Action<string, string, string, string>? IncomingPairRequested;
    public event Action<string, string>? PairEstablished;
    public event Action<string, string>? RemotePeerUnpaired;

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
                    _core.SetConfig("pairing_pin", "");
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
            PairingPin = "已信任 (长期密钥)";
            AddOrUpdatePairedDevice(res.ResponderId, res.ResponderName, host, port, res.LongTermKey);
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
            PairingPin = "已信任 (长期密钥)";
            AddOrUpdatePairedDevice(res.ResponderId, res.ResponderName, host, port, res.LongTermKey);
        }
        return res.Success;
    }

    public void AddOrUpdatePairedDevice(string deviceId, string deviceName, string ip, int port)
        => AddOrUpdatePairedDevice(deviceId, deviceName, ip, port, null);

    public void AddOrUpdatePairedDevice(string deviceId, string deviceName, string ip, int port, string? peerKey)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;
        var (host, resolvedPort) = ParseEndpoint(ip, port > 0 ? port : 42424);
        string cleanPeerKey = IsValidHexKey64(peerKey) ? peerKey!.Trim() : string.Empty;
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
            if (string.IsNullOrEmpty(cleanPeerKey) && peerMatch != null && IsValidHexKey64(peerMatch.Fingerprint))
            {
                cleanPeerKey = peerMatch.Fingerprint.Trim();
            }

            string resolvedType = peerMatch != null && !string.IsNullOrWhiteSpace(peerMatch.DeviceType) && peerMatch.DeviceType != "Manual"
                ? peerMatch.DeviceType
                : "Windows";

            var existing = _pairedDevices.FirstOrDefault(p => p.DeviceId == deviceId);
            if (existing != null)
            {
                if (!string.IsNullOrEmpty(deviceName)) existing.DeviceName = deviceName;
                if (!string.IsNullOrEmpty(host)) existing.Ip = host;
                if (resolvedPort > 0) existing.SyncPort = resolvedPort;
                if (!string.IsNullOrEmpty(cleanPeerKey)) existing.PeerKey = cleanPeerKey;
                else cleanPeerKey = existing.PeerKey;
                if (!string.IsNullOrEmpty(existing.DeviceType)) resolvedType = existing.DeviceType;
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
                    DeviceType = resolvedType,
                    Ip = host,
                    SyncPort = resolvedPort > 0 ? resolvedPort : 42424,
                    PairedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                    LastSyncAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                    PeerKey = cleanPeerKey,
                    IsOnline = true,
                    IsManual = isManual
                });
            }
            SavePairedDevices();

            string spec = BuildPairedDeviceSpec(deviceId, host, resolvedPort, deviceName, resolvedType, cleanPeerKey);
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
            var (host, port) = ParseEndpoint(endpoint, 42424);
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(80).ConfigureAwait(false);
                    var sync = _sync;
                    if (sync != null && !_disposed)
                    {
                        sync.TestPeer($"{host}:{port}");
                        RefreshPeers();
                    }
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
        if (_sync == null) return Array.Empty<string>();
        return _sync.GetManualPeers();
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

            // 自动持久化同集群内通过受信任介绍人 (Trusted Introducer Gossip) 自动互信的节点及其对端密钥
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
                        var devType = !string.IsNullOrWhiteSpace(p.DeviceType) ? p.DeviceType : "Windows";
                        var peerKey = IsValidHexKey64(p.Fingerprint) ? p.Fingerprint.Trim() : string.Empty;
                        _pairedDevices.Add(new PairedDeviceInfo
                        {
                            DeviceId = p.DeviceId,
                            DeviceName = name,
                            DeviceType = devType,
                            Ip = p.Ip ?? string.Empty,
                            SyncPort = port,
                            PairedAt = nowStr,
                            LastSyncAt = nowStr,
                            PeerKey = peerKey,
                            IsOnline = p.IsOnline,
                            IsManual = p.IsManual
                        });
                        if (!string.IsNullOrWhiteSpace(p.Ip) || !string.IsNullOrEmpty(peerKey))
                        {
                            _sync.AddPairedDevice(BuildPairedDeviceSpec(p.DeviceId, p.Ip, port, name, devType, peerKey));
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
                    if (IsValidHexKey64(match.Fingerprint) && paired.PeerKey != match.Fingerprint.Trim())
                    {
                        paired.PeerKey = match.Fingerprint.Trim();
                        pairedStatusChanged = true;
                        endpointChanged = true;
                    }
                    if (paired.IsManual != match.IsManual)
                    {
                        paired.IsManual = match.IsManual;
                        pairedStatusChanged = true;
                    }
                    if (endpointChanged && !string.IsNullOrEmpty(paired.DeviceId) && (!string.IsNullOrEmpty(paired.Ip) || !string.IsNullOrEmpty(paired.PeerKey)))
                    {
                        string spec = BuildPairedDeviceSpec(paired.DeviceId, paired.Ip, paired.SyncPort, paired.DeviceName, paired.DeviceType, paired.PeerKey);
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
                WriteBinaryStr(ms, EncryptSecret(d.PeerKey));
            }

            string payload = "BIN2:" + Convert.ToBase64String(ms.ToArray());
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
            // 纯数据库 secure_config 表读取，使用 BinarySpanReader 解码二进制帧（兼容 BIN1 与带加密对端密钥的 BIN2）
            string? raw = _core?.GetConfig("paired_devices");
            if (string.IsNullOrEmpty(raw)) return;
            bool isBin2 = raw.StartsWith("BIN2:", StringComparison.Ordinal);
            bool isBin1 = raw.StartsWith("BIN1:", StringComparison.Ordinal);
            if (!isBin1 && !isBin2) return;

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
                string peerKey = string.Empty;
                if (isBin2)
                {
                    string encKey = reader.ReadString();
                    string? decKey = DecryptSecret(encKey);
                    if (IsValidHexKey64(decKey))
                    {
                        peerKey = decKey!.Trim();
                    }
                }
                list.Add(new PairedDeviceInfo
                {
                    DeviceId = deviceId,
                    DeviceName = deviceName,
                    DeviceType = string.IsNullOrEmpty(deviceType) ? "Windows" : deviceType,
                    Ip = ip,
                    SyncPort = syncPort > 0 ? syncPort : 42424,
                    PairedAt = pairedAt,
                    LastSyncAt = lastSyncAt,
                    PeerKey = peerKey,
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

    private void OnPairEstablished(object? sender, PairEstablishedEventArgs e)
    {
        if (string.Equals(e.DeviceId, _localDeviceId, StringComparison.OrdinalIgnoreCase)) return;

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

        PairingPin = "已信任 (长期密钥)";
        AddOrUpdatePairedDevice(e.DeviceId, e.DeviceName, parsedHost, parsedPort, e.Pin);
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

    private static string BuildPairedDeviceSpec(string deviceId, string? ip, int port, string? name, string? type, string? peerKey)
    {
        int p = port > 0 ? port : 42424;
        string safeIp = (ip ?? string.Empty).Trim();
        string safeName = (name ?? string.Empty).Replace('|', ' ').Trim();
        string safeType = string.IsNullOrWhiteSpace(type) ? "Windows" : type.Replace('|', ' ').Trim();
        string safeKey = IsValidHexKey64(peerKey) ? peerKey!.Trim() : string.Empty;
        if (string.IsNullOrEmpty(safeIp) && string.IsNullOrEmpty(safeKey))
        {
            return deviceId;
        }
        return $"{deviceId}|{safeIp}|{p}|{safeName}|{safeType}|{safeKey}";
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

    public bool ChangeSyncPort(ushort newPort)
    {
        if (newPort == 0) return false;
        if (_core == null) return false;

        try
        {
            _core.SetConfig("custom_sync_port", newPort.ToString());
        }
        catch { }

        if (_sync == null) return true;

        try
        {
            string deviceId = _localDeviceId;
            string deviceName = _localDeviceName;
            string? ltk = _core.GetConfig("long_term_key");
            string? decrypted = !string.IsNullOrEmpty(ltk) ? DecryptSecret(ltk) : null;
            string activeSessionKey = (!string.IsNullOrEmpty(decrypted) && IsValidHexKey64(decrypted))
                ? decrypted
                : (!string.IsNullOrEmpty(PairingPin) && PairingPin != "已信任 (长期密钥)" ? PairingPin : GenerateRandomLongTermKeyHex());

            // 停用并释放当前同步
            _sync.ClipboardReceived -= OnRemoteClipboardReceived;
            _sync.PairRequestReceived -= OnRemotePairRequestReceived;
            _sync.IncomingPairRequestReceived -= OnIncomingPairRequestReceived;
            _sync.PairEstablished -= OnPairEstablished;
            _sync.Dispose();

            _sync = new MuClipSync(deviceId, deviceName, "Windows", newPort, 42110, activeSessionKey);
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

            if (!string.IsNullOrEmpty(_fileSaveDirectory))
            {
                _sync.SetFileSaveDirectory(_fileSaveDirectory);
            }

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
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MuClip] 切换端口异常: {ex.Message}");
            return false;
        }
    }
}
