using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MuClip.Models;

public partial class MuClipPhraseItem : ObservableObject
{
    public long Id { get; init; }
    public string Content { get; init; } = string.Empty;
    public string Preview { get; init; } = string.Empty;
    public string ContentType { get; init; } = "plain_text";
    public string CreatedAt { get; init; } = string.Empty;
    public string LastUsedAt { get; init; } = string.Empty;
    public long SortOrder { get; init; }
    public string FormattedTime { get; init; } = string.Empty;
    public int CharCount { get; init; }
    public int LineCount { get; init; }
    public int ByteCount { get; init; }
    public string TimeAndCharCount => $"{FormattedTime}  ·  {CharCount} 字";

    [ObservableProperty]
    private bool _isCopied;

    public static MuClipPhraseItem FromRecord(PhraseRecord rec)
    {
        string ctype = !string.IsNullOrWhiteSpace(rec.ContentType) ? rec.ContentType : "plain_text";
        int chars = rec.CharCount > 0 ? rec.CharCount : rec.Content.Length;
        int lines = rec.LineCount > 0 ? rec.LineCount : Math.Max(1, rec.Content.Split('\n').Length);
        int bytes = rec.ByteCount > 0 ? rec.ByteCount : System.Text.Encoding.UTF8.GetByteCount(rec.Content);
        string preview = !string.IsNullOrWhiteSpace(rec.Preview)
            ? rec.Preview
            : (rec.Content.Length > 80
                ? rec.Content[..80].Replace("\r", " ").Replace("\n", " ") + "..."
                : rec.Content.Replace("\r", " ").Replace("\n", " "));

        string timeStr = rec.CreatedAt;
        if (DateTime.TryParse(rec.CreatedAt, out var dt))
        {
            timeStr = dt.ToString("yyyy-MM-dd HH:mm:ss");
        }
        else if (long.TryParse(rec.CreatedAt, out var epochSec) && epochSec > 0)
        {
            timeStr = DateTimeOffset.FromUnixTimeSeconds(epochSec).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        }
        else if (!string.IsNullOrWhiteSpace(rec.LastUsedAt) && DateTime.TryParse(rec.LastUsedAt, out var dtUsed))
        {
            timeStr = dtUsed.ToString("yyyy-MM-dd HH:mm:ss");
        }
        else
        {
            timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }

        return new MuClipPhraseItem
        {
            Id = rec.Id,
            Content = rec.Content,
            Preview = preview,
            ContentType = ctype,
            CreatedAt = rec.CreatedAt,
            LastUsedAt = rec.LastUsedAt,
            SortOrder = rec.SortOrder,
            FormattedTime = timeStr,
            CharCount = chars,
            LineCount = lines,
            ByteCount = bytes
        };
    }
}
