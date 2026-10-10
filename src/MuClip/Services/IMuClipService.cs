using System;
using System.Collections.Generic;
using MuClip.Models;

namespace MuClip.Services;

public interface IMuClipService : IDisposable
{
    bool IsRunning { get; }
    bool IsListening { get; }
    bool IsSyncEnabled { get; }
    ushort SyncPort { get; }
    string PairingPin { get; }
    bool AutoDiscoveryEnabled { get; set; }

    IReadOnlyList<MuClipItem> HistoryItems { get; }
    int TotalHistoryCount { get; }
    IReadOnlyList<MuClipPhraseItem> Phrases { get; }
    IReadOnlyList<PeerDeviceInfo> ActivePeers { get; }
    IReadOnlyList<PairedDeviceInfo> PairedDevices { get; }

    event Action? HistoryChanged;
    event Action? PhrasesChanged;
    event Action? PeersChanged;
    event Action? PairedDevicesChanged;
    event Action<string>? ClipboardReceived;
    event Action<string, string, string>? PairRequestReceived;
    event Action<string, string, string, string>? IncomingPairRequested; // (requestId, deviceId, deviceName, peerIp)
    event Action<string, string>? PairEstablished; // (deviceId, deviceName)
    event Action<string, string>? RemotePeerUnpaired; // (deviceId, deviceName)
    event Action? QuickPanelHotkeyTriggered;
    event Action<FileProgressEvent>? FileProgress;
    event Action<FileCompletedEvent>? FileCompleted;
    event Action<FileOfferEvent>? FileOfferReceived;

    void Initialize(string? dbPath = null, bool startSync = true, ushort syncPort = 42424, string? pairingPin = null, bool startListener = true, string? customDeviceId = null, string? customDeviceName = null);
    bool RegisterQuickPanelHotkey(uint modifiers = 0x0002, uint vk = 0xC0);
    void AddRecord(string content, bool broadcast = true);
    void CopyToSystemClipboard(string text);
    void DeleteRecord(long id);
    void ClearRecords();
    List<MuClipItem> QueryHistoryPage(int offset, int limit = 100);
    List<MuClipItem> Search(string query, int limit = 500);
    int Broadcast(string content);
    void UpdatePairingPin(string pin);
    Task<(bool Success, string DeviceId, string DeviceName, string Message)> RequestPairAsync(string peerIp, int peerPort, string pin, uint timeoutSecs = 60);
    int RespondPair(string requestId, string pin, bool accept);
    bool SendPairRequest(string peerIp, int peerPort, string pin, string? deviceId = null, string? deviceName = null);
    void AddOrUpdatePairedDevice(string deviceId, string deviceName, string ip, int port);
    void RemovePairedDevice(string deviceId);
    void AddManualPeer(string endpoint);
    void RemoveManualPeer(string endpointOrDeviceId);
    IReadOnlyList<string> GetManualPeers();
    void RefreshHistory();
    void RefreshPhrases();
    List<MuClipPhraseItem> SearchPhrases(string query);
    long AddPhrase(string content);
    bool EditPhrase(long id, string newContent);
    bool DeletePhrase(long id);
    void UsePhrase(long id, string content);
    void ReorderPhrases(IReadOnlyList<long> orderedIds);
    void RefreshPeers();
    void TriggerDiscoveryScan();
    int TestPeerConnection(string peerIp, int peerPort);
    string FileSaveDirectory { get; }
    void SetFileSaveDirectory(string dir);
    string? SendFile(string peerEndpoint, string filePath);
    bool CancelFileTransfer(string transferId);
}
