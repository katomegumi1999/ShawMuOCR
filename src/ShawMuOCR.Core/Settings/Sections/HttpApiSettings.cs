namespace ShawMuOCR.Core.Settings;

/// <summary>
/// 内置 HTTP API 服务分块配置
/// </summary>
public sealed class HttpApiSettings
{
    public int Port { get; set; } = 8080;
    public bool AllowRemote { get; set; } = false;

    /// <summary>
    /// 局域网访问令牌（Bearer）。非空时 HTTP 接口强制鉴权；
    /// 开启 <see cref="AllowRemote"/> 时若无令牌会自动生成，服务端拒绝无令牌的远程暴露。
    /// </summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>
    /// 显式允许局域网无令牌访问（默认关闭）。
    /// 用于"临时分享给同网段同事"：开启后同网段任何设备都可调用本机 OCR 并占用 CPU，
    /// 但接口不读取任何本机文件。仅当 <see cref="AccessToken"/> 为空时才有意义。
    /// </summary>
    public bool AllowAnonymousRemote { get; set; } = false;

    public int MaxCores { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);
    public int GpuStreams { get; set; } = 1;
    public int CpuLimitPercent { get; set; } = 50;

    /// <summary>
    /// 推理设备调度模式 ("Auto", "GPU", "CPU")
    /// </summary>
    public string SchedulerMode { get; set; } = "Auto";

    public HttpApiSettings Clone() => new()
    {
        Port = Port,
        AllowRemote = AllowRemote,
        AccessToken = AccessToken,
        AllowAnonymousRemote = AllowAnonymousRemote,
        MaxCores = MaxCores,
        GpuStreams = GpuStreams,
        CpuLimitPercent = CpuLimitPercent,
        SchedulerMode = SchedulerMode
    };
}
