namespace ShawMuOCR.Core.OCR;

public sealed class OcrResult
{
    public string Text { get; init; } = string.Empty;

    public IReadOnlyList<OcrTextBlock> Blocks { get; init; } = Array.Empty<OcrTextBlock>();

    public TimeSpan Elapsed { get; init; }

    public bool IsSuccess { get; init; } = true;

    public string? ErrorMessage { get; init; }

    public static OcrResult Failed(string errorMessage, TimeSpan elapsed = default) => new()
    {
        IsSuccess = false,
        ErrorMessage = errorMessage,
        Elapsed = elapsed,
        Text = string.Empty,
        Blocks = Array.Empty<OcrTextBlock>()
    };

    public static OcrResult Empty(TimeSpan elapsed = default) => new()
    {
        IsSuccess = true,
        Elapsed = elapsed,
        Text = string.Empty,
        Blocks = Array.Empty<OcrTextBlock>()
    };
}
