namespace ShawMuOCR.Core.Ai;

/// <summary>
/// 统一的对话补全客户端。翻译、OCR 以及后续任何 AI 功能都通过它访问「API 池」里的某个节点。
/// 实现方负责协议、鉴权、重试与限流；调用方只关心"我要一段文字"。
/// </summary>
public interface ILlmClient
{
    /// <summary>目标节点（档案）Id</summary>
    string ProfileId { get; }

    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default);
}

public enum LlmErrorKind
{
    None,

    /// <summary>档案缺少密钥或模型名</summary>
    NotConfigured,

    /// <summary>鉴权失败（401 / 403）</summary>
    Unauthorized,

    /// <summary>被限流（429）</summary>
    RateLimited,

    /// <summary>其它 HTTP 错误</summary>
    Http,

    /// <summary>请求超时</summary>
    Timeout,

    /// <summary>调用方取消</summary>
    Cancelled,

    /// <summary>返回内容无法解析</summary>
    BadResponse,

    /// <summary>该节点的月度额度已用尽</summary>
    QuotaExceeded
}

public sealed record LlmRequest
{
    /// <summary>用户消息正文（OCR 时为识别提示词，翻译时为待译文本所在指令）</summary>
    public string Prompt { get; init; } = string.Empty;

    /// <summary>系统消息（可选）</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>随请求发送的 JPEG 图片（可选，仅视觉模型）</summary>
    public byte[]? ImageJpeg { get; init; }

    public double Temperature { get; init; } = 0.3;
}

public sealed record LlmResponse
{
    public bool IsSuccess { get; init; }

    public string Content { get; init; } = string.Empty;

    public int TotalTokens { get; init; }

    public TimeSpan Elapsed { get; init; }

    public LlmErrorKind ErrorKind { get; init; }

    public string? ErrorMessage { get; init; }

    public static LlmResponse Ok(string content, int totalTokens, TimeSpan elapsed) => new()
    {
        IsSuccess = true,
        Content = content,
        TotalTokens = totalTokens,
        Elapsed = elapsed
    };

    public static LlmResponse Fail(LlmErrorKind kind, string message, TimeSpan elapsed = default) => new()
    {
        IsSuccess = false,
        ErrorKind = kind,
        ErrorMessage = message,
        Elapsed = elapsed
    };
}
