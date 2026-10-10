namespace MuClip.Models;

public class PeerDeviceInfo
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string DeviceType { get; set; } = string.Empty;
    public string Ip { get; set; } = string.Empty;
    public int SyncPort { get; set; }
    public long LastSeenMs { get; set; }
    public bool IsOnline { get; set; }
    public string Fingerprint { get; set; } = string.Empty;
    public bool IsPaired { get; set; }
    public bool IsManual { get; set; }

    public string Endpoint => SyncPort > 0 && !Ip.Contains(':') ? $"{Ip}:{SyncPort}" : Ip;
}
