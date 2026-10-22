using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ShawMuOCR.Core.Logging;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Scheduling;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.Core.Sorting;
using ShawMuOCR.Services.Batch;
using SkiaSharp;

namespace ShawMuOCR.Services.Http;

/// <summary>
/// 纯 .NET BCL 零外部依赖的高性能嵌入式 HTTP OCR 服务端
/// 100% Native AOT 兼容，复用 CpuThrottleScheduler 多核并发调度与占空比限流机制
/// </summary>
public sealed class HttpOcrServer : IDisposable
{
    private static readonly HttpApiJsonContext s_jsonContext = new(new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    });

    private readonly IOcrEngineFactory _engineFactory;
    private readonly ISettingsService _settingsService;
    private readonly ILoggerService? _logger;
    private readonly IOcrTextSorter _textSorter = new DefaultOcrTextSorter();

    /// <summary>
    /// 单个请求体的硬上限（20MB）。超出即刻 413 拒绝，防止通过无界读取打爆进程内存。
    /// </summary>
    private const long MaxRequestBodyBytes = 20L * 1024 * 1024;

    /// <summary>
    /// 请求并发闸门的最小配额（即使调度器只有 1 核，也允许 2 个请求同时在途，
    /// 其余请求直接 429 快速失败，绝不进入无界等待队列）。
    /// </summary>
    private const int MinRequestGatePermits = 2;

    private HttpListener? _listener;
    private OcrEnginePool? _enginePool;
    private CpuThrottleScheduler? _scheduler;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;

    /// <summary>
    /// 请求准入闸门：非阻塞获取，取不到即返回 429，杜绝无界排队导致的内存放大。
    /// </summary>
    private SemaphoreSlim? _requestGate;

    /// <summary>
    /// 访问令牌。非空时所有接口强制校验 Authorization: Bearer &lt;token&gt;。
    /// </summary>
    private string? _accessToken;

    /// <summary>
    /// 是否显式允许局域网无令牌访问（仅影响"无令牌时能否开放到局域网"，不影响令牌校验本身）
    /// </summary>
    private bool _allowAnonymousRemote;

    /// <summary>
    /// 局域网绑定是否失败并回退到仅本机监听。
    /// 非管理员且未注册 URL ACL 时，HttpListener 无法绑定 http://*:port/，会静默退回 127.0.0.1，
    /// 此时"局域网共享"实际不可用，必须让 UI 明确告知用户。
    /// </summary>
    private bool _remoteBindingFallback;

    /// <summary>
    /// 准入闸门配额（仅用于状态展示与日志）
    /// </summary>
    private int _requestGatePermits;

    private readonly object _stateLock = new();
    private bool _isRunning;
    private int _port = 8080;
    private bool _allowRemote;

    public event Action<HttpApiLogItem>? RequestLogged;
    public event Action<bool>? StateChanged;

    public bool IsRunning => _isRunning;
    public int Port => _port;
    public bool AllowRemote => _allowRemote;
    public bool RequiresAuth => !string.IsNullOrEmpty(_accessToken);

    /// <summary>
    /// 当前是否处于"局域网无令牌开放"状态（仅供 UI/日志展示）
    /// </summary>
    public bool IsAnonymousRemote => _allowRemote && !RequiresAuth;

    /// <summary>
    /// 局域网绑定失败、已回退为仅本机监听（此时局域网其他人无法访问）
    /// </summary>
    public bool IsRemoteBindingFallback => _remoteBindingFallback;

    /// <summary>
    /// 实际生效的监听范围描述
    /// </summary>
    public string EffectiveBindingText => _allowRemote
        ? (_remoteBindingFallback ? "仅本机 (127.0.0.1)" : "局域网 (0.0.0.0)")
        : "仅本机 (127.0.0.1)";
    public string BaseUrl => $"http://{(_allowRemote ? "0.0.0.0" : "127.0.0.1")}:{_port}";
    public string OcrEndpointUrl => $"http://127.0.0.1:{_port}/ocr";

    public int MaxCores => _scheduler?.MaxCores ?? 1;
    public int CpuLimitPercent => _scheduler?.CpuLimitPercent ?? 50;
    public int ActiveTasks => _scheduler?.ActiveCount ?? 0;
    public int QueuedTasks => _scheduler?.QueuedCount ?? 0;

    /// <summary>
    /// 当前可同时处理的请求数上限（准入闸门配额）
    /// </summary>
    public int MaxConcurrentRequests => _requestGatePermits;

    public HttpOcrServer(
        IOcrEngineFactory engineFactory,
        ISettingsService settingsService,
        ILoggerService? logger = null)
    {
        _engineFactory = engineFactory ?? throw new ArgumentNullException(nameof(engineFactory));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _logger = logger;
    }

    /// <summary>
    /// 启动 HTTP OCR 服务
    /// </summary>
    /// <param name="port">监听端口</param>
    /// <param name="allowRemote">是否允许局域网访问（开启时必须提供访问令牌）</param>
    /// <param name="maxCores">最大并发核心数</param>
    /// <param name="cpuLimitPercent">CPU 占空比上限</param>
    /// <param name="accessToken">访问令牌；非空时所有接口强制 Bearer 鉴权</param>
    /// <param name="allowAnonymousRemote">
    /// 允许局域网无令牌访问。仅当用户显式开启时才生效——用于"临时分享给同网段同事"的场景。
    /// 注意：开启后同网段任何人都可调用本机 OCR 并占用 CPU，但接口不会读取任何本机文件。
    /// </param>
    public Task StartAsync(
        int port,
        bool allowRemote,
        int maxCores,
        int cpuLimitPercent,
        string? accessToken = null,
        bool allowAnonymousRemote = false)
    {
        lock (_stateLock)
        {
            if (_isRunning)
            {
                return Task.CompletedTask;
            }

            if (port is < 1 or > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(port), "端口号必须位于 1 到 65535 之间");
            }

            // 失败即关闭（Fail-Closed）：局域网暴露默认必须携带令牌，
            // 只有用户显式勾选"允许无令牌访问"才放行，避免"开了开关就以为安全"。
            if (allowRemote && string.IsNullOrWhiteSpace(accessToken) && !allowAnonymousRemote)
            {
                throw new InvalidOperationException(
                    "开启局域网访问需要访问令牌（AccessToken）。可点击“重新生成令牌”，" +
                    "或显式勾选“允许局域网无令牌访问”后再启动服务。");
            }

            _port = port;
            _allowRemote = allowRemote;
            _accessToken = string.IsNullOrWhiteSpace(accessToken) ? null : accessToken.Trim();
            _allowAnonymousRemote = allowAnonymousRemote;
            _remoteBindingFallback = false;

            if (_allowRemote && _accessToken == null)
            {
                _logger?.Warn($"HTTP OCR 服务以【无令牌】模式开放到局域网（端口 {_port}）：同网段任何设备均可调用并占用本机 CPU。");
            }

            _scheduler = new CpuThrottleScheduler(maxCores, cpuLimitPercent);
            _enginePool = new OcrEnginePool(_engineFactory, _settingsService.Current, _scheduler.MaxCores);

            // 准入闸门配额 = 调度并发 × 2，最小 2；超出的请求立刻 429，不再堆积内存
            int gatePermits = Math.Max(MinRequestGatePermits, _scheduler.MaxCores * 2);
            _requestGate = new SemaphoreSlim(gatePermits, gatePermits);
            _requestGatePermits = gatePermits;

            _listener = new HttpListener();
            _listener.IgnoreWriteExceptions = true;

            if (_allowRemote)
            {
                try
                {
                    _listener.Prefixes.Add($"http://*:{_port}/");
                }
                catch (Exception ex)
                {
                    _logger?.Warn($"绑定全局前缀 http://*:{_port}/ 失败（可能需要管理员权限），回退至本地 127.0.0.1: {ex.Message}");
                    _remoteBindingFallback = true;
                    _listener.Prefixes.Clear();
                    _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                    _listener.Prefixes.Add($"http://localhost:{_port}/");
                }
            }
            else
            {
                _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                _listener.Prefixes.Add($"http://localhost:{_port}/");
            }

            try
            {
                _listener.Start();
            }
            catch (HttpListenerException hlex) when (hlex.ErrorCode == 5) // Access Denied
            {
                _logger?.Warn($"启动监听器权限受限，尝试切换为仅 127.0.0.1 模式: {hlex.Message}");
                _remoteBindingFallback = _allowRemote;
                _listener.Close();
                _listener = new HttpListener();
                _listener.IgnoreWriteExceptions = true;
                _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                _listener.Start();
            }

            if (_remoteBindingFallback)
            {
                _logger?.Warn(
                    "局域网绑定失败，服务已回退为仅本机监听（局域网其他人无法访问）。" +
                    $"如需真正开放到局域网，请以管理员身份运行，或先执行：netsh http add urlacl url=http://+:{_port}/ user=Everyone");
            }

            _cts = new CancellationTokenSource();
            _isRunning = true;
            _acceptLoopTask = Task.Run(() => AcceptConnectionsAsync(_cts.Token));

            _logger?.Info($"HTTP OCR 服务启动成功: 监听端口={_port}, 监听范围={EffectiveBindingText}, 鉴权={(_accessToken != null ? "Bearer 令牌" : "关闭")}, 请求并发上限={gatePermits}, 最大核心={_scheduler.MaxCores}, CPU限制={_scheduler.CpuLimitPercent}%");
            StateChanged?.Invoke(true);

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 停止 HTTP OCR 服务
    /// </summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        HttpListener? listener;
        OcrEnginePool? enginePool;
        CpuThrottleScheduler? scheduler;
        SemaphoreSlim? gate;
        Task? acceptTask;

        lock (_stateLock)
        {
            if (!_isRunning)
            {
                return;
            }

            _isRunning = false;
            cts = _cts;
            listener = _listener;
            enginePool = _enginePool;
            scheduler = _scheduler;
            gate = _requestGate;
            acceptTask = _acceptLoopTask;

            _cts = null;
            _listener = null;
            _enginePool = null;
            _scheduler = null;
            _requestGate = null;
            _requestGatePermits = 0;
            _accessToken = null;
            _allowAnonymousRemote = false;
            _acceptLoopTask = null;
        }

        try
        {
            cts?.Cancel();
            listener?.Stop();
            listener?.Close();
        }
        catch { }

        if (acceptTask != null)
        {
            try
            {
                await Task.WhenAny(acceptTask, Task.Delay(500));
            }
            catch { }
        }

        try
        {
            enginePool?.Dispose();
        }
        catch { }

        try
        {
            scheduler?.Dispose();
        }
        catch { }

        gate?.Dispose();

        cts?.Dispose();

        _logger?.Info("HTTP OCR 服务已停止");
        StateChanged?.Invoke(false);
    }

    /// <summary>
    /// 热更新 CPU 调度参数（无须重启服务）
    /// </summary>
    public void UpdateCpuSettings(int maxCores, int cpuLimitPercent)
    {
        _scheduler?.UpdateSettings(maxCores, cpuLimitPercent);
    }

    private async Task AcceptConnectionsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            try
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);
                _ = Task.Run(() => HandleRequestSafeAsync(context, ct), ct);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested || !_isRunning)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.Error("HTTP 监听器接收连接异常", ex);
                if (ct.IsCancellationRequested || !_isRunning) break;
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleRequestSafeAsync(HttpListenerContext context, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        int statusCode = 200;
        string logMsg = "OK";
        string path = context.Request.Url?.AbsolutePath ?? "/";
        string method = context.Request.HttpMethod;
        string remoteIp = context.Request.RemoteEndPoint.Address.ToString();

        try
        {
            // 跨域策略：仅放行本机来源（localhost / 127.0.0.1 / [::1]）的浏览器页面。
            // 不再使用 "*" 通配，避免任意网站的 JS 直接跨域读写本机识别接口。
            if (!ApplyCorsHeaders(context))
            {
                statusCode = 403;
                logMsg = "跨域来源被拒绝";
                await WriteJsonResponseAsync(context, 403, new HttpOcrResponse
                {
                    Code = 403,
                    Success = false,
                    ErrorMessage = "跨域来源未被允许：本接口仅接受本机来源页面或直连客户端调用。"
                });
                return;
            }

            if (method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 204;
                context.Response.Close();
                return;
            }

            // 访问令牌校验：配置了令牌即强制 Bearer 鉴权（局域网模式下必然配置）
            if (!IsAuthorized(context.Request))
            {
                statusCode = 401;
                logMsg = "缺少或错误访问令牌";
                context.Response.Headers.Add("WWW-Authenticate", "Bearer realm=\"ShawMuOCR\"");
                await WriteJsonResponseAsync(context, 401, new HttpOcrResponse
                {
                    Code = 401,
                    Success = false,
                    ErrorMessage = "未授权：请在请求头中携带 Authorization: Bearer <访问令牌>（令牌可在小沐 OCR 的 HTTP API 页面查看）。"
                });
                return;
            }

            if (path.Equals("/") || path.Equals("/health", StringComparison.OrdinalIgnoreCase) || path.Equals("/api/info", StringComparison.OrdinalIgnoreCase))
            {
                statusCode = await HandleInfoRequestAsync(context);
                logMsg = "状态查询";
            }
            else if (path.Equals("/ocr", StringComparison.OrdinalIgnoreCase) || path.Equals("/api/ocr", StringComparison.OrdinalIgnoreCase))
            {
                // 非阻塞准入：取不到配额立刻 429 快速失败，绝不进入无界队列
                var gate = _requestGate;
                if (gate != null && !gate.Wait(0))
                {
                    statusCode = 429;
                    logMsg = "请求并发已达上限";
                    await WriteJsonResponseAsync(context, 429, new HttpOcrResponse
                    {
                        Code = 429,
                        Success = false,
                        ErrorMessage = $"服务繁忙：并发请求已达上限 {_requestGatePermits} 路，请稍后重试。"
                    });
                }
                else
                {
                    try
                    {
                        (statusCode, logMsg) = await HandleOcrRequestAsync(context, ct);
                    }
                    finally
                    {
                        gate?.Release();
                    }
                }
            }
            else
            {
                statusCode = 404;
                logMsg = "接口不存在";
                await WriteJsonResponseAsync(context, 404, new HttpOcrResponse
                {
                    Code = 404,
                    Success = false,
                    ErrorMessage = $"接口路径未定义: {path}"
                });
            }
        }
        catch (PayloadTooLargeException ex)
        {
            statusCode = 413;
            logMsg = ex.Message;
            try
            {
                await WriteJsonResponseAsync(context, 413, new HttpOcrResponse
                {
                    Code = 413,
                    Success = false,
                    ErrorMessage = ex.Message
                });
            }
            catch { }
        }
        catch (OperationCanceledException)
        {
            statusCode = 499;
            logMsg = "客户端取消";
        }
        catch (Exception ex)
        {
            statusCode = 500;
            logMsg = $"服务未知异常: {ex.Message}";
            _logger?.Error("处理 HTTP 请求发生未捕获异常", ex);

            try
            {
                await WriteJsonResponseAsync(context, 500, new HttpOcrResponse
                {
                    Code = 500,
                    Success = false,
                    ErrorMessage = $"内部服务错误: {ex.Message}"
                });
            }
            catch { }
        }
        finally
        {
            sw.Stop();
            try
            {
                context.Response.Close();
            }
            catch { }

            RequestLogged?.Invoke(new HttpApiLogItem
            {
                Timestamp = DateTime.Now,
                Method = method,
                Path = path,
                RemoteEndPoint = remoteIp,
                StatusCode = statusCode,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
                Message = logMsg
            });
        }
    }

    /// <summary>
    /// 写入跨域响应头。仅放行本机来源（localhost / 127.0.0.1 / [::1]）。
    /// 返回 false 表示该来源不被允许，调用方应直接返回 403。
    /// </summary>
    private bool ApplyCorsHeaders(HttpListenerContext context)
    {
        string? origin = context.Request.Headers["Origin"];
        string allowOrigin;

        if (string.IsNullOrEmpty(origin))
        {
            // 非浏览器客户端（curl / HttpClient / 脚本）不受 CORS 约束，
            // 这里回填自身来源仅用于保持响应头可预期。
            allowOrigin = $"http://127.0.0.1:{_port}";
        }
        else if (IsLocalOrigin(origin))
        {
            allowOrigin = origin;
        }
        else
        {
            return false;
        }

        var headers = context.Response.Headers;
        headers.Add("Access-Control-Allow-Origin", allowOrigin);
        headers.Add("Vary", "Origin");
        headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization, Accept, X-Requested-With");
        headers.Add("Access-Control-Max-Age", "600");
        return true;
    }

    /// <summary>
    /// 判断 Origin 是否为本机来源（防 DNS 重绑定：只认回环主机名）
    /// </summary>
    private static bool IsLocalOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        string host = uri.Host;
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("127.0.0.1", StringComparison.Ordinal)
            || host.Equals("::1", StringComparison.Ordinal)
            || host.Equals("[::1]", StringComparison.Ordinal);
    }

    /// <summary>
    /// 校验 Authorization: Bearer 令牌。未配置令牌时直接放行（仅限本地模式）。
    /// </summary>
    private bool IsAuthorized(HttpListenerRequest request)
    {
        if (string.IsNullOrEmpty(_accessToken))
        {
            return true;
        }

        string? header = request.Headers["Authorization"];
        if (string.IsNullOrEmpty(header))
        {
            return false;
        }

        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string provided = header[prefix.Length..].Trim();
        if (provided.Length == 0)
        {
            return false;
        }

        // 定长比较，避免时序侧信道
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided),
            Encoding.UTF8.GetBytes(_accessToken));
    }

    /// <summary>
    /// 带上限读取请求体：累计超过 maxBytes 立即抛 <see cref="PayloadTooLargeException"/>，
    /// 避免 StreamReader.ReadToEndAsync / CopyToAsync 把超大请求整体落进内存。
    /// </summary>
    private static async Task<byte[]> ReadBodyBoundedAsync(Stream input, long maxBytes, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81_920);
        try
        {
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    throw new PayloadTooLargeException(maxBytes, total);
                }
                ms.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return ms.ToArray();
    }

    private async Task<int> HandleInfoRequestAsync(HttpListenerContext context)
    {
        var info = new HttpOcrInfoResponse
        {
            Code = 200,
            Status = "ok",
            Service = "ShawMuOCR HTTP API",
            Version = "1.0",
            IsReady = _isRunning && _enginePool != null,
            MaxCores = MaxCores,
            CpuLimitPercent = CpuLimitPercent,
            ActiveTasks = ActiveTasks,
            QueuedTasks = QueuedTasks
        };

        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(info, s_jsonContext.HttpOcrInfoResponse);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = jsonBytes.Length;
        await context.Response.OutputStream.WriteAsync(jsonBytes);
        return 200;
    }

    private async Task<(int statusCode, string logMsg)> HandleOcrRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        var scheduler = _scheduler;
        var enginePool = _enginePool;

        if (scheduler == null || enginePool == null)
        {
            await WriteJsonResponseAsync(context, 503, new HttpOcrResponse
            {
                Code = 503,
                Success = false,
                ErrorMessage = "OCR 服务未处于就绪状态"
            });
            return (503, "服务未就绪");
        }

        // 解析请求参数
        byte[]? imageBytes = null;
        bool splitColumns = false;
        string? querySplit = context.Request.QueryString["splitColumns"];
        if (string.Equals(querySplit, "true", StringComparison.OrdinalIgnoreCase) || querySplit == "1")
        {
            splitColumns = true;
        }

        // 硬上限：声明长度超限直接拒绝，避免无界读取耗尽内存
        long declaredLength = context.Request.ContentLength64;
        if (declaredLength > MaxRequestBodyBytes)
        {
            throw new PayloadTooLargeException(MaxRequestBodyBytes, declaredLength);
        }

        // 安全约束：?path= 查询参数已被移除（原实现可直接读本机任意文件）
        if (context.Request.QueryString["path"] != null)
        {
            await WriteJsonResponseAsync(context, 400, new HttpOcrResponse
            {
                Code = 400,
                Success = false,
                ErrorMessage = "path 查询参数已被禁用：为避免任意文件读取风险，请直接上传图片字节流。"
            });
            return (400, "path 参数已禁用");
        }

        string contentType = context.Request.ContentType ?? string.Empty;

        if (contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            var multipart = HttpMultipartParser.Parse(context.Request.InputStream, contentType, MaxRequestBodyBytes, ct);
            if (multipart != null)
            {
                imageBytes = multipart.FileBytes;
                if (multipart.FormFields.TryGetValue("splitColumns", out var sc) &&
                    (string.Equals(sc, "true", StringComparison.OrdinalIgnoreCase) || sc == "1"))
                {
                    splitColumns = true;
                }
            }
        }
        else if (contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            byte[] jsonBytes = await ReadBodyBoundedAsync(context.Request.InputStream, MaxRequestBodyBytes, ct);
            string jsonBody = Encoding.UTF8.GetString(jsonBytes);
            if (!string.IsNullOrWhiteSpace(jsonBody))
            {
                var req = JsonSerializer.Deserialize(jsonBody, s_jsonContext.HttpOcrRequest);
                if (req != null)
                {
                    if (req.SplitColumns) splitColumns = true;

                    // 安全约束：不再支持按服务端本地路径读取图片。
                    // 该入参一旦存在即等于把"未授权读取本机文件"暴露给任何调用方（含浏览器页面）。
                    if (!string.IsNullOrWhiteSpace(req.ImagePath))
                    {
                        await WriteJsonResponseAsync(context, 400, new HttpOcrResponse
                        {
                            Code = 400,
                            Success = false,
                            ErrorMessage = "imagePath 参数已被禁用：为避免任意文件读取风险，请改用 image/base64 字段或直接 POST 图片二进制流。"
                        });
                        return (400, "imagePath 已禁用");
                    }

                    string? b64 = req.Image ?? req.Base64 ?? req.ImageBase64;
                    if (!string.IsNullOrEmpty(b64))
                    {
                        // 去除 data:image/png;base64, 前缀
                        int commaIdx = b64.IndexOf(',');
                        if (commaIdx >= 0)
                        {
                            b64 = b64[(commaIdx + 1)..];
                        }
                        try
                        {
                            imageBytes = Convert.FromBase64String(b64.Trim());
                        }
                        catch
                        {
                            await WriteJsonResponseAsync(context, 400, new HttpOcrResponse
                            {
                                Code = 400,
                                Success = false,
                                ErrorMessage = "Base64 图像数据解析失败，请检查格式有效性"
                            });
                            return (400, "Base64解析失败");
                        }
                    }
                }
            }
        }
        else
        {
            // 直接传输图片二进制字节流 (e.g. image/png, application/octet-stream)
            imageBytes = await ReadBodyBoundedAsync(context.Request.InputStream, MaxRequestBodyBytes, ct);
        }

        // 安全约束：?path= 查询参数已被移除（原实现可直接读本机任意文件）
        if (context.Request.QueryString["path"] != null)
        {
            await WriteJsonResponseAsync(context, 400, new HttpOcrResponse
            {
                Code = 400,
                Success = false,
                ErrorMessage = "path 查询参数已被禁用：为避免任意文件读取风险，请直接上传图片字节流。"
            });
            return (400, "path 参数已禁用");
        }

        if (imageBytes == null || imageBytes.Length == 0)
        {
            await WriteJsonResponseAsync(context, 400, new HttpOcrResponse
            {
                Code = 400,
                Success = false,
                ErrorMessage = "未能从请求中获取到有效的图片数据（支持 multipart/form-data 文件上传、JSON base64、或纯图片二进制流）"
            });
            return (400, "无图片数据");
        }

        // 解码动作放入调度器内部执行，确保排队等待期间不持有解码后的位图内存。
        string? decodeError = null;
        var ocrSw = Stopwatch.StartNew();
        OcrResult? ocrResult = await scheduler.ExecuteAsync<OcrResult?>(async ctLocal =>
        {
            SKBitmap? bitmap;
            try
            {
                bitmap = ImageOrientationHelper.DecodeAndOrient(imageBytes, ImageRotationMode.Auto);
            }
            catch (Exception ex)
            {
                decodeError = $"图片解码异常，请确认图像数据是否有效: {ex.Message}";
                return null;
            }

            if (bitmap == null)
            {
                decodeError = "图片解码失败，请确认图像数据是否有效 (JPEG/PNG/BMP/WebP/GIF)";
                return null;
            }

            using (bitmap)
            using (var ocrImage = new OcrImage(bitmap))
            {
                var engine = enginePool.Rent();
                try
                {
                    return await engine.RecognizeAsync(ocrImage, ctLocal);
                }
                finally
                {
                    enginePool.Return(engine);
                }
            }
        }, ct);
        ocrSw.Stop();

        if (decodeError != null)
        {
            await WriteJsonResponseAsync(context, 400, new HttpOcrResponse
            {
                Code = 400,
                Success = false,
                ErrorMessage = decodeError
            });
            return (400, "解码失败");
        }

        if (ocrResult == null || !ocrResult.IsSuccess)
        {
            await WriteJsonResponseAsync(context, 500, new HttpOcrResponse
            {
                Code = 500,
                Success = false,
                ErrorMessage = ocrResult?.ErrorMessage ?? "OCR 引擎识别失败"
            });
            return (500, ocrResult?.ErrorMessage ?? "识别失败");
        }

        string formattedText = splitColumns && ocrResult.Blocks.Count > 0
            ? _textSorter.SortAndFormat(ocrResult.Blocks, separateColumns: true)
            : (ocrResult.Text ?? string.Empty);

        var blockDtos = new List<HttpOcrBlockDto>(ocrResult.Blocks.Count);
        foreach (var b in ocrResult.Blocks)
        {
            var blockDto = new HttpOcrBlockDto
            {
                Text = b.Text,
                Confidence = b.Confidence
            };
            if (b.BoxPoints is { Count: > 0 })
            {
                var boxList = new int[b.BoxPoints.Count][];
                for (int i = 0; i < b.BoxPoints.Count; i++)
                {
                    boxList[i] = [b.BoxPoints[i].X, b.BoxPoints[i].Y];
                }
                blockDto.Box = boxList;
            }
            blockDtos.Add(blockDto);
        }

        string? queryFormat = context.Request.QueryString["format"];
        if (string.Equals(queryFormat, "text", StringComparison.OrdinalIgnoreCase))
        {
            byte[] textBytes = Encoding.UTF8.GetBytes(formattedText);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/plain; charset=utf-8";
            context.Response.ContentLength64 = textBytes.Length;
            await context.Response.OutputStream.WriteAsync(textBytes, ct);
        }
        else
        {
            var responseObj = new HttpOcrResponse
            {
                Code = 200,
                Success = true,
                Text = formattedText,
                ElapsedMs = Math.Round(ocrSw.Elapsed.TotalMilliseconds, 1),
                Blocks = blockDtos
            };
            await WriteJsonResponseAsync(context, 200, responseObj);
        }

        string summary = formattedText.Length > 20 ? formattedText[..20] + "..." : formattedText;
        return (200, $"识别成功: \"{summary.Replace("\r", "").Replace("\n", " ")}\"");
    }

    private static async Task WriteJsonResponseAsync(HttpListenerContext context, int statusCode, HttpOcrResponse response)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(response, s_jsonContext.HttpOcrResponse);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
    }

    private static async Task WriteJsonResponseAsync(HttpListenerContext context, int statusCode, HttpOcrInfoResponse info)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(info, s_jsonContext.HttpOcrInfoResponse);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
    }

    public void Dispose()
    {
        _ = StopAsync();
    }
}
