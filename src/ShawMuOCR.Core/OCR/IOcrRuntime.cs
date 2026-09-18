namespace ShawMuOCR.Core.OCR;

public readonly record struct OcrInput(OcrImage Image, object? Options = null);

public sealed class OcrRuntimeResult
{
    public IReadOnlyList<OcrTextBlock> Blocks { get; init; } = Array.Empty<OcrTextBlock>();
    public TimeSpan Elapsed { get; init; }
}

public interface IOcrRuntime : IDisposable
{
    string RuntimeName { get; }

    Task<OcrRuntimeResult> RunAsync(
        OcrInput input,
        CancellationToken cancellationToken = default);
}
