using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ShawMuOCR.Services.Http;

/// <summary>
/// HTTP OCR 识别响应结果数据模型
/// </summary>
public sealed class HttpOcrResponse
{
    public int Code { get; set; } = 200;
    public bool Success { get; set; } = true;
    public string Text { get; set; } = string.Empty;
    public double ElapsedMs { get; set; }
    public List<HttpOcrBlockDto>? Blocks { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// 文本块与四边形坐标数据模型
/// </summary>
public sealed class HttpOcrBlockDto
{
    public string Text { get; set; } = string.Empty;
    public float Confidence { get; set; }
    public int[][]? Box { get; set; }
}

/// <summary>
/// HTTP 服务健康检查与状态响应
/// </summary>
public sealed class HttpOcrInfoResponse
{
    public int Code { get; set; } = 200;
    public string Status { get; set; } = "ok";
    public string Service { get; set; } = "ShawMuOCR HTTP API";
    public string Version { get; set; } = "1.0";
    public bool IsReady { get; set; } = true;
    public int MaxCores { get; set; }
    public int CpuLimitPercent { get; set; }
    public int ActiveTasks { get; set; }
    public int QueuedTasks { get; set; }
}

/// <summary>
/// JSON 形式请求体参数
/// </summary>
public sealed class HttpOcrRequest
{
    public string? Image { get; set; }
    public string? Base64 { get; set; }
    public string? ImageBase64 { get; set; }

    /// <summary>
    /// 【已禁用】原用于按服务端本地路径读取图片，等同于未授权的任意文件读取入口。
    /// 保留字段仅为向旧客户端返回明确的 400 提示，服务端不会再读取任何本地路径。
    /// </summary>
    public string? ImagePath { get; set; }

    public bool SplitColumns { get; set; }
}

/// <summary>
/// 请求体超过服务端硬上限时抛出（映射为 HTTP 413 Payload Too Large）
/// </summary>
public sealed class PayloadTooLargeException : Exception
{
    public long MaxBytes { get; }
    public long ReceivedBytes { get; }

    public PayloadTooLargeException(long maxBytes, long receivedBytes)
        : base($"请求体过大：服务端上限 {maxBytes / (1024 * 1024)} MB，当前已接收 {receivedBytes / (1024.0 * 1024.0):F1} MB。请压缩图片或降低分辨率后重试。")
    {
        MaxBytes = maxBytes;
        ReceivedBytes = receivedBytes;
    }
}

/// <summary>
/// HTTP API 实时调用审计日志模型
/// </summary>
public sealed class HttpApiLogItem
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string TimeText => Timestamp.ToString("HH:mm:ss");
    public string Method { get; set; } = "POST";
    public string Path { get; set; } = "/ocr";
    public string RemoteEndPoint { get; set; } = "127.0.0.1";
    public int StatusCode { get; set; } = 200;
    public double ElapsedMs { get; set; }
    public string Message { get; set; } = string.Empty;

    public bool IsSuccess => StatusCode >= 200 && StatusCode < 300;
    public string StatusBadgeColor => IsSuccess ? "#107C41" : "#D83B01";
}

/// <summary>
/// Native AOT 静态源码生成 JSON 序列化上下文
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HttpOcrResponse))]
[JsonSerializable(typeof(HttpOcrInfoResponse))]
[JsonSerializable(typeof(HttpOcrRequest))]
[JsonSerializable(typeof(HttpOcrBlockDto))]
public partial class HttpApiJsonContext : JsonSerializerContext
{
}
