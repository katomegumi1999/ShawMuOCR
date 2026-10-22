using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MuClip.Models;

namespace MuClip.Services;

/// <summary>
/// 局域网剪贴板同步与对端管理契约
/// 职责：局域网对端广播发现、Diffie-Hellman + PIN 配对、双向解除配对、手动节点映射
/// </summary>
public interface IClipboardSyncService
{
    bool IsSyncEnabled { get; }
    ushort SyncPort { get; }
    string PairingPin { get; }
    bool AutoDiscoveryEnabled { get; set; }

    IReadOnlyList<PeerDeviceInfo> ActivePeers { get; }
    IReadOnlyList<PairedDeviceInfo> PairedDevices { get; }

    event Action? PeersChanged;
    event Action? PairedDevicesChanged;
    event Action<string>? ClipboardReceived;
    event Action<string, string, string>? PairRequestReceived;
    event Action<string, string, string, string>? IncomingPairRequested;
    event Action<string, string>? PairEstablished;
    event Action<string, string>? RemotePeerUnpaired;

    int Broadcast(string content);
    void UpdatePairingPin(string pin);
    Task<(bool Success, string DeviceId, string DeviceName, string Message)> RequestPairAsync(string peerIp, int peerPort, string pin, uint timeoutSecs = 60);
    int RespondPair(string requestId, string pin, bool accept);
    bool SendPairRequest(string peerIp, int peerPort, string pin, string? deviceId = null, string? deviceName = null);
    void AddOrUpdatePairedDevice(string deviceId, string deviceName, string ip, int port);
    void AddOrUpdatePairedDevice(string deviceId, string deviceName, string ip, int port, string? peerKey);
    void RemovePairedDevice(string deviceId);
    void AddManualPeer(string endpoint);
    void RemoveManualPeer(string endpointOrDeviceId);
    IReadOnlyList<string> GetManualPeers();
    void RefreshPeers();
    void TriggerDiscoveryScan();
    int TestPeerConnection(string peerIp, int peerPort);
    bool ChangeSyncPort(ushort newPort);
}
