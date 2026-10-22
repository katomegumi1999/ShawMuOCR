using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MuClip.Models;

namespace MuClip.Services;

/// <summary>
/// 小沐剪贴板综合统一服务外观 (Facade)
/// 整合历史记录、常用语规则、局域网同步与文件传输 4 大高内聚子服务，
/// 同时保留统一对外门面方法以保障向上向下的零成本兼容性。
/// </summary>
public interface IMuClipService : IClipboardHistoryService, IClipboardPhraseService, IClipboardSyncService, IClipboardFileTransferService, IDisposable
{
    // === 核心状态与全局生命周期 ===
    bool IsRunning { get; }
    bool IsListening { get; }
    string DatabasePath { get; }
    string DataDirectory { get; }

    event Action? QuickPanelHotkeyTriggered;

    void Initialize(string? dbPath = null, bool startSync = true, ushort syncPort = 42424, string? pairingPin = null, bool startListener = true, string? customDeviceId = null, string? customDeviceName = null);
    bool RegisterQuickPanelHotkey(uint modifiers = 0x0002, uint vk = 0xC0);

    // === 细粒度子领域服务访问入口 ===
    IClipboardHistoryService History { get; }
    IClipboardPhraseService PhrasesService { get; }
    IClipboardSyncService SyncService { get; }
    IClipboardFileTransferService FileTransferService { get; }
}
