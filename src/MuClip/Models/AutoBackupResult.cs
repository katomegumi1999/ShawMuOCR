using System.Text.Json;

namespace MuClip.Models;

/// <summary>
/// 一次滚动自动备份的结果。由 Rust 侧的 <c>muclip_core_auto_backup</c> 以 JSON 信封返回。
/// </summary>
public sealed class AutoBackupResult
{
    /// <summary>本次是否真的生成了快照。</summary>
    public bool Created { get; init; }

    /// <summary>因距上次快照不足间隔而跳过。</summary>
    public bool Skipped { get; init; }

    /// <summary>失败原因（为空表示未失败）。</summary>
    public string? Error { get; init; }

    /// <summary>快照文件的完整路径。</summary>
    public string? Path { get; init; }

    /// <summary>快照文件字节数。</summary>
    public long Size { get; init; }

    /// <summary>本次清理掉的旧快照数量。</summary>
    public int Pruned { get; init; }

    /// <summary>清理后目录内保留的快照数量。</summary>
    public int Kept { get; init; }

    public static AutoBackupResult Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new AutoBackupResult { Error = "空响应" };
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement r = doc.RootElement;
            return new AutoBackupResult
            {
                Created = r.TryGetProperty("created", out JsonElement c) && c.ValueKind == JsonValueKind.True,
                Skipped = r.TryGetProperty("skipped", out JsonElement s) && s.ValueKind == JsonValueKind.True,
                Error = r.TryGetProperty("error", out JsonElement e) ? e.GetString() : null,
                Path = r.TryGetProperty("path", out JsonElement p) ? p.GetString() : null,
                Size = r.TryGetProperty("size", out JsonElement z) && z.TryGetInt64(out long zz) ? zz : 0,
                Pruned = r.TryGetProperty("pruned", out JsonElement a) && a.TryGetInt32(out int aa) ? aa : 0,
                Kept = r.TryGetProperty("kept", out JsonElement k) && k.TryGetInt32(out int kk) ? kk : 0,
            };
        }
        catch (Exception ex)
        {
            return new AutoBackupResult { Error = "解析失败: " + ex.Message };
        }
    }
}
