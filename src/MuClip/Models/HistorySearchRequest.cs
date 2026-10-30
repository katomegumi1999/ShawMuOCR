using System.Text.Json.Serialization;

namespace MuClip.Models;

/// <summary>
/// 搜索请求（跨 FFI 传给 Rust 核心）。
/// 三个维度正交：关键词 / 内容类型白名单 / 分页，全部在 SQL 侧生效。
/// </summary>
internal sealed class HistorySearchRequest
{
    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;

    /// <summary>内容类型编号（与 Rust 侧 ContentType 落盘编号一致）；空数组表示不限。</summary>
    [JsonPropertyName("types")]
    public int[] Types { get; set; } = System.Array.Empty<int>();

    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    [JsonPropertyName("limit")]
    public int Limit { get; set; } = 200;
}
