using SkiaSharp;

namespace ShawMuOCR.Core.OCR;

public sealed class OcrTextBlock
{
    public string Text { get; init; } = string.Empty;

    public float Confidence { get; init; }

    /// <summary>
    /// 文字框外接矩形 (X, Y, Width, Height)
    /// </summary>
    public SKRectI BoundingBox { get; init; }

    /// <summary>
    /// 文字多边形四个顶点 (顺时针顺序)
    /// </summary>
    public IReadOnlyList<SKPointI> BoxPoints { get; init; } = Array.Empty<SKPointI>();
}
