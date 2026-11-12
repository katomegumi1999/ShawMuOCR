using ShawMuOCR.Core.Capture;

namespace ShawMuOCR.Core.OCR;

public sealed class OcrSession : IDisposable
{
    private bool _disposed;

    public OcrImage? Image { get; }
    public OcrResult? Result { get; }
    public ScreenRect? ScreenBounds { get; }
    public DateTime CreatedAt { get; } = DateTime.Now;

    public OcrSession(OcrImage? image, OcrResult? result, ScreenRect? screenBounds = null)
    {
        Image = image;
        Result = result;
        ScreenBounds = screenBounds;
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
