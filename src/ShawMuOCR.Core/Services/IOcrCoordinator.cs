using ShawMuOCR.Core.OCR;

namespace ShawMuOCR.Core.Services;

public enum OcrState
{
    Idle,
    Selecting,
    Recognizing,
    Success,
    Failed
}

public interface IOcrCoordinator : IDisposable
{
    OcrState CurrentState { get; }
    OcrSession? CurrentSession { get; }
    string? StatusMessage { get; }

    event Action<OcrState, string?>? StateChanged;
    event Action<OcrSession>? SessionCompleted;

    Task StartScreenCaptureAsync();
    Task StartCaptureImageAsync();
    Task StartAnnotateScreenshotAsync();
    Task StartTranslateSelectionAsync();
    Task RunOcrAsync(OcrImage image);
    Task<OcrResult> RecognizeDirectAsync(OcrImage image);
}
