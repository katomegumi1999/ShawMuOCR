namespace ShawMuOCR.Core.OCR;

public interface IOcrEngine : IDisposable
{
    string ModelName { get; }

    Task<OcrResult> RecognizeAsync(
        OcrImage image,
        CancellationToken cancellationToken = default);

    Task WarmupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
