namespace MuClip.Models;

public class ContentAnalysisResult
{
    public string ContentType { get; set; } = "plain_text";
    public int CharCount { get; set; }
    public int LineCount { get; set; }
    public int ByteCount { get; set; }
    public string Preview { get; set; } = string.Empty;
}
