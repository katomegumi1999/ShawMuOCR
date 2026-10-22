using System.Text.Json.Serialization;

namespace MuClip.Models;

/// <summary>
/// 数据库加密备份 / 还原统计信息摘要 (.mubc)
/// </summary>
public sealed class BackupSummary
{
    [JsonPropertyName("history_count")]
    public ulong HistoryCount { get; set; }

    [JsonPropertyName("phrase_count")]
    public ulong PhraseCount { get; set; }

    [JsonPropertyName("rule_count")]
    public ulong RuleCount { get; set; }

    [JsonPropertyName("template_count")]
    public ulong TemplateCount { get; set; }

    [JsonPropertyName("config_count")]
    public ulong ConfigCount { get; set; }
}
