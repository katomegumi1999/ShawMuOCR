using CommunityToolkit.Mvvm.ComponentModel;

namespace MuClip.Models;

public partial class MuClipItem : ObservableObject
{
    public long Id { get; init; }
    public string Content { get; init; } = string.Empty;
    public string Preview { get; init; } = string.Empty;
    public string ContentType { get; init; } = "plain_text";
    public int CharCount { get; init; }
    public int LineCount { get; init; }
    public int ByteCount { get; init; }
    public string CreatedAt { get; init; } = string.Empty;
    public string FormattedTime { get; init; } = string.Empty;
    public string TimeAndCharCount => $"{FormattedTime}  ·  {CharCount} 字";
    public string SourceDevice { get; init; } = "本机";

    [ObservableProperty]
    private bool _isCopied;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ItemOpacity))]
    private bool _isDragging;

    public double ItemOpacity => IsDragging ? 0.45 : 1.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavoriteIcon))]
    [NotifyPropertyChangedFor(nameof(FavoriteColor))]
    [NotifyPropertyChangedFor(nameof(FavoriteOpacity))]
    private bool _isFavorite;

    public string FavoriteIcon => IsFavorite ? "★" : "☆";
    public string FavoriteColor => IsFavorite ? "#F59E0B" : "#888888";
    public double FavoriteOpacity => IsFavorite ? 1.0 : 0.45;

    public string ContentTypeBadge => ContentType switch
    {
        "url" => "🌐 链接",
        "email" => "✉️ 邮箱",
        "json" => "{ } JSON",
        "code" => "💻 代码",
        "color" or "hex_color" => "🎨 颜色",
        "number" => "🔢 数值",
        _ => "📝 纯文本"
    };

    public string ContentTypeColor => ContentType switch
    {
        "url" => "#007ACC",
        "email" => "#0284C7",
        "json" => "#E08000",
        "code" => "#8B5CF6",
        "color" or "hex_color" => "#10B981",
        "number" => "#6366F1",
        _ => "#6B7280"
    };

    public static MuClipItem FromRecord(HistoryRecord rec, ContentAnalysisResult? analysis = null, string sourceDevice = "本机")
    {
        string ctype = !string.IsNullOrWhiteSpace(analysis?.ContentType)
            ? analysis.ContentType
            : (!string.IsNullOrWhiteSpace(rec.ContentType) ? rec.ContentType : "plain_text");
        int chars = analysis?.CharCount > 0
            ? analysis.CharCount
            : (rec.CharCount > 0 ? rec.CharCount : rec.Content.Length);
        int lines = analysis?.LineCount > 0
            ? analysis.LineCount
            : (rec.LineCount > 0 ? rec.LineCount : Math.Max(1, rec.Content.Split('\n').Length));
        int bytes = analysis?.ByteCount > 0
            ? analysis.ByteCount
            : (rec.ByteCount > 0 ? rec.ByteCount : System.Text.Encoding.UTF8.GetByteCount(rec.Content));
        string preview = !string.IsNullOrWhiteSpace(analysis?.Preview)
            ? analysis.Preview
            : (!string.IsNullOrWhiteSpace(rec.Preview)
                ? rec.Preview
                : (rec.Content.Length > 80 ? rec.Content[..80].Replace("\r", " ").Replace("\n", " ") + "..." : rec.Content.Replace("\r", " ").Replace("\n", " ")));

        string timeStr = rec.CreatedAt;
        if (DateTime.TryParse(rec.CreatedAt, out var dt))
        {
            timeStr = dt.ToString("yyyy-MM-dd HH:mm:ss");
        }
        else if (long.TryParse(rec.CreatedAt, out var epochSec) && epochSec > 0)
        {
            timeStr = DateTimeOffset.FromUnixTimeSeconds(epochSec).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        }

        return new MuClipItem
        {
            Id = rec.Id,
            Content = rec.Content,
            Preview = preview,
            ContentType = ctype,
            CharCount = chars,
            LineCount = lines,
            ByteCount = bytes,
            CreatedAt = rec.CreatedAt,
            FormattedTime = timeStr,
            SourceDevice = sourceDevice
        };
    }
}
