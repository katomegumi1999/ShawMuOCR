using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ShawMuOCR.Core.Settings;

namespace ShawMuOCR.Core.Ai;

/// <summary>
/// OpenAI 兼容协议的通用客户端（文本 + 视觉）。
///
/// 三个实现要点：
/// 1. HttpClient 静态共享：避免每个节点/每次创建各建一个导致 handler 泄漏；
///    超时改由每次请求的 CTS 控制，才能按档案动态调整。
/// 2. 限流信号量按「档案 Id」静态共享：批量 OCR 会按 CPU 核数创建多个引擎实例，
///    若信号量挂在实例上，实际并发就会放大到「实例数 × 上限」，直接撞供应商 429。
/// 3. 请求体手写 Utf8JsonWriter（AOT 安全）+ UnsafeRelaxedJsonEscaping：
///    默认编码器会把中文提示词转成 \uXXXX，体积膨胀数倍。
/// </summary>
public sealed class OpenAiCompatibleClient : ILlmClient
{
    private static readonly HttpClient s_http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly ConcurrentDictionary<string, (SemaphoreSlim Gate, int Limit)> s_gates = new(StringComparer.Ordinal);

    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly int _timeoutSeconds;
    private readonly int _maxConcurrency;

    public string ProfileId { get; }

    public OpenAiCompatibleClient(
        string profileId,
        string baseUrl,
        string apiKey,
        string model,
        int timeoutSeconds = 60,
        int maxConcurrency = 3)
    {
        ProfileId = profileId;
        _baseUrl = baseUrl;
        _apiKey = apiKey;
        _model = model;
        _timeoutSeconds = Math.Clamp(timeoutSeconds, 10, 300);
        _maxConcurrency = Math.Clamp(maxConcurrency, 1, 8);
    }

    public static OpenAiCompatibleClient FromProfile(AiApiProfile profile, int timeoutSeconds = 60, int maxConcurrency = 3)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new OpenAiCompatibleClient(
            profile.Id,
            profile.BaseUrl,
            profile.ApiKey,
            profile.Model,
            timeoutSeconds,
            maxConcurrency);
    }

    private static SemaphoreSlim GetGate(string profileId, int limit)
    {
        var entry = s_gates.GetOrAdd(profileId, _ => (new SemaphoreSlim(limit, limit), limit));
        if (entry.Limit != limit)
        {
            // 上限变化时换新实例；旧实例不 Dispose，在途请求仍持有其租约
            var replacement = (new SemaphoreSlim(limit, limit), limit);
            entry = s_gates.AddOrUpdate(profileId, replacement, (_, _) => replacement);
        }
        return entry.Gate;
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return LlmResponse.Fail(LlmErrorKind.NotConfigured, "该节点未配置 API Key");
        }
        if (string.IsNullOrWhiteSpace(_model))
        {
            return LlmResponse.Fail(LlmErrorKind.NotConfigured, "该节点未配置模型名称");
        }

        var sw = Stopwatch.StartNew();
        string requestJson = BuildRequestJson(request);
        string endpoint = BuildEndpoint(_baseUrl);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        var gate = GetGate(ProfileId, _maxConcurrency);
        string? lastError = null;

        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                await gate.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                try
                {
                    using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                    using var reqMsg = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
                    reqMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey.Trim());

                    using var response = await s_http.SendAsync(reqMsg, timeoutCts.Token).ConfigureAwait(false);
                    string body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

                    if (response.IsSuccessStatusCode)
                    {
                        string text = ParseContent(body);
                        int tokens = ParseTotalTokens(body);
                        return LlmResponse.Ok(text, tokens, sw.Elapsed);
                    }

                    string apiMessage = ExtractErrorMessage(body);
                    string detail = apiMessage.Length > 0 ? apiMessage : $"HTTP {(int)response.StatusCode}";
                    lastError = $"AI 接口返回 {(int)response.StatusCode}: {detail}";

                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    {
                        return LlmResponse.Fail(LlmErrorKind.Unauthorized, lastError, sw.Elapsed);
                    }

                    bool retriable = response.StatusCode == HttpStatusCode.TooManyRequests
                                     || (int)response.StatusCode >= 500;

                    if (!retriable || attempt == 1)
                    {
                        var kind = response.StatusCode == HttpStatusCode.TooManyRequests
                            ? LlmErrorKind.RateLimited
                            : LlmErrorKind.Http;
                        return LlmResponse.Fail(kind, lastError, sw.Elapsed);
                    }
                }
                finally
                {
                    gate.Release();
                }

                await Task.Delay(TimeSpan.FromMilliseconds(1200), timeoutCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 只有「本地超时」才走到这里：调用方主动取消时下面那个 catch 优先
            return LlmResponse.Fail(
                LlmErrorKind.Timeout,
                $"AI 接口超过 {_timeoutSeconds} 秒未响应，可换更快的节点或在设置里调大超时",
                sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            return LlmResponse.Fail(LlmErrorKind.Cancelled, "AI 接口调用已取消", sw.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            return LlmResponse.Fail(
                LlmErrorKind.Http,
                $"无法连接 AI 接口，请检查地址与网络：{ex.Message}",
                sw.Elapsed);
        }

        return LlmResponse.Fail(LlmErrorKind.Http, lastError ?? "AI 接口调用失败", sw.Elapsed);
    }

    private string BuildRequestJson(LlmRequest request)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions
               {
                   Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
               }))
        {
            writer.WriteStartObject();
            writer.WriteString("model", _model.Trim());
            writer.WriteNumber("temperature", request.Temperature);

            writer.WriteStartArray("messages");

            if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            {
                writer.WriteStartObject();
                writer.WriteString("role", "system");
                writer.WriteString("content", request.SystemPrompt);
                writer.WriteEndObject();
            }

            writer.WriteStartObject();
            writer.WriteString("role", "user");

            if (request.ImageJpeg is { Length: > 0 })
            {
                writer.WriteStartArray("content");

                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", request.Prompt);
                writer.WriteEndObject();

                writer.WriteStartObject();
                writer.WriteString("type", "image_url");
                writer.WriteStartObject("image_url");
                writer.WriteString("url", "data:image/jpeg;base64," + Convert.ToBase64String(request.ImageJpeg));
                writer.WriteEndObject();
                writer.WriteEndObject();

                writer.WriteEndArray();
            }
            else
            {
                writer.WriteString("content", request.Prompt);
            }

            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static string BuildEndpoint(string? baseUrl)
    {
        string endpoint = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.openai.com/v1" : baseUrl.Trim();
        endpoint = endpoint.TrimEnd('/');
        if (!endpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            endpoint += "/chat/completions";
        }
        return endpoint;
    }

    /// <summary>
    /// 提取 choices[0].message.content。不取 reasoning_content ——
    /// 那是模型的思考过程，写进导出文件会污染结果。
    /// </summary>
    public static string ParseContent(string responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson)) return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            var first = choices.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Undefined ||
                !first.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("content", out var content))
            {
                return string.Empty;
            }

            string raw = content.ValueKind switch
            {
                JsonValueKind.String => content.GetString() ?? string.Empty,
                JsonValueKind.Array => string.Concat(
                    content.EnumerateArray()
                           .Where(p => p.ValueKind == JsonValueKind.Object && p.TryGetProperty("text", out _))
                           .Select(p => p.GetProperty("text").GetString() ?? string.Empty)),
                _ => string.Empty
            };

            return StripCodeFence(raw).Trim();
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    public static int ParseTotalTokens(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("usage", out var usage) &&
                usage.TryGetProperty("total_tokens", out var total) &&
                total.TryGetInt32(out int value))
            {
                return value;
            }
        }
        catch (JsonException)
        {
            // usage 缺失不影响结果
        }
        return 0;
    }

    /// <summary>
    /// 剥掉模型自作主张包上的 Markdown 代码块围栏。提示词里已明确禁止，
    /// 但小模型仍会偶发添加，直接落盘会让 .txt 里多出 ``` 行。
    /// </summary>
    private static string StripCodeFence(string text)
    {
        string trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;

        int firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0) return trimmed;

        string body = trimmed[(firstNewline + 1)..];
        int closing = body.LastIndexOf("```", StringComparison.Ordinal);
        return closing >= 0 ? body[..closing].TrimEnd() : body.TrimEnd();
    }

    private static string ExtractErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("message", out var msg))
                {
                    return msg.GetString() ?? string.Empty;
                }
                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString() ?? string.Empty;
                }
            }
        }
        catch (JsonException)
        {
            // 非 JSON 响应，交由调用方按状态码报告
        }
        return string.Empty;
    }
}
