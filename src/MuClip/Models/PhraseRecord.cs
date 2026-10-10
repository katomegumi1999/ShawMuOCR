namespace MuClip.Models;

public class PhraseRecord
{
    public long Id { get; set; }
    public string Content { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string LastUsedAt { get; set; } = string.Empty;
    public long SortOrder { get; set; }
    public string ContentType { get; set; } = "plain_text";
    public int CharCount { get; set; }
    public int LineCount { get; set; }
    public int ByteCount { get; set; }
    public string Preview { get; set; } = string.Empty;
}
