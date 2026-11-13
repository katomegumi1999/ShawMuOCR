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
    Task StartQuickSaveScreenshotAsync();
    Task StartLongScreenshotAsync(ScreenRect? initialRegion = null);
    Task StartTranslateSelectionAsync();
    Task StartOcrPopupAsync();
    Task TranslateImageAsync(OcrImage image);
    Task RunOcrAsync(OcrImage image, ScreenRect? screenBounds = null);
    Task<OcrResult> RecognizeDirectAsync(OcrImage image);
    void PreloadEngineInBackground(string scene = "启动预载", int delayMs = 0);

    /// <summary>
    /// 丢弃当前会话（会话图像 + 识别结果）并把状态复位为 <see cref="OcrState.Idle"/>。
    /// 供「清空截屏工作区」使用：仅清 UI 侧位图缓存是不够的，
    /// 因为 <c>MainViewModel.GetCurrentSkBitmapCopy</c> 会在缓存为空时兜底回读会话图像，
    /// 不连带清掉会话就会表现为「点一次清空无效，点第二次才生效」。
    /// </summary>
    void ClearCurrentSession();
}
