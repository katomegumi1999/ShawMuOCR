using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MuClip.Models;

/// <summary>
/// 局域网已配对设备持久化信息
/// </summary>
public partial class PairedDeviceInfo : ObservableObject
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string DeviceType { get; set; } = "Windows";
    public string Ip { get; set; } = string.Empty;
    public int SyncPort { get; set; } = 42424;
    public string PairedAt { get; set; } = string.Empty;
    public string LastSyncAt { get; set; } = string.Empty;
    public string PeerKey { get; set; } = string.Empty;

    /// <summary>
    /// 运行时动态在线状态（由心跳包与 TCP 探活判定）
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isOnline;

    /// <summary>
    /// 是否为手动添加的端口映射/穿透节点
    /// </summary>
    [ObservableProperty]
    private bool _isManual;

    public string StatusText => IsOnline ? "🟢 实时同步中" : "⚪ 离线等待";

    public string Endpoint => $"{Ip}:{SyncPort}";
}
