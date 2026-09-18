namespace ShawMuOCR.Core.OCR;

public sealed class OcrSession : IDisposable
{
    private bool _disposed;

    public OcrImage? Image { get; }
    public OcrResult? Result { get; }
    public DateTime CreatedAt { get; } = DateTime.Now;

    public OcrSession(OcrImage? image, OcrResult? result)
    {
        Image = image;
        Result = result;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Image?.Dispose();
            _disposed = true;
        }
    }
}
