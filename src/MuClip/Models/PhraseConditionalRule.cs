using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MuClip.Models;

/// <summary>
/// 常用语动态条件追加规则（按当天时段在指定字符位置追加/插入文本）
/// </summary>
public partial class PhraseConditionalRule : ObservableObject
{
    private string _id = Guid.NewGuid().ToString("N");
    [JsonPropertyName("id")]
    public string Id
    {
        get => _id;
        set
        {
            if (SetProperty(ref _id, value))
            {
                OnPropertyChanged(nameof(IsCurrentlyActive));
            }
        }
    }

    [JsonPropertyName("phrase_id")]
    public long PhraseId { get; set; } = 0;

    private bool _isEnabled = true;
    [JsonPropertyName("is_enabled")]
    [JsonConverter(typeof(IntOrBoolJsonConverter))]
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                OnPropertyChanged(nameof(IsCurrentlyActive));
            }
        }
    }

    private int _insertPosition = 0;
    [JsonPropertyName("insert_position")]
    public int InsertPosition
    {
        get => _insertPosition;
        set
        {
            if (SetProperty(ref _insertPosition, value))
            {
                OnPropertyChanged(nameof(PositionSummaryText));
            }
        }
    }

    private int _positionMode = 1;
    [JsonPropertyName("position_mode")]
    public int PositionMode
    {
        get => _positionMode;
        set
        {
            if (SetProperty(ref _positionMode, value))
            {
                OnPropertyChanged(nameof(PositionSummaryText));
            }
        }
    }

    private string _startTime = "08:00";
    [JsonPropertyName("start_time")]
    public string StartTime
    {
        get => _startTime;
        set
        {
            if (SetProperty(ref _startTime, value))
            {
                OnPropertyChanged(nameof(TimeRangeText));
                OnPropertyChanged(nameof(IsCurrentlyActive));
            }
        }
    }

    private string _endTime = "18:00";
    [JsonPropertyName("end_time")]
    public string EndTime
    {
        get => _endTime;
        set
        {
            if (SetProperty(ref _endTime, value))
            {
                OnPropertyChanged(nameof(TimeRangeText));
                OnPropertyChanged(nameof(IsCurrentlyActive));
            }
        }
    }

    private string _appendText = string.Empty;
    [JsonPropertyName("append_text")]
    public string AppendText
    {
        get => _appendText;
        set
        {
            if (SetProperty(ref _appendText, value))
            {
                OnPropertyChanged(nameof(AppendTextDisplay));
            }
        }
    }

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; } = 0;

    [JsonIgnore]
    public string TimeRangeText => $"{StartTime} ~ {EndTime}";

    [JsonIgnore]
    public string PositionSummaryText => PositionMode == 0
        ? (InsertPosition == 0 ? "文本开头插入" : $"第 {InsertPosition} 字符前插入")
        : (InsertPosition >= 9999 ? "文本末尾追加" : $"第 {InsertPosition} 字符后追加");

    [JsonIgnore]
    public string AppendTextDisplay => string.IsNullOrEmpty(AppendText) ? "(无追加文字)" : $"追加: \"{AppendText}\"";

    [JsonIgnore]
    public bool IsCurrentlyActive => IsEnabled && IsTimeMatched();

    public void RefreshState()
    {
        OnPropertyChanged(nameof(TimeRangeText));
        OnPropertyChanged(nameof(PositionSummaryText));
        OnPropertyChanged(nameof(AppendTextDisplay));
        OnPropertyChanged(nameof(IsCurrentlyActive));
    }

    /// <summary>
    /// 判断指定时间是否命中生效时段
    /// </summary>
    public bool IsTimeMatched(DateTime? now = null)
    {
        if (!IsEnabled) return false;

        DateTime current = now ?? DateTime.Now;
        TimeOnly currentTime = TimeOnly.FromDateTime(current);

        if (!TimeOnly.TryParse(StartTime, out var start))
        {
            start = new TimeOnly(8, 0);
        }
        if (!TimeOnly.TryParse(EndTime, out var end))
        {
            end = new TimeOnly(18, 0);
        }

        return start <= end
            ? (currentTime >= start && currentTime <= end)
            : (currentTime >= start || currentTime <= end); // 跨午夜支持 (如 22:00 ~ 06:00)
    }

    /// <summary>
    /// 根据当前时间与单条规则，计算组装最终文本
    /// </summary>
    public string Apply(string content, DateTime? now = null)
    {
        if (string.IsNullOrEmpty(content) || !IsEnabled || string.IsNullOrEmpty(AppendText))
        {
            return content;
        }

        if (!IsTimeMatched(now))
        {
            return content;
        }

        int targetIndex;
        if (PositionMode == 0) // 在指定字符前插入
        {
            targetIndex = Math.Clamp(InsertPosition, 0, content.Length);
        }
        else // 在指定字符后追加
        {
            targetIndex = Math.Clamp(InsertPosition + 1, 0, content.Length);
        }

        return content.Insert(targetIndex, AppendText);
    }

    /// <summary>
    /// 强制应用单条规则（忽略当前时钟判定，专用于规则编辑时仅预览所选时段的独立效果）
    /// </summary>
    public string ApplyUnconditional(string content)
    {
        if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(AppendText))
        {
            return content;
        }

        int targetIndex;
        if (PositionMode == 0) // 在指定字符前插入
        {
            targetIndex = Math.Clamp(InsertPosition, 0, content.Length);
        }
        else // 在指定字符后追加
        {
            targetIndex = Math.Clamp(InsertPosition + 1, 0, content.Length);
        }

        return content.Insert(targetIndex, AppendText);
    }

    /// <summary>
    /// 依次应用多条规则 (真实运行时调用，严格检查当前时间是否命中生效时段)
    /// </summary>
    public static string ApplyRules(string content, IEnumerable<PhraseConditionalRule>? rules, DateTime? now = null)
    {
        if (string.IsNullOrEmpty(content) || rules == null) return content;
        string result = content;
        foreach (var rule in rules)
        {
            if (rule != null && rule.IsEnabled && rule.IsTimeMatched(now))
            {
                result = rule.Apply(result, now);
            }
        }
        return result;
    }

    /// <summary>
    /// 模拟应用所有启用规则（专用于最终结果实时预览，不受当前时钟限制）
    /// </summary>
    public static string SimulateApplyRules(string content, IEnumerable<PhraseConditionalRule>? rules)
    {
        if (string.IsNullOrEmpty(content) || rules == null) return content;
        string result = content;
        foreach (var rule in rules)
        {
            if (rule != null && rule.IsEnabled && !string.IsNullOrEmpty(rule.AppendText))
            {
                int targetIndex;
                if (rule.PositionMode == 0)
                {
                    targetIndex = Math.Clamp(rule.InsertPosition, 0, result.Length);
                }
                else
                {
                    targetIndex = Math.Clamp(rule.InsertPosition + 1, 0, result.Length);
                }
                result = result.Insert(targetIndex, rule.AppendText);
            }
        }
        return result;
    }

    public PhraseConditionalRule Clone() => new()
    {
        Id = Id,
        PhraseId = PhraseId,
        IsEnabled = IsEnabled,
        InsertPosition = InsertPosition,
        PositionMode = PositionMode,
        StartTime = StartTime,
        EndTime = EndTime,
        AppendText = AppendText,
        CreatedAt = CreatedAt
    };
}

/// <summary>
/// 常用语规则模板（保存预设规则供下次或跨常用语自定名称直接复用）
/// </summary>
public class PhraseRuleTemplate
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("insert_position")]
    public int InsertPosition { get; set; } = 0;

    [JsonPropertyName("position_mode")]
    public int PositionMode { get; set; } = 1;

    [JsonPropertyName("start_time")]
    public string StartTime { get; set; } = "08:00";

    [JsonPropertyName("end_time")]
    public string EndTime { get; set; } = "18:00";

    [JsonPropertyName("append_text")]
    public string AppendText { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; } = 0;

    [JsonIgnore]
    public string SummaryText => $"{StartTime}~{EndTime} | {(PositionMode == 0 ? $"第{InsertPosition}字前" : (InsertPosition >= 9999 ? "末尾" : $"第{InsertPosition}字后"))} | \"{AppendText}\"";

    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(Name) ? SummaryText : $"{Name} ({SummaryText})";

    public PhraseConditionalRule ToRule(long phraseId = 0) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        PhraseId = phraseId,
        IsEnabled = true,
        InsertPosition = InsertPosition,
        PositionMode = PositionMode,
        StartTime = StartTime,
        EndTime = EndTime,
        AppendText = AppendText,
        CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
    };

    public static PhraseRuleTemplate FromRule(PhraseConditionalRule rule, string name) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = name,
        InsertPosition = rule.InsertPosition,
        PositionMode = rule.PositionMode,
        StartTime = rule.StartTime,
        EndTime = rule.EndTime,
        AppendText = rule.AppendText,
        CreatedAt = rule.CreatedAt > 0 ? rule.CreatedAt : DateTimeOffset.UtcNow.ToUnixTimeSeconds()
    };

    public PhraseRuleTemplate Clone() => new()
    {
        Id = Id,
        Name = Name,
        InsertPosition = InsertPosition,
        PositionMode = PositionMode,
        StartTime = StartTime,
        EndTime = EndTime,
        AppendText = AppendText,
        CreatedAt = CreatedAt
    };
}

/// <summary>
/// 兼容数字 (1/0) 与布尔值 (true/false) 的 JSON 反序列化转换器
/// </summary>
public sealed class IntOrBoolJsonConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.True) return true;
        if (reader.TokenType == JsonTokenType.False) return false;
        if (reader.TokenType == JsonTokenType.Number) return reader.GetInt32() != 0;
        if (reader.TokenType == JsonTokenType.String && bool.TryParse(reader.GetString(), out bool b)) return b;
        return false;
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value ? 1 : 0);
    }
}

[JsonSerializable(typeof(PhraseConditionalRule))]
[JsonSerializable(typeof(List<PhraseConditionalRule>))]
[JsonSerializable(typeof(Dictionary<string, PhraseConditionalRule>))]
[JsonSerializable(typeof(Dictionary<string, List<PhraseConditionalRule>>))]
[JsonSerializable(typeof(PhraseRuleTemplate))]
[JsonSerializable(typeof(List<PhraseRuleTemplate>))]
[JsonSerializable(typeof(BackupSummary))]
[JsonSerializable(typeof(HistorySearchRequest))]
internal partial class PhraseRuleJsonContext : JsonSerializerContext
{
}

