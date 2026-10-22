using System;
using System.Collections.ObjectModel;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShawMuOCR.Core.Clipboard;
using ShawMuOCR.Core.Logging;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.Services.Http;

namespace ShawMuOCR.ViewModels;

/// <summary>
/// HTTP 调用页面 ViewModel，管理内置 HTTP OCR 服务端生命周期、实时调用审计与示例代码
/// </summary>
public partial class HttpApiViewModel : ViewModelBase, IDisposable
{
    private readonly HttpOcrServer _server;
    private readonly ISettingsService _settingsService;
    private readonly IClipboardService? _clipboardService;
    private readonly ILoggerService? _logger;

    private double _totalElapsedAccumulator;
    private bool _isLoadingSettings;
    private bool _isNormalizingToken;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EndpointUrl))]
    [NotifyPropertyChangedFor(nameof(LanEndpointUrl))]
    [NotifyPropertyChangedFor(nameof(LanAccessHint))]
    [NotifyPropertyChangedFor(nameof(CurlExample))]
    [NotifyPropertyChangedFor(nameof(PythonExample))]
    [NotifyPropertyChangedFor(nameof(CsharpExample))]
    private int _port = 8080;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EndpointUrl))]
    [NotifyPropertyChangedFor(nameof(LanEndpointUrl))]
    [NotifyPropertyChangedFor(nameof(LanAccessHint))]
    [NotifyPropertyChangedFor(nameof(CurlExample))]
    [NotifyPropertyChangedFor(nameof(PythonExample))]
    [NotifyPropertyChangedFor(nameof(CsharpExample))]
    [NotifyPropertyChangedFor(nameof(AuthHeaderHint))]
    private bool _allowRemote = false;

    /// <summary>
    /// 访问令牌（Bearer）。非空时接口强制鉴权；开启局域网访问时自动生成。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurlExample))]
    [NotifyPropertyChangedFor(nameof(PythonExample))]
    [NotifyPropertyChangedFor(nameof(CsharpExample))]
    [NotifyPropertyChangedFor(nameof(AuthHeaderHint))]
    private string _accessToken = "";

    /// <summary>
    /// 显式允许局域网无令牌访问（与令牌互斥：勾选后令牌会被清空）
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTokenRowVisible))]
    [NotifyPropertyChangedFor(nameof(CurlExample))]
    [NotifyPropertyChangedFor(nameof(PythonExample))]
    [NotifyPropertyChangedFor(nameof(CsharpExample))]
    [NotifyPropertyChangedFor(nameof(AuthHeaderHint))]
    private bool _allowAnonymousRemote = false;

    /// <summary>
    /// 令牌输入行是否可见（无令牌模式下列表隐藏，避免用户误以为仍在鉴权）
    /// </summary>
    public bool IsTokenRowVisible => !AllowAnonymousRemote;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConcurrencyLabel))]
    [NotifyPropertyChangedFor(nameof(ConcurrencyValueText))]
    private int _maxCores = Math.Max(1, Environment.ProcessorCount / 2);

    [ObservableProperty]
    private bool _isConcurrencyWarningOpen;

    [ObservableProperty]
    private int _pendingMaxCores = 1;

    public string ConcurrencyWarningTitle => "⚠️ GPU 并发流上限提升提示";
    public string CancelConcurrencyButtonText => "保持 1 路 (推荐)";
    public string ConfirmConcurrencyButtonText => $"确定提升至 {PendingMaxCores} 路";

    public string ConcurrencyWarningMessage =>
        $"您正在尝试将 GPU 并发流上限设置为 {PendingMaxCores} 路。\n\n" +
        "• 深度学习推理（PaddleOCR）在单张 GPU 显卡中开启多路并发流时，显存（VRAM）占用量将成倍增长。\n" +
        "• 若显存不足或算力争抢，可能导致显存溢出 (CUDA Out of Memory)、显卡高负荷发热降频甚至显卡驱动重置崩溃。\n" +
        "• 对于绝大多数消费级显卡，保持 1 路并发已具备极佳的推理吞吐，建议仅在配备高端大显存显卡且需要大并发接口调用时再行提升。\n\n" +
        $"确定要继续提升至 {PendingMaxCores} 路并发吗？";

    private bool _concurrencyWarningConfirmed;
    private bool _isAdjustingCoresInternally;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsageLimitValueText))]
    private int _cpuLimitPercent = 50;

    [ObservableProperty]
    private string _schedulerTitle = "⚡ CPU 调度与并发控制";

    [ObservableProperty]
    private string _schedulerSummaryText = $"本机共 {Environment.ProcessorCount} 逻辑核 (默认 {Math.Max(1, Environment.ProcessorCount / 2)} 核 / 上限 {Environment.ProcessorCount} 核)";

    [ObservableProperty]
    private string _deviceBadgeText = "⚡ CPU 调度";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConcurrencyLabel))]
    [NotifyPropertyChangedFor(nameof(ConcurrencyValueText))]
    [NotifyPropertyChangedFor(nameof(UsageLimitLabel))]
    [NotifyPropertyChangedFor(nameof(ConcurrencyWarningTitle))]
    [NotifyPropertyChangedFor(nameof(CancelConcurrencyButtonText))]
    [NotifyPropertyChangedFor(nameof(ConfirmConcurrencyButtonText))]
    [NotifyPropertyChangedFor(nameof(ConcurrencyWarningMessage))]
    [NotifyPropertyChangedFor(nameof(MaxAvailableCores))]
    private bool _isGpuAccelerated;

    public string ConcurrencyLabel => IsGpuAccelerated ? "GPU 并发流上限:" : "最大并发核心数上限:";
    public string ConcurrencyValueText => IsGpuAccelerated ? $"{MaxCores} 路并发" : $"{MaxCores} 核";
    public string UsageLimitLabel => IsGpuAccelerated ? "GPU 算力占空比上限:" : "CPU 利用率上限 (占空比限流):";
    public string UsageLimitValueText => $"{CpuLimitPercent}%";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBadgeText))]
    [NotifyPropertyChangedFor(nameof(StatusBadgeColor))]
    [NotifyPropertyChangedFor(nameof(ToggleServerButtonText))]
    [NotifyPropertyChangedFor(nameof(IsSettingsEnabled))]
    private bool _isRunning;

    public string StatusBadgeText => IsRunning ? "运行中" : "已停止";
    public string StatusBadgeColor => IsRunning ? "#107C41" : "#8A8886";
    public string ToggleServerButtonText => IsRunning ? "停止服务" : "启动服务";
    public bool IsSettingsEnabled => !IsRunning;

    public string EndpointUrl => $"http://{(AllowRemote ? "0.0.0.0" : "127.0.0.1")}:{Port}/ocr";

    /// <summary>
    /// 局域网内其他设备实际可用的调用地址（用本机内网 IP，而不是 0.0.0.0）
    /// </summary>
    public string LanEndpointUrl => $"http://{LanIpv4}:{Port}/ocr";

    public string LanAccessHint => $"局域网其他人用这个地址：{LanEndpointUrl}";

    /// <summary>
    /// 本机内网 IPv4（进程内缓存；网卡变动需重启程序，属可接受取舍）
    /// </summary>
    private static readonly Lazy<string> s_lanIpv4 = new(DetectLanIpv4, LazyThreadSafetyMode.ExecutionAndPublication);

    private static string LanIpv4 => s_lanIpv4.Value;

    private static string DetectLanIpv4()
    {
        try
        {
            // 优先选择带默认网关的网卡（真正的对外网卡），避免误取 VPN / 虚拟网卡地址
            string? fallback = null;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var properties = nic.GetIPProperties();
                string? candidate = null;
                foreach (var address in properties.UnicastAddresses)
                {
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !System.Net.IPAddress.IsLoopback(address.Address))
                    {
                        candidate = address.Address.ToString();
                        break;
                    }
                }

                if (candidate == null)
                {
                    continue;
                }

                bool hasGateway = false;
                foreach (var gateway in properties.GatewayAddresses)
                {
                    if (gateway.Address != null &&
                        !gateway.Address.Equals(System.Net.IPAddress.Any))
                    {
                        hasGateway = true;
                        break;
                    }
                }

                if (hasGateway)
                {
                    return candidate;
                }

                fallback ??= candidate;
            }

            if (fallback != null)
            {
                return fallback;
            }
        }
        catch
        {
            // 网卡枚举失败时退回回环地址
        }

        return "127.0.0.1";
    }
    public int MaxAvailableCores => IsGpuAccelerated ? 8 : Math.Max(1, Environment.ProcessorCount);

    [ObservableProperty]
    private string _statusMessage = "就绪：点击“启动服务”开放本地 HTTP 识别接口";

    [ObservableProperty]
    private int _totalRequests;

    [ObservableProperty]
    private int _successRequests;

    [ObservableProperty]
    private int _failedRequests;

    [ObservableProperty]
    private double _avgElapsedMs;

    [ObservableProperty]
    private int _activeTasks;

    [ObservableProperty]
    private int _queuedTasks;

    public ObservableCollection<HttpApiLogItem> Logs { get; } = new();

    /// <summary>
    /// 令牌鉴权提示（示例代码顶部展示）
    /// </summary>
    public string AuthHeaderHint => string.IsNullOrEmpty(AccessToken)
        ? (AllowAnonymousRemote
            ? "# 当前为【局域网无令牌】模式：同网段设备可直接调用，无需请求头"
            : "# 当前未配置访问令牌：接口仅接受本机直连调用（浏览器跨域与非本机来源一律拒绝）")
        : $"# 已启用令牌鉴权，每个请求都必须携带请求头：Authorization: Bearer {AccessToken}";

    public string CurlExample =>
$@"{AuthHeaderHint}

# 1. 表单文件上传识别 (最常用)
curl -X POST ""{EndpointUrl}"" -F ""file=@sample.png""

# 2. 表单上传并按框分列 (多列数据导出 \t 制表符)
curl -X POST ""{EndpointUrl}?splitColumns=true"" -F ""file=@sample.png""

# 3. 纯文本格式直接返回 (无须解析 JSON)
curl -X POST ""{EndpointUrl}?format=text"" -F ""file=@sample.png""

# 4. JSON Base64 编码调用
curl -X POST ""{EndpointUrl}"" \
  -H ""Content-Type: application/json"" \
  -d '{{""image"": ""iVBORw0KGgoAAAANSUhEUgAA...""}}'

# 5. 二进制图像流直传
curl -X POST ""{EndpointUrl}"" \
  -H ""Content-Type: image/png"" \
  --data-binary @sample.png";

    public string PythonExample =>
$@"import requests

url = ""{EndpointUrl}""

# 若已启用令牌鉴权，请填入页面显示的访问令牌；未启用时该请求头会被服务端忽略
headers = {{""Authorization"": ""Bearer {AccessToken}""}}

# 方式 1: 上传图片并获取 JSON 详细结果 (包含文字框位置与置信度)
with open(""sample.png"", ""rb"") as f:
    files = {{""file"": f}}
    resp = requests.post(url, files=files, headers=headers)
    data = resp.json()
    print(""识别文本:\n"", data.get(""text""))
    print(f""耗时: {{data.get('elapsedMs')}} ms"")

# 方式 2: 获取纯文本字符串 (极简调用)
with open(""sample.png"", ""rb"") as f:
    text = requests.post(f""{{url}}?format=text"", files={{""file"": f}}, headers=headers).text
    print(text)";

    public string CsharpExample =>
$@"using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

using var client = new HttpClient();
// 若已启用令牌鉴权，请附加此行（未启用时服务端会忽略）：
// client.DefaultRequestHeaders.Add(""Authorization"", ""Bearer {AccessToken}"");
using var form = new MultipartFormDataContent();
form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(""sample.png"")), ""file"", ""sample.png"");

var response = await client.PostAsync(""{EndpointUrl}"", form);
string json = await response.Content.ReadAsStringAsync();
Console.WriteLine(json);";

    public HttpApiViewModel(
        IOcrEngineFactory engineFactory,
        ISettingsService settingsService,
        IClipboardService? clipboardService = null,
        ILoggerService? logger = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _clipboardService = clipboardService;
        _logger = logger;

        _isLoadingSettings = true;
        var s = _settingsService.Current;
        Port = Math.Clamp(s.HttpApiPort, 1024, 65535);
        AllowRemote = s.HttpApiAllowRemote;
        AccessToken = s.HttpApiAccessToken ?? string.Empty;
        AllowAnonymousRemote = s.HttpApiAllowAnonymousRemote && string.IsNullOrEmpty(AccessToken);
        UpdateSchedulerDeviceState(s);
        if (IsGpuAccelerated)
        {
            MaxCores = Math.Clamp(s.HttpApiGpuStreams, 1, 8);
            if (MaxCores > 1)
            {
                _concurrencyWarningConfirmed = true;
            }
        }
        else
        {
            MaxCores = Math.Clamp(s.HttpApiMaxCores, 1, Environment.ProcessorCount);
            _concurrencyWarningConfirmed = false;
        }
        CpuLimitPercent = Math.Clamp(s.HttpApiCpuLimitPercent, 10, 100);
        _isLoadingSettings = false;

        _settingsService.SettingsChanged += OnSettingsChanged;

        _server = new HttpOcrServer(engineFactory, settingsService, logger);
        _server.RequestLogged += OnServerRequestLogged;
        _server.StateChanged += OnServerStateChanged;
    }

    private void OnSettingsChanged(AppSettings newSettings)
    {
        Dispatcher.UIThread.Post(() => UpdateSchedulerDeviceState(newSettings));
    }

    private void UpdateSchedulerDeviceState(AppSettings s)
    {
        string backend = (s.OcrBackend ?? "Auto").Trim();
        bool isGpu = false;
        string? gpuDeviceName = null;
        if (backend.Equals("GPU", StringComparison.OrdinalIgnoreCase))
        {
            isGpu = true;
            try
            {
                var probe = ShawMuOCR.OCR.PaddleOcrV6Engine.ProbeBackendInfo();
                gpuDeviceName = probe.GpuDeviceName;
            }
            catch { }
        }
        else if (backend.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var probe = ShawMuOCR.OCR.PaddleOcrV6Engine.ProbeBackendInfo();
                isGpu = probe.AutoSelectsGpu;
                gpuDeviceName = probe.GpuDeviceName;
            }
            catch { }
        }

        bool oldIsGpu = IsGpuAccelerated;
        IsGpuAccelerated = isGpu;
        if (isGpu)
        {
            DeviceBadgeText = "🚀 GPU 硬件加速 (Vulkan)";
            SchedulerTitle = "🚀 GPU 推理调度与并发控制";
            SchedulerSummaryText = !string.IsNullOrWhiteSpace(gpuDeviceName)
                ? $"设备: {gpuDeviceName} (默认 1 路并发 / 上限 8 路)"
                : "当前跟随系统设置：启用纯 C# Vulkan GPU 加速调度 (默认 1 路并发 / 上限 8 路)";
        }
        else
        {
            DeviceBadgeText = "⚡ CPU 多核调度 (AVX2)";
            SchedulerTitle = "⚡ CPU 调度与性能控制";
            SchedulerSummaryText = $"本机共 {Environment.ProcessorCount} 逻辑核 (默认 {Math.Max(1, Environment.ProcessorCount / 2)} 核 / 上限 {Environment.ProcessorCount} 核)";
        }

        if (oldIsGpu != isGpu && !_isLoadingSettings)
        {
            _isAdjustingCoresInternally = true;
            if (isGpu)
            {
                MaxCores = Math.Clamp(s.HttpApiGpuStreams, 1, 8);
                _concurrencyWarningConfirmed = MaxCores > 1;
            }
            else
            {
                MaxCores = Math.Clamp(s.HttpApiMaxCores, 1, Environment.ProcessorCount);
                _concurrencyWarningConfirmed = false;
            }
            _isAdjustingCoresInternally = false;
            OnPropertyChanged(nameof(MaxCores));
            OnPropertyChanged(nameof(ConcurrencyValueText));
        }
    }

    partial void OnPortChanged(int value)
    {
        SaveSettings();
    }

    partial void OnAllowRemoteChanged(bool value)
    {
        // 开启局域网访问即意味着对外暴露识别接口：
        // 默认自动生成令牌；若用户已选择"无令牌开放"，则保持空令牌。
        if (!_isLoadingSettings && value && !AllowAnonymousRemote && string.IsNullOrWhiteSpace(AccessToken))
        {
            _isNormalizingToken = true;
            try
            {
                AccessToken = GenerateAccessToken();
            }
            finally
            {
                _isNormalizingToken = false;
            }
            StatusMessage = "✓ 已自动生成局域网访问令牌，调用方需携带 Authorization: Bearer <令牌>";
        }
        else if (!_isLoadingSettings && value && AllowAnonymousRemote)
        {
            StatusMessage = "当前为局域网无令牌模式：同网段设备可直接调用，无需令牌";
        }

        SaveSettings();
    }

    partial void OnAllowAnonymousRemoteChanged(bool value)
    {
        if (_isLoadingSettings) return;

        if (value)
        {
            // 无令牌模式与令牌鉴权互斥：清空令牌，否则服务端仍会强制校验
            if (!string.IsNullOrEmpty(AccessToken))
            {
                _isNormalizingToken = true;
                try
                {
                    AccessToken = string.Empty;
                }
                finally
                {
                    _isNormalizingToken = false;
                }
            }

            StatusMessage = IsRunning
                ? "⚠ 已切换为无令牌模式，需重启服务后生效"
                : "已切换为局域网无令牌模式：同网段设备可直接调用（接口不会读取本机文件）";
        }
        else if (AllowRemote && string.IsNullOrWhiteSpace(AccessToken))
        {
            _isNormalizingToken = true;
            try
            {
                AccessToken = GenerateAccessToken();
            }
            finally
            {
                _isNormalizingToken = false;
            }
            StatusMessage = "✓ 已恢复令牌鉴权，并生成新的访问令牌";
        }

        SaveSettings();
    }

    /// <summary>
    /// 生成 24 字节随机访问令牌（48 位十六进制）
    /// </summary>
    private static string GenerateAccessToken()
    {
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    [RelayCommand]
    public async Task CopyAccessTokenAsync()
    {
        if (_clipboardService == null) return;
        if (string.IsNullOrEmpty(AccessToken))
        {
            StatusMessage = "当前未配置访问令牌";
            return;
        }

        await _clipboardService.SetTextAsync(AccessToken);
        StatusMessage = "✓ 已复制访问令牌到剪贴板";
    }

    [RelayCommand]
    public void RegenerateAccessToken()
    {
        if (IsRunning)
        {
            StatusMessage = "请先停止服务，再重新生成访问令牌";
            return;
        }

        AccessToken = GenerateAccessToken();
        SaveSettings();
        StatusMessage = "✓ 已生成新的访问令牌，请同步更新所有调用方（旧令牌立即失效）";
    }

    partial void OnMaxCoresChanged(int oldValue, int newValue)
    {
        if (_isLoadingSettings || _isAdjustingCoresInternally) return;

        // 仅在 GPU 模式且并发流大于 1 且未确认时弹出警告拦截；CPU 模式下不弹窗，直接生效
        if (IsGpuAccelerated && newValue > 1 && !_concurrencyWarningConfirmed)
        {
            PendingMaxCores = newValue;
            OnPropertyChanged(nameof(ConcurrencyWarningTitle));
            OnPropertyChanged(nameof(ConcurrencyWarningMessage));
            OnPropertyChanged(nameof(CancelConcurrencyButtonText));
            OnPropertyChanged(nameof(ConfirmConcurrencyButtonText));
            IsConcurrencyWarningOpen = true;
            return;
        }

        if (IsGpuAccelerated && newValue <= 1)
        {
            _concurrencyWarningConfirmed = false;
        }

        if (IsRunning)
        {
            _server.UpdateCpuSettings(newValue, CpuLimitPercent);
        }
        SaveSettings();
    }

    [RelayCommand]
    public void ConfirmConcurrencyWarning()
    {
        IsConcurrencyWarningOpen = false;
        _concurrencyWarningConfirmed = true;

        _isAdjustingCoresInternally = true;
        MaxCores = PendingMaxCores;
        _isAdjustingCoresInternally = false;
        OnPropertyChanged(nameof(MaxCores));
        OnPropertyChanged(nameof(ConcurrencyValueText));

        Dispatcher.UIThread.Post(() =>
        {
            _isAdjustingCoresInternally = true;
            MaxCores = PendingMaxCores;
            _isAdjustingCoresInternally = false;
            OnPropertyChanged(nameof(MaxCores));
            OnPropertyChanged(nameof(ConcurrencyValueText));
        }, DispatcherPriority.Render);

        if (IsRunning)
        {
            _server.UpdateCpuSettings(MaxCores, CpuLimitPercent);
        }
        SaveSettings();
    }

    [RelayCommand]
    public void CancelConcurrencyWarning()
    {
        IsConcurrencyWarningOpen = false;
        _concurrencyWarningConfirmed = false;

        _isAdjustingCoresInternally = true;
        MaxCores = 1;
        _isAdjustingCoresInternally = false;
        OnPropertyChanged(nameof(MaxCores));
        OnPropertyChanged(nameof(ConcurrencyValueText));

        Dispatcher.UIThread.Post(() =>
        {
            _isAdjustingCoresInternally = true;
            MaxCores = 1;
            _isAdjustingCoresInternally = false;
            OnPropertyChanged(nameof(MaxCores));
            OnPropertyChanged(nameof(ConcurrencyValueText));
        }, DispatcherPriority.Render);

        if (IsRunning)
        {
            _server.UpdateCpuSettings(1, CpuLimitPercent);
        }
        SaveSettings();
    }

    partial void OnCpuLimitPercentChanged(int value)
    {
        if (IsRunning)
        {
            _server.UpdateCpuSettings(MaxCores, value);
        }
        SaveSettings();
    }

    private void SaveSettings()
    {
        if (_isLoadingSettings) return;

        var s = _settingsService.Current.Clone();
        s.HttpApiPort = Port;
        s.HttpApiAllowRemote = AllowRemote;

        if (AllowRemote)
        {
            if (AllowAnonymousRemote)
            {
                // 无令牌开放：令牌必须为空，否则鉴权仍然生效
                if (!string.IsNullOrEmpty(AccessToken))
                {
                    _isNormalizingToken = true;
                    try
                    {
                        AccessToken = string.Empty;
                    }
                    finally
                    {
                        _isNormalizingToken = false;
                    }
                }
                s.HttpApiAccessToken = string.Empty;
            }
            else if (string.IsNullOrWhiteSpace(AccessToken) && !_isNormalizingToken)
            {
                string generated = GenerateAccessToken();
                _isNormalizingToken = true;
                try
                {
                    AccessToken = generated;
                }
                finally
                {
                    _isNormalizingToken = false;
                }
                s.HttpApiAccessToken = generated;
            }
            else
            {
                s.HttpApiAccessToken = AccessToken ?? string.Empty;
            }
        }
        else
        {
            s.HttpApiAccessToken = AccessToken ?? string.Empty;
        }

        s.HttpApiAllowAnonymousRemote = AllowAnonymousRemote;

        if (IsGpuAccelerated)
        {
            s.HttpApiGpuStreams = MaxCores;
        }
        else
        {
            s.HttpApiMaxCores = MaxCores;
        }
        s.HttpApiCpuLimitPercent = CpuLimitPercent;
        _settingsService.Save(s);
    }

    [RelayCommand]
    public async Task ToggleServerAsync()
    {
        if (IsRunning)
        {
            StatusMessage = "正在停止服务...";
            await _server.StopAsync();
            StatusMessage = "服务已停止";
        }
        else
        {
            try
            {
                StatusMessage = "正在启动服务...";
                await _server.StartAsync(
                    Port,
                    AllowRemote,
                    MaxCores,
                    CpuLimitPercent,
                    string.IsNullOrWhiteSpace(AccessToken) ? null : AccessToken,
                    AllowAnonymousRemote);
                StatusMessage = !AllowRemote
                    ? $"✓ 服务已启动，正在监听 {EndpointUrl}"
                    : _server.IsRemoteBindingFallback
                        ? $"⚠ 仅本机可访问：绑定 0.0.0.0 失败。请以管理员身份运行，或先执行 netsh http add urlacl url=http://+:{Port}/ user=Everyone"
                        : _server.RequiresAuth
                            ? $"✓ 服务已启动（已启用令牌鉴权），局域网可用 {LanEndpointUrl}"
                            : $"⚠ 服务已启动（局域网无令牌开放），同网段设备可直接调用 {LanEndpointUrl}";
            }
            catch (Exception ex)
            {
                _logger?.Error("启动 HTTP OCR 服务失败", ex);
                StatusMessage = $"✕ 启动失败: {ex.Message}";
            }
        }
    }

    [RelayCommand]
    public async Task CopyEndpointAsync()
    {
        if (_clipboardService != null)
        {
            await _clipboardService.SetTextAsync(EndpointUrl);
            StatusMessage = $"✓ 已复制接口地址: {EndpointUrl}";
        }
    }

    [RelayCommand]
    public async Task CopyCurlCommandAsync()
    {
        if (_clipboardService != null)
        {
            await _clipboardService.SetTextAsync(CurlExample);
            StatusMessage = "✓ 已复制 cURL 示例到剪贴板";
        }
    }

    [RelayCommand]
    public async Task CopyPythonCommandAsync()
    {
        if (_clipboardService != null)
        {
            await _clipboardService.SetTextAsync(PythonExample);
            StatusMessage = "✓ 已复制 Python 示例到剪贴板";
        }
    }

    [RelayCommand]
    public async Task CopyCsharpCommandAsync()
    {
        if (_clipboardService != null)
        {
            await _clipboardService.SetTextAsync(CsharpExample);
            StatusMessage = "✓ 已复制 C# 示例到剪贴板";
        }
    }

    [RelayCommand]
    public void ClearLogs()
    {
        Logs.Clear();
        TotalRequests = 0;
        SuccessRequests = 0;
        FailedRequests = 0;
        AvgElapsedMs = 0;
        _totalElapsedAccumulator = 0;
        StatusMessage = "已清空调用审计日志与统计数据";
    }

    private void OnServerStateChanged(bool running)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsRunning = running;
        });
    }

    private void OnServerRequestLogged(HttpApiLogItem item)
    {
        Dispatcher.UIThread.Post(() =>
        {
            TotalRequests++;
            if (item.IsSuccess)
            {
                SuccessRequests++;
            }
            else
            {
                FailedRequests++;
            }

            _totalElapsedAccumulator += item.ElapsedMs;
            AvgElapsedMs = Math.Round(_totalElapsedAccumulator / TotalRequests, 1);

            ActiveTasks = _server.ActiveTasks;
            QueuedTasks = _server.QueuedTasks;

            Logs.Insert(0, item);
            if (Logs.Count > 100)
            {
                Logs.RemoveAt(Logs.Count - 1);
            }
        });
    }

    public void Dispose()
    {
        _server.RequestLogged -= OnServerRequestLogged;
        _server.StateChanged -= OnServerStateChanged;
        _server.Dispose();
    }
}
