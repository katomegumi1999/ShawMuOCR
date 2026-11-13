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

    /// <summary>
    /// 是否开启访问令牌鉴权。关闭时所有调用免令牌放行。
    /// </summary>
    public bool EnableTokenAuth { get; set; } = false;

    /// <summary>
    /// 是否允许本机免令牌直连（开启后即使配置了令牌，来自 127.0.0.1 的本机请求也免传令牌，仅局域网外部设备强制鉴权）。
    /// </summary>
    public bool AllowLocalWithoutToken { get; set; } = true;

    /// <summary>
    /// 单个请求体的最大大小上限（MB）。
    /// 默认 0 表示无限制（不限制请求文件大小）；大于 0 时限制为对应 MB 数。
    /// </summary>
    public int MaxRequestBodyMb { get; set; } = 0;

    public HttpApiSettings Clone() => new()
    {
        Port = Port,
        AllowRemote = AllowRemote,
        AccessToken = AccessToken,
        AllowAnonymousRemote = AllowAnonymousRemote,
        EnableTokenAuth = EnableTokenAuth,
        AllowLocalWithoutToken = AllowLocalWithoutToken,
        MaxCores = MaxCores,
        GpuStreams = GpuStreams,
        CpuLimitPercent = CpuLimitPercent,
        SchedulerMode = SchedulerMode,
        MaxRequestBodyMb = MaxRequestBodyMb
    };
}
