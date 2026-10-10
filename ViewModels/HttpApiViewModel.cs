using System;
using System.Collections.ObjectModel;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EndpointUrl))]
    [NotifyPropertyChangedFor(nameof(CurlExample))]
    [NotifyPropertyChangedFor(nameof(PythonExample))]
    [NotifyPropertyChangedFor(nameof(CsharpExample))]
    private int _port = 8080;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EndpointUrl))]
    [NotifyPropertyChangedFor(nameof(CurlExample))]
    [NotifyPropertyChangedFor(nameof(PythonExample))]
    [NotifyPropertyChangedFor(nameof(CsharpExample))]
    private bool _allowRemote = false;

    [ObservableProperty]
    private int _maxCores = Math.Max(1, Environment.ProcessorCount / 2);

    [ObservableProperty]
    private int _cpuLimitPercent = 50;

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
    public int MaxAvailableCores => Environment.ProcessorCount;

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

    public string CurlExample =>
$@"# 1. 表单文件上传识别 (最常用)
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

# 方式 1: 上传图片并获取 JSON 详细结果 (包含文字框位置与置信度)
with open(""sample.png"", ""rb"") as f:
    files = {{""file"": f}}
    resp = requests.post(url, files=files)
    data = resp.json()
    print(""识别文本:\n"", data.get(""text""))
    print(f""耗时: {{data.get('elapsedMs')}} ms"")

# 方式 2: 获取纯文本字符串 (极简调用)
with open(""sample.png"", ""rb"") as f:
    text = requests.post(f""{{url}}?format=text"", files={{""file"": f}}).text
    print(text)";

    public string CsharpExample =>
$@"using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

using var client = new HttpClient();
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
        MaxCores = Math.Clamp(s.HttpApiMaxCores, 1, Environment.ProcessorCount);
        CpuLimitPercent = Math.Clamp(s.HttpApiCpuLimitPercent, 10, 100);
        _isLoadingSettings = false;

        _server = new HttpOcrServer(engineFactory, settingsService, logger);
        _server.RequestLogged += OnServerRequestLogged;
        _server.StateChanged += OnServerStateChanged;
    }

    partial void OnPortChanged(int value)
    {
        SaveSettings();
    }

    partial void OnAllowRemoteChanged(bool value)
    {
        SaveSettings();
    }

    partial void OnMaxCoresChanged(int value)
    {
        if (IsRunning)
        {
            _server.UpdateCpuSettings(value, CpuLimitPercent);
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
        s.HttpApiMaxCores = MaxCores;
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
                await _server.StartAsync(Port, AllowRemote, MaxCores, CpuLimitPercent);
                StatusMessage = $"✓ 服务已启动，正在监听 {EndpointUrl}";
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
