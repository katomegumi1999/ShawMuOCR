using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
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

    private HttpListener? _listener;
    private OcrEnginePool? _enginePool;
    private CpuThrottleScheduler? _scheduler;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;

    private readonly object _stateLock = new();
    private bool _isRunning;
    private int _port = 8080;
    private bool _allowRemote;

    public event Action<HttpApiLogItem>? RequestLogged;
    public event Action<bool>? StateChanged;

    public bool IsRunning => _isRunning;
    public int Port => _port;
    public bool AllowRemote => _allowRemote;
    public string BaseUrl => $"http://{(_allowRemote ? "0.0.0.0" : "127.0.0.1")}:{_port}";
    public string OcrEndpointUrl => $"http://127.0.0.1:{_port}/ocr";

    public int MaxCores => _scheduler?.MaxCores ?? Math.Max(1, Environment.ProcessorCount / 2);
    public int CpuLimitPercent => _scheduler?.CpuLimitPercent ?? 50;
    public int ActiveTasks => _scheduler?.ActiveCount ?? 0;
    public int QueuedTasks => _scheduler?.QueuedCount ?? 0;

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
    public Task StartAsync(int port, bool allowRemote, int maxCores, int cpuLimitPercent)
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

            _port = port;
            _allowRemote = allowRemote;

            _scheduler = new CpuThrottleScheduler(maxCores, cpuLimitPercent);
            _enginePool = new OcrEnginePool(_engineFactory, _settingsService.Current, _scheduler.MaxCores);

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
                _listener.Close();
                _listener = new HttpListener();
                _listener.IgnoreWriteExceptions = true;
                _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                _listener.Start();
            }

            _cts = new CancellationTokenSource();
            _isRunning = true;
            _acceptLoopTask = Task.Run(() => AcceptConnectionsAsync(_cts.Token));

            _logger?.Info($"HTTP OCR 服务启动成功: 监听端口={_port}, 远程访问={_allowRemote}, 最大核心={_scheduler.MaxCores}, CPU限制={_scheduler.CpuLimitPercent}%");
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
            acceptTask = _acceptLoopTask;

            _cts = null;
            _listener = null;
            _enginePool = null;
            _scheduler = null;
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
            // 添加跨域 CORS 响应头，允许任意网页/前端无阻碍直接调用
            context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
            context.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            context.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization, Accept, X-Requested-With");

            if (method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 204;
                context.Response.Close();
                return;
            }

            if (path.Equals("/") || path.Equals("/health", StringComparison.OrdinalIgnoreCase) || path.Equals("/api/info", StringComparison.OrdinalIgnoreCase))
            {
                statusCode = await HandleInfoRequestAsync(context);
                logMsg = "状态查询";
            }
            else if (path.Equals("/ocr", StringComparison.OrdinalIgnoreCase) || path.Equals("/api/ocr", StringComparison.OrdinalIgnoreCase))
            {
                (statusCode, logMsg) = await HandleOcrRequestAsync(context, ct);
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

        string? queryPath = context.Request.QueryString["path"];
        if (!string.IsNullOrEmpty(queryPath) && File.Exists(queryPath))
        {
            imageBytes = await File.ReadAllBytesAsync(queryPath, ct);
        }
        else
        {
            string contentType = context.Request.ContentType ?? string.Empty;

            if (contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            {
                var multipart = HttpMultipartParser.Parse(context.Request.InputStream, contentType);
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
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                string jsonBody = await reader.ReadToEndAsync(ct);
                if (!string.IsNullOrWhiteSpace(jsonBody))
                {
                    var req = JsonSerializer.Deserialize(jsonBody, s_jsonContext.HttpOcrRequest);
                    if (req != null)
                    {
                        if (req.SplitColumns) splitColumns = true;

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
                        else if (!string.IsNullOrEmpty(req.ImagePath) && File.Exists(req.ImagePath))
                        {
                            imageBytes = await File.ReadAllBytesAsync(req.ImagePath, ct);
                        }
                    }
                }
            }
            else
            {
                // 直接传输图片二进制字节流 (e.g. image/png, application/octet-stream)
                using var ms = new MemoryStream();
                await context.Request.InputStream.CopyToAsync(ms, ct);
                if (ms.Length > 0)
                {
                    imageBytes = ms.ToArray();
                }
            }
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

        // 解码图片为 SKBitmap 并自动校正拍摄朝向
        SKBitmap? normalizedBitmap;
        try
        {
            normalizedBitmap = ImageOrientationHelper.DecodeAndOrient(imageBytes, ImageRotationMode.Auto);
        }
        catch (Exception ex)
        {
            await WriteJsonResponseAsync(context, 400, new HttpOcrResponse
            {
                Code = 400,
                Success = false,
                ErrorMessage = $"图片解码异常，请确认图像数据是否有效: {ex.Message}"
            });
            return (400, "解码异常");
        }

        if (normalizedBitmap == null)
        {
            await WriteJsonResponseAsync(context, 400, new HttpOcrResponse
            {
                Code = 400,
                Success = false,
                ErrorMessage = "图片解码失败，请确认图像数据是否有效 (JPEG/PNG/BMP/WebP/GIF)"
            });
            return (400, "解码失败");
        }

        using (normalizedBitmap)
        {
            using var ocrImage = new OcrImage(normalizedBitmap);

            // 通过公共调度器执行识别：自动并发核心排队与 CPU 占空比限流
            var ocrSw = Stopwatch.StartNew();
            var ocrResult = await scheduler.ExecuteAsync(async ctLocal =>
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
            }, ct);
            ocrSw.Stop();

            if (!ocrResult.IsSuccess)
            {
                await WriteJsonResponseAsync(context, 500, new HttpOcrResponse
                {
                    Code = 500,
                    Success = false,
                    ErrorMessage = ocrResult.ErrorMessage ?? "OCR 引擎识别失败"
                });
                return (500, ocrResult.ErrorMessage ?? "识别失败");
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
