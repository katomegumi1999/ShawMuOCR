using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MuClip.Models;

public class FileProgressEvent
{
    public string TransferId { get; set; } = string.Empty;
    public ulong TransferredBytes { get; set; }
    public ulong TotalBytes { get; set; }
    public ulong SpeedBps { get; set; }
    public bool IsSend { get; set; }
    public double ProgressPercent => TotalBytes > 0 ? (double)TransferredBytes / TotalBytes * 100.0 : 0.0;
    public double SpeedMBs => (double)SpeedBps / (1024.0 * 1024.0);
}

public class FileCompletedEvent
{
    public string TransferId { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public bool IsSend { get; set; }
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
}

public class FileOfferEvent
{
    public string TransferId { get; set; } = string.Empty;
    public string FromDeviceId { get; set; } = string.Empty;
    public string FromDeviceName { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public ulong FileSize { get; set; }
}

public enum TransferStatus
{
    Pending,
    Transferring,
    Completed,
    Failed,
    Canceled
}

public partial class TransferTargetDeviceItem : ObservableObject
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public bool IsPaired { get; set; }
    public bool IsOnline { get; set; }

    [ObservableProperty]
    private bool _isSelected;

    public string DisplayName => IsPaired
        ? $"💻 {DeviceName} ({Endpoint}) [已配对]"
        : $"📡 {DeviceName} ({Endpoint}) [附近新设备]";

    public override string ToString() => DisplayName;
}

public partial class FileTransferItem : ObservableObject
{
    [ObservableProperty]
    private string _transferId = string.Empty;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private bool _isSend;

    [ObservableProperty]
    private string _peerName = string.Empty;

    [ObservableProperty]
    private string _peerEndpoint = string.Empty;

    [ObservableProperty]
    private ulong _transferredBytes;

    [ObservableProperty]
    private ulong _totalBytes;

    [ObservableProperty]
    private ulong _speedBps;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(DirectionBadge))]
    [NotifyPropertyChangedFor(nameof(DirectionColor))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    [NotifyPropertyChangedFor(nameof(CanOpenFile))]
    [NotifyPropertyChangedFor(nameof(CanOpenFolder))]
    private TransferStatus _status = TransferStatus.Transferring;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private DateTime _startTime = DateTime.Now;

    public double SpeedMBs => (double)SpeedBps / (1024.0 * 1024.0);
    public string FormattedSpeed => $"{SpeedMBs:F2} MB/s";
    public string FormattedSize => $"{FormatFileSize(TransferredBytes)} / {FormatFileSize(TotalBytes)}";
    public string FormattedPercent => $"{ProgressPercent:F1}%";
    public string DirectionBadge => Status == TransferStatus.Pending ? "待发送" : (IsSend ? "发送 ↑" : "接收 ↓");
    public string DirectionColor => Status == TransferStatus.Pending ? "#F59E0B" : (IsSend ? "#3B82F6" : "#10B981");

    /// <summary>
    /// 预计剩余时间（例如 "⏱ 剩余约 15 秒"、"⏱ 剩余约 2 分 30 秒"）
    /// </summary>
    public string FormattedRemainingTime
    {
        get
        {
            if (Status != TransferStatus.Transferring)
                return string.Empty;

            if (TotalBytes <= TransferredBytes)
                return string.Empty;

            if (SpeedBps == 0)
                return "⏱ 计算剩余时间...";

            ulong remainingBytes = TotalBytes - TransferredBytes;
            double seconds = (double)remainingBytes / SpeedBps;

            if (seconds < 1)
                return "⏱ 即将完成";
            if (seconds < 60)
                return $"⏱ 剩余约 {(int)Math.Ceiling(seconds)} 秒";
            if (seconds < 3600)
            {
                int mins = (int)(seconds / 60);
                int secs = (int)(seconds % 60);
                return secs > 0 ? $"⏱ 剩余约 {mins} 分 {secs} 秒" : $"⏱ 剩余约 {mins} 分钟";
            }
            else
            {
                int hours = (int)(seconds / 3600);
                int mins = (int)((seconds % 3600) / 60);
                return mins > 0 ? $"⏱ 剩余约 {hours} 小时 {mins} 分" : $"⏱ 剩余约 {hours} 小时";
            }
        }
    }

    public string StatusText => Status switch
    {
        TransferStatus.Pending => "待发送",
        TransferStatus.Transferring => "传输中...",
        TransferStatus.Completed => "✓ 已完成",
        TransferStatus.Failed => $"✕ 失败: {ErrorMessage}",
        TransferStatus.Canceled => "已取消",
        _ => ""
    };

    public bool CanCancel => Status == TransferStatus.Transferring;
    public bool CanRemove => Status != TransferStatus.Transferring;
    public bool CanOpenFile => Status == TransferStatus.Completed && !string.IsNullOrEmpty(FilePath) && File.Exists(FilePath);
    public bool CanOpenFolder => Status == TransferStatus.Completed && !string.IsNullOrEmpty(FilePath);

    public static string FormatFileSize(ulong bytes)
    {
        if (bytes >= 1024UL * 1024 * 1024)
            return $"{(double)bytes / (1024.0 * 1024 * 1024):F2} GB";
        if (bytes >= 1024UL * 1024)
            return $"{(double)bytes / (1024.0 * 1024):F2} MB";
        if (bytes >= 1024)
            return $"{(double)bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }

    public void UpdateProgress(ulong transferred, ulong total, ulong speed)
    {
        TransferredBytes = transferred;
        TotalBytes = total;
        SpeedBps = speed;
        ProgressPercent = total > 0 ? (double)transferred / total * 100.0 : 0.0;
        OnPropertyChanged(nameof(FormattedSpeed));
        OnPropertyChanged(nameof(FormattedSize));
        OnPropertyChanged(nameof(FormattedPercent));
        OnPropertyChanged(nameof(FormattedRemainingTime));
        OnPropertyChanged(nameof(SpeedMBs));
    }

    public void MarkCompleted(string filePath, bool success, string error)
    {
        if (!string.IsNullOrEmpty(filePath)) FilePath = filePath;
        if (success)
        {
            Status = TransferStatus.Completed;
            ProgressPercent = 100.0;
            TransferredBytes = TotalBytes;
        }
        else
        {
            Status = error.Contains("取消") || error.Contains("cancel", StringComparison.OrdinalIgnoreCase)
                ? TransferStatus.Canceled
                : TransferStatus.Failed;
            ErrorMessage = error;
        }
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanOpenFile));
        OnPropertyChanged(nameof(CanOpenFolder));
        OnPropertyChanged(nameof(FormattedPercent));
        OnPropertyChanged(nameof(FormattedSize));
        OnPropertyChanged(nameof(FormattedRemainingTime));
    }
}
