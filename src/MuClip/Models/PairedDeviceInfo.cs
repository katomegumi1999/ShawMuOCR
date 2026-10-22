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

    /// <summary>
    /// 是否正在进行网络延迟测试
    /// </summary>
    [ObservableProperty]
    private bool _isTestingLatency;

    /// <summary>
    /// 延迟测试结果反馈（例如 "✓ 延迟: 12 ms"、"⏳ 测速中..."、"✕ 连接超时 (离线)"）
    /// </summary>
    [ObservableProperty]
    private string _latencyFeedback = string.Empty;

    /// <summary>
    /// 延迟测试结果是否成功（成功为 true，失败为 false，测速中为 null）
    /// </summary>
    [ObservableProperty]
    private bool? _latencySuccess;

    public string StatusText => IsOnline ? "🟢 实时同步中" : "⚪ 离线等待";

    public string Endpoint => $"{Ip}:{SyncPort}";
}
