using System.Diagnostics;
using Avalonia.Threading;
using ShawMuOCR.Core.Capture;
using ShawMuOCR.Core.Clipboard;
using ShawMuOCR.Core.Logging;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Services;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.Core.Translation;
using ShawMuOCR.Views;

namespace ShawMuOCR.Services;

public sealed class OcrCoordinator : IOcrCoordinator
{
    private readonly IScreenCaptureService _captureService;
    private readonly IScreenSelectionService _selectionService;
    private readonly IOcrEngineFactory _engineFactory;
    private readonly IClipboardService _clipboardService;
    private readonly ISettingsService _settingsService;
    private readonly ITranslationService _translationService;
    private readonly ILoggerService? _logger;

    private readonly object _stateLock = new();
    private IOcrEngine _ocrEngine;
    private OcrSession? _currentSession;
    private OcrState _currentState = OcrState.Idle;
    private string? _statusMessage = "就绪";
    private bool _disposed;
    private TranslateFloatingWindow? _activeTranslateWindow;
    private CancellationTokenSource? _translateCts;
    private string _lastTranslateOcrText = string.Empty;

    public OcrState CurrentState => _currentState;
    public OcrSession? CurrentSession => _currentSession;
    public string? StatusMessage => _statusMessage;
    public ITranslationService TranslationService => _translationService;

    public event Action<OcrState, string?>? StateChanged;
    public event Action<OcrSession>? SessionCompleted;
    public event Action? RequestShowMainWindow;

    public OcrCoordinator(
        IScreenCaptureService captureService,
        IOcrEngineFactory engineFactory,
        IClipboardService clipboardService,
        ISettingsService settingsService,
        IScreenSelectionService? selectionService = null,
        ILoggerService? logger = null,
        ITranslationService? translationService = null)
    {
        _captureService = captureService;
        _selectionService = selectionService ?? new AvaloniaScreenSelectionFallback();
        _engineFactory = engineFactory;
        _clipboardService = clipboardService;
        _settingsService = settingsService;
        _logger = logger;
        _translationService = translationService ?? new TranslationService(settingsService);

        // 默认初始化当前模型
        _cachedSettings = _settingsService.Current.Clone();
        _ocrEngine = _engineFactory.Create(_cachedSettings);

        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    private AppSettings _cachedSettings;

    private void OnSettingsChanged(AppSettings settings)
    {
        lock (_stateLock)
        {
            if (_cachedSettings.ScanExternalModels != settings.ScanExternalModels ||
                _cachedSettings.Model != settings.Model ||
                _cachedSettings.UseExternalModel != settings.UseExternalModel ||
                _cachedSettings.EmbeddedModel != settings.EmbeddedModel ||
                _cachedSettings.ExternalModelType != settings.ExternalModelType ||
                _cachedSettings.ExternalModelPath != settings.ExternalModelPath ||
                _cachedSettings.ExternalApiUrl != settings.ExternalApiUrl ||
                _cachedSettings.EnableAngleDetection != settings.EnableAngleDetection ||
                _cachedSettings.MaxSideLen != settings.MaxSideLen)
            {
                try
                {
                    var newEngine = _engineFactory.Create(settings);
                    _ocrEngine.Dispose();
                    _ocrEngine = newEngine;
                    _cachedSettings = settings.Clone();
                }
                catch (Exception ex)
                {
                    _logger?.Error("OCR 模型加载失败", ex);
                    // 模型加载失败时维持原引擎
                }
            }
        }
    }

    public async Task StartScreenCaptureAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_stateLock)
        {
            // 并发控制：如果正在框选或正在识别中，则忽略
            if (_currentState is OcrState.Selecting or OcrState.Recognizing)
            {
                return;
            }
            SetState(OcrState.Selecting, "正在框选截屏区域...");
        }

        try
        {
            // 立即销毁未消失的气泡通知，避免二次截屏时被截入画面或阻挡框选视线
            ToastNotificationWindow.Dismiss();

            // 1. 抓取物理全屏（包括所有显示器）
            var virtualBounds = _captureService.GetVirtualScreenBounds();
            var fullScreenshot = _captureService.CaptureVirtualScreen();

            // 2. 唤起全屏选择遮罩交互（Win32 原生极速或 Avalonia 回退）
            var cropImage = await _selectionService.SelectRegionAsync(fullScreenshot, virtualBounds);

            // 3. 释放全屏快照原图（由于已获得裁剪后的局部图，原图不再保留）
            fullScreenshot.Dispose();

            if (cropImage == null)
            {
                // 用户按 ESC 或取消了截屏
                SetState(OcrState.Idle, "截屏已取消");
                return;
            }

            // 4. 执行 OCR 识别
            await RunOcrAsync(cropImage);
        }
        catch (Exception ex)
        {
            _logger?.Error("屏幕截取流程失败", ex);
            SetState(OcrState.Failed, $"截屏错误: {ex.Message}");
        }
    }

    public async Task StartCaptureImageAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_stateLock)
        {
            if (_currentState is OcrState.Selecting or OcrState.Recognizing)
            {
                return;
            }
            SetState(OcrState.Selecting, "正在框选截屏区域...");
        }

        try
        {
            // 立即销毁未消失的气泡通知，避免二次截屏时被截入画面或阻挡框选视线
            ToastNotificationWindow.Dismiss();

            // 1. 抓取物理全屏（包括所有显示器）
            var virtualBounds = _captureService.GetVirtualScreenBounds();
            var fullScreenshot = _captureService.CaptureVirtualScreen();

            // 2. 唤起全屏选择遮罩交互
            var cropImage = await _selectionService.SelectRegionAsync(fullScreenshot, virtualBounds);

            fullScreenshot.Dispose();

            if (cropImage == null)
            {
                SetState(OcrState.Idle, "截屏已取消");
                return;
            }

            // 3. 将截取的图片直接复制到系统剪贴板
            bool copied = await _clipboardService.SetImageAsync(cropImage);

            // 4. 更新当前会话为图片会话（空 OCR 结果）供主窗口预览查看
            _currentSession?.Dispose();
            _currentSession = new OcrSession(cropImage, OcrResult.Empty());

            string status = copied ? "✓ 截图已复制到剪贴板" : "✕ 截图复制到剪贴板失败";
            SetState(copied ? OcrState.Success : OcrState.Failed, status);

            if (_settingsService.Current.ShowCaptureImageNotification)
            {
                ToastNotificationWindow.Show(
                    copied ? "图片已复制到剪贴板，可直接在软件中 Ctrl+V 粘贴" : "无法写入系统剪贴板",
                    TimeSpan.Zero,
                    copied ? "截图已复制到剪贴板" : "截图复制失败");
            }

            SessionCompleted?.Invoke(_currentSession);

            // 截图完成后轻量回收临时内存（涵盖 Gen 2 及 LOH 托管资源）
            GC.Collect(2, GCCollectionMode.Optimized, false, false);
        }
        catch (Exception ex)
        {
            _logger?.Error("截图复制流程失败", ex);
            SetState(OcrState.Failed, $"截屏错误: {ex.Message}");
        }
    }

    public async Task StartAnnotateScreenshotAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_stateLock)
        {
            if (_currentState is OcrState.Selecting or OcrState.Recognizing)
            {
                return;
            }
            SetState(OcrState.Selecting, "正在框选截屏区域...");
        }

        try
        {
            // 立即销毁未消失的气泡通知，避免二次截屏时被截入画面或阻挡框选视线
            ToastNotificationWindow.Dismiss();

            // 1. 抓取物理全屏（包括所有显示器）
            var virtualBounds = _captureService.GetVirtualScreenBounds();
            var fullScreenshot = _captureService.CaptureVirtualScreen();

            // 2. 唤起全屏选择遮罩交互，同时捕获裁剪图像与物理选区坐标
            var selection = await _selectionService.SelectRegionExAsync(fullScreenshot, virtualBounds);

            fullScreenshot.Dispose();

            if (selection == null)
            {
                SetState(OcrState.Idle, "截屏已取消");
                return;
            }

            SetState(OcrState.Idle, "截图标注已打开");

            // 3. 在 UI 线程打开截图标注编辑器（同时支持直接 OCR 与一键翻译）
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var editor = new ScreenshotEditorWindow(
                    selection.Image,
                    _clipboardService,
                    async img => await RunOcrAsync(img),
                    async (img, bounds) => await PushTranslationPipelineAsync(img, bounds),
                    _settingsService,
                    _logger,
                    selection.ScreenBounds);
                editor.Show();
            });
        }
        catch (Exception ex)
        {
            _logger?.Error("截图标注流程失败", ex);
            SetState(OcrState.Failed, $"截屏标注错误: {ex.Message}");
        }
    }

    public async Task StartTranslateSelectionAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_stateLock)
        {
            if (_currentState is OcrState.Selecting or OcrState.Recognizing)
            {
                return;
            }
            SetState(OcrState.Selecting, "正在框选翻译区域...");
        }

        try
        {
            // 立即销毁未消失的气泡通知，避免二次截屏时被截入画面或阻挡框选视线
            ToastNotificationWindow.Dismiss();

            // 1. 抓取物理全屏
            var virtualBounds = _captureService.GetVirtualScreenBounds();
            var fullScreenshot = _captureService.CaptureVirtualScreen();

            // 2. 唤起全屏遮罩并获得选区物理边界
            var selection = await _selectionService.SelectRegionExAsync(fullScreenshot, virtualBounds);

            fullScreenshot.Dispose();

            if (selection == null)
            {
                SetState(OcrState.Idle, "选区翻译已取消");
                return;
            }

            SetState(OcrState.Idle, "选区翻译已就绪");

            // 3. 执行数据流水线推送：浮窗 UI 保留不销毁重建，仅重新推送数据并重置位置
            await PushTranslationPipelineAsync(selection.Image, selection.ScreenBounds);

            GC.Collect(2, GCCollectionMode.Optimized, false, false);
        }
        catch (Exception ex)
        {
            _logger?.Error("选区翻译流程失败", ex);
            SetState(OcrState.Failed, $"选区翻译错误: {ex.Message}");
        }
    }

    public async Task RunOcrAsync(OcrImage image)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(image);

        lock (_stateLock)
        {
            SetState(OcrState.Recognizing, "正在识别文字...");
        }

        try
        {
            // 执行后台推理
            var result = await Task.Run(async () =>
            {
                return await _ocrEngine.RecognizeAsync(image);
            });

            // 释放旧会话中的图片
            _currentSession?.Dispose();
            _currentSession = new OcrSession(image, result);

            if (result.IsSuccess)
            {
                bool copied = false;
                if (_settingsService.Current.AutoCopy && !string.IsNullOrWhiteSpace(result.Text))
                {
                    copied = await _clipboardService.SetTextAsync(result.Text);
                }

                string status = copied
                    ? $"✓ 已自动复制 (耗时 {result.Elapsed.TotalMilliseconds:F0} ms)"
                    : $"✓ 识别完成 (耗时 {result.Elapsed.TotalMilliseconds:F0} ms)";

                SetState(OcrState.Success, status);

                // 气泡提醒：如果在设置中开启，且识别成功，在屏幕底部中间弹出轻量气泡卡片
                if (_settingsService.Current.ShowOcrNotification)
                {
                    ToastNotificationWindow.Show(result.Text, result.Elapsed);
                }
            }
            else
            {
                _logger?.Warn($"OCR 文字识别返回失败: {result.ErrorMessage}");
                SetState(OcrState.Failed, $"✕ 识别失败: {result.ErrorMessage}");
            }

            SessionCompleted?.Invoke(_currentSession);

            // 静默模式控制：开启静默模式后不再弹出主界面
            bool shouldShowWindow = !_settingsService.Current.SilentMode && _settingsService.Current.ShowWindowAfterOcr;
            if (shouldShowWindow && RequestShowMainWindow != null)
            {
                try
                {
                    Dispatcher.UIThread.Post(() => RequestShowMainWindow?.Invoke());
                }
                catch
                {
                    RequestShowMainWindow?.Invoke();
                }
            }

            // 识别完成后轻量回收临时内存，防止多轮截屏内存堆积（涵盖 Gen 2 及 LOH 托管资源）
            GC.Collect(2, GCCollectionMode.Optimized, false, false);
        }
        catch (Exception ex)
        {
            _logger?.Error("OCR 运行流程异常", ex);
            SetState(OcrState.Failed, $"✕ OCR 异常: {ex.Message}");
        }
    }

    public async Task<OcrResult> RecognizeDirectAsync(OcrImage image)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(image);

        return await Task.Run(async () =>
        {
            return await _ocrEngine.RecognizeAsync(image);
        });
    }

    private void SetState(OcrState state, string message)
    {
        _currentState = state;
        _statusMessage = message;
        StateChanged?.Invoke(state, message);
    }

    #region 随动翻译浮窗数据流水线管理 (UI 与数据解耦)

    private TranslateFloatingWindow EnsureTranslateWindow()
    {
        if (_activeTranslateWindow == null || !_activeTranslateWindow.IsVisible)
        {
            _activeTranslateWindow = new TranslateFloatingWindow(
                _settingsService,
                _clipboardService,
                _translationService);

            _activeTranslateWindow.RetryRequested += OnTranslateWindowRetryRequested;
            _activeTranslateWindow.Closed += (_, _) =>
            {
                _activeTranslateWindow = null;
                CancelTranslatePipeline();
            };

            _activeTranslateWindow.Show();
        }
        return _activeTranslateWindow;
    }

    private async Task PushTranslationPipelineAsync(OcrImage image, ScreenRect bounds)
    {
        CancelTranslatePipeline();
        _translateCts = new CancellationTokenSource();
        var ct = _translateCts.Token;

        // 无论浮窗是否已存在，均就地复用。
        // 若首次打开则计算选区旁边初始位置；若浮窗已在屏幕上，则保留用户当前位置，绝不重新位移！
        TranslateFloatingWindow win = null!;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            bool isNewWindow = (_activeTranslateWindow == null || !_activeTranslateWindow.IsVisible);
            win = EnsureTranslateWindow();
            if (isNewWindow)
            {
                win.InitializePosition(bounds);
            }
            else
            {
                win.BringToFront();
            }
            win.ShowLoading("正在 OCR 识别文字...");
            win.SetSourceText(string.Empty);
        });

        var totalSw = Stopwatch.StartNew();
        try
        {
            // 1. OCR 识别数据提取（提取完成后立刻释放图片，内存 0 泄露）
            OcrResult ocrResult;
            try
            {
                ocrResult = await _ocrEngine.RecognizeAsync(image);
            }
            finally
            {
                image.Dispose();
            }

            if (ct.IsCancellationRequested) return;

            if (!ocrResult.IsSuccess || string.IsNullOrWhiteSpace(ocrResult.Text))
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    string errMsg = !string.IsNullOrWhiteSpace(ocrResult.ErrorMessage)
                        ? $"OCR 提取失败: {ocrResult.ErrorMessage}"
                        : "未在选区中检测到可识别文字。";
                    win.SetError(errMsg, $"{totalSw.ElapsedMilliseconds} ms (OCR 提取未命中)");
                });
                return;
            }

            _lastTranslateOcrText = ocrResult.Text;

            // 推送 OCR 识别到的原文数据到 UI，并更新提示
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                win.SetSourceText(_lastTranslateOcrText);
                win.ShowLoading("文字提取成功，正在请求翻译...");
            });

            // 2. 翻译服务请求
            var s = _settingsService.Current;
            var transResult = await _translationService.TranslateAsync(
                _lastTranslateOcrText,
                s.TranslateSourceLang,
                s.TranslateTargetLang,
                ct);

            if (ct.IsCancellationRequested) return;
            totalSw.Stop();

            // 推送翻译结果数据到 UI
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (transResult.IsSuccess)
                {
                    string elapsedInfo = $"{totalSw.ElapsedMilliseconds} ms (OCR {ocrResult.Elapsed.TotalMilliseconds:F0}ms / 翻译 {transResult.Elapsed.TotalMilliseconds:F0}ms)";
                    string quotaInfo = FormatQuotaInfo(s.TranslateProvider);
                    win.SetSuccessResult(transResult.TranslatedText, elapsedInfo, quotaInfo);
                }
                else
                {
                    win.SetError(transResult.ErrorMessage ?? "翻译接口返回失败", $"{totalSw.ElapsedMilliseconds} ms (接口异常)");
                }
            });
        }
        catch (OperationCanceledException)
        {
            // 正常取消操作
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                win.SetError($"处理异常: {ex.Message}", $"{totalSw.ElapsedMilliseconds} ms (系统异常)");
            });
        }
    }

    private void OnTranslateWindowRetryRequested()
    {
        if (string.IsNullOrWhiteSpace(_lastTranslateOcrText) || _activeTranslateWindow == null) return;

        CancelTranslatePipeline();
        _translateCts = new CancellationTokenSource();
        var ct = _translateCts.Token;

        var win = _activeTranslateWindow;
        var s = _settingsService.Current;
        win.ShowLoading($"正在请求 {s.TranslateProvider} 翻译...");

        Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var transResult = await _translationService.TranslateAsync(
                    _lastTranslateOcrText,
                    s.TranslateSourceLang,
                    s.TranslateTargetLang,
                    ct);

                if (ct.IsCancellationRequested) return;
                sw.Stop();

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (transResult.IsSuccess)
                    {
                        string elapsedInfo = $"{sw.ElapsedMilliseconds} ms (重试完成)";
                        string quotaInfo = FormatQuotaInfo(s.TranslateProvider);
                        win.SetSuccessResult(transResult.TranslatedText, elapsedInfo, quotaInfo);
                    }
                    else
                    {
                        win.SetError(transResult.ErrorMessage ?? "翻译接口返回失败", $"{sw.ElapsedMilliseconds} ms (请求异常)");
                    }
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) return;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    win.SetError($"重试异常: {ex.Message}", $"{sw.ElapsedMilliseconds} ms (系统异常)");
                });
            }
        }, ct);
    }

    private string FormatQuotaInfo(string provider)
    {
        var quota = _translationService.GetQuotaInfo(provider);
        return quota.MonthlyLimit > 0
            ? $"{quota.UsedThisMonth:N0} / {quota.MonthlyLimit:N0} {quota.UnitName} ({quota.Percentage:F1}%)"
            : $"{quota.UsedThisMonth:N0} {quota.UnitName}";
    }

    private void CancelTranslatePipeline()
    {
        _translateCts?.Cancel();
        _translateCts?.Dispose();
        _translateCts = null;
    }

    #endregion

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _settingsService.SettingsChanged -= OnSettingsChanged;
            CancelTranslatePipeline();
            _activeTranslateWindow?.Close();
            _activeTranslateWindow = null;
            _currentSession?.Dispose();
            _ocrEngine.Dispose();
        }
    }
}

internal sealed class AvaloniaScreenSelectionFallback : IScreenSelectionService
{
    public Task<OcrImage?> SelectRegionAsync(OcrImage fullScreenshot, ScreenRect virtualBounds)
    {
        return Dispatcher.UIThread.InvokeAsync(() => OverlaySelectionWindow.ShowAndSelectAsync(fullScreenshot, virtualBounds));
    }
}
