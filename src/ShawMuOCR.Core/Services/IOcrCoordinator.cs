using ShawMuOCR.Core.Capture;
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

    Action<ScreenRect>? RegionRecordingHandler { get; set; }

    event Action<OcrState, string?>? StateChanged;
    event Action<OcrSession>? SessionCompleted;

    Task StartScreenCaptureAsync();
    Task StartCaptureImageAsync();
    Task StartAnnotateScreenshotAsync();
    Task StartLongScreenshotAsync(ScreenRect? initialRegion = null);
    Task StartTranslateSelectionAsync();
    Task StartOcrPopupAsync();
    Task RunOcrAsync(OcrImage image);
    Task<OcrResult> RecognizeDirectAsync(OcrImage image);
}
