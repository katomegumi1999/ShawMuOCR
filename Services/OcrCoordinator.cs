using System.Diagnostics;
using Avalonia;
using Avalonia.Threading;
using ShawMuOCR.Core.Capture;
using ShawMuOCR.Core.Clipboard;
using ShawMuOCR.Core.Logging;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Services;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.Core.Sorting;
using ShawMuOCR.Core.Translation;
using ShawMuOCR.Views;
using ShawMuOCR.Windows.ScreenCapture;

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
    private IIdleSleepService? _idleSleepService;
    private Task? _warmupTask;
    private OcrSession? _currentSession;
    private OcrState _currentState = OcrState.Idle;
    private string? _statusMessage = "就绪";
    private bool _disposed;
    private TranslateFloatingWindow? _activeTranslateWindow;
    private TranslateFloatingWindow? _activeOcrPopupWindow;
    private CancellationTokenSource? _translateCts;
    private string _lastTranslateOcrText = string.Empty;

    public Action<ScreenRect>? RegionRecordingHandler { get; set; }

    /// <summary>当前正在跑的长截图采集器；非采集期间为 null。用于「再次按热键提前收尾」。</summary>
    private LongScreenshotCapturer? _longScreenshotCapturer;

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
        ITranslationService? translationService = null,
        IIdleSleepService? idleSleepService = null)
    {
        _captureService = captureService;
        _selectionService = selectionService ?? new AvaloniaScreenSelectionFallback();
        _engineFactory = engineFactory;
        _clipboardService = clipboardService;
        _settingsService = settingsService;
        _logger = logger;
        _translationService = translationService ?? new TranslationService(settingsService);
        _idleSleepService = idleSleepService;

        // 默认初始化当前模型（捕获启动异常并写入错误日志，防止因目录权限或模型文件问题导致启动直接闪退）
        _cachedSettings = _settingsService.Current.Clone();
        try
        {
            string originalModel = _cachedSettings.Model;
            _ocrEngine = _engineFactory.Create(_cachedSettings);
            if (!string.Equals(originalModel, _cachedSettings.Model, StringComparison.OrdinalIgnoreCase))
            {
                _settingsService.Save(_cachedSettings);
            }

            if (_ocrEngine is ShawMuOCR.OCR.PaddleOcrV6Engine { ConfiguredBackend: not Sdcb.SimdPaddleOCR.OcrBackend.Cpu })
            {
                StartBackgroundWarmup(_ocrEngine, "启动初始化", delayMs: 300);
            }
        }
        catch (Exception ex)
        {
            _logger?.Error("启动初始化 OCR 模型引擎失败", ex);
            _statusMessage = $"⚠️ OCR 模型未就绪: {ex.Message}";
            _ocrEngine = new FaultedOcrEngine(ex.Message);
        }

        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    public bool IsEngineFaulted => _ocrEngine is FaultedOcrEngine;

    private bool TryRecoverFaultedEngine(out string errorMessage)
    {
        lock (_stateLock)
        {
            if (_ocrEngine is not FaultedOcrEngine)
            {
                errorMessage = string.Empty;
                return true;
            }

            try
            {
                var current = _settingsService.Current.Clone();
                string originalModel = current.Model;
                var newEngine = _engineFactory.Create(current);
                _ocrEngine.Dispose();
                _ocrEngine = newEngine;
                _cachedSettings = current;
                if (!string.Equals(originalModel, current.Model, StringComparison.OrdinalIgnoreCase))
                {
                    _settingsService.Save(current);
                }

                errorMessage = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                _ocrEngine = new FaultedOcrEngine(ex.Message);
                return false;
            }
        }
    }

    private sealed class FaultedOcrEngine(string errorMessage) : IOcrEngine
    {
        public string ModelName => "未检测到模型";

        public Task<OcrResult> RecognizeAsync(OcrImage image, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException($"OCR 引擎未就绪: {errorMessage}");

        public void Dispose()
        {
        }
    }

    public void AttachIdleSleepService(IIdleSleepService idleSleepService)
    {
        _idleSleepService = idleSleepService;
    }

    private void StartBackgroundWarmup(IOcrEngine engine, string scene, int delayMs = 0)
    {
        lock (_stateLock)
        {
            _logger?.Info($"[{scene}] 正在后台异步预热 OCR 引擎 (包含 GPU Vulkan 着色器管线 / 内存池)...");
            _warmupTask = Task.Run(async () =>
            {
                try
                {
                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs);
                    }
                    var sw = Stopwatch.StartNew();
                    await engine.WarmupAsync();
                    sw.Stop();
                    _logger?.Info($"[{scene}] OCR 引擎预热就绪，耗时 {sw.ElapsedMilliseconds} ms");
                }
                catch (Exception ex)
                {
                    _logger?.Warn($"[{scene}] OCR 引擎预热异常: {ex.Message}");
                }
            });
        }
    }

    private void CheckAndTriggerWarmup()
    {
        if (_idleSleepService?.TryWakeUp() == true)
        {
            StartBackgroundWarmup(_ocrEngine, "休眠唤醒");
        }
    }

    private async Task AwaitWarmupAsync()
    {
        Task? warmup;
        lock (_stateLock)
        {
            warmup = _warmupTask;
            _warmupTask = null;
        }

        if (warmup != null)
        {
            try
            {
                await warmup;
            }
            catch
            {
                // 预热异常已在内部消化，不影响正式流程
            }
        }
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
                _cachedSettings.MaxSideLen != settings.MaxSideLen ||
                !string.Equals(_cachedSettings.OcrBackend, settings.OcrBackend, StringComparison.OrdinalIgnoreCase) ||
                _cachedSettings.SelectedGpuIndex != settings.SelectedGpuIndex)
            {
                try
                {
                    var newEngine = _engineFactory.Create(settings);
                    _ocrEngine.Dispose();
                    _ocrEngine = newEngine;
                    _cachedSettings = settings.Clone();

                    if (_ocrEngine is ShawMuOCR.OCR.PaddleOcrV6Engine { ConfiguredBackend: not Sdcb.SimdPaddleOCR.OcrBackend.Cpu })
                    {
                        StartBackgroundWarmup(_ocrEngine, "切换配置");
                    }
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

        CheckAndTriggerWarmup();

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

    /// <summary>
    /// 长截图：框选一块区域后跟随用户手动滚动连续抓帧拼接，结果写入剪贴板并作为预览会话展示。
    /// 采集中再次触发则提前收尾。支持传入初始预设选区（如截图标注选区一键转入长截屏）。
    /// </summary>
    public async Task StartLongScreenshotAsync(ScreenRect? initialRegion = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 采集中再次按下热键 = 立即收尾，而不是开新任务
        if (_longScreenshotCapturer is { IsRunning: true } active)
        {
            active.RequestStop();
            SetState(OcrState.Recognizing, "长截图正在收尾...");
            return;
        }

        lock (_stateLock)
        {
            if (_currentState is OcrState.Selecting or OcrState.Recognizing)
            {
                return;
            }
            SetState(OcrState.Selecting, "正在框选长截图区域...");
        }

        ScreenRect targetBounds;
        var virtualBounds = _captureService.GetVirtualScreenBounds();

        try
        {
            // 立即销毁未消失的气泡通知，避免二次截屏时被截入画面或阻挡框选视线
            ToastNotificationWindow.Dismiss();

            if (initialRegion.HasValue && !initialRegion.Value.IsEmpty && initialRegion.Value.Width >= 10 && initialRegion.Value.Height >= 10)
            {
                targetBounds = initialRegion.Value;
            }
            else
            {
                // 1. 抓取物理全屏用于框选遮罩
                var fullScreenshot = _captureService.CaptureVirtualScreen();

                // 2. 框选，顺便拿到选区的物理屏幕坐标（采集器要按坐标反复重抓这一块）
                var selection = await _selectionService.SelectRegionExAsync(fullScreenshot, virtualBounds);

                fullScreenshot.Dispose();

                if (selection == null)
                {
                    SetState(OcrState.Idle, "长截图已取消");
                    return;
                }

                // 选区图只是框选那一刻的静态快照，采集器会自己重新抓帧，这里立即释放
                selection.Image.Dispose();
                targetBounds = selection.ScreenBounds;
            }
        }
        catch (Exception ex)
        {
            _logger?.Error("长截图选区准备失败", ex);
            SetState(OcrState.Failed, $"长截图启动异常：{ex.Message}");
            return;
        }

        var capturer = new LongScreenshotCapturer(_captureService);
        _longScreenshotCapturer = capturer;

        SetState(OcrState.Recognizing, "长截图进行中，请滚动目标窗口");

        // 唤起遮罩层与悬浮面板（无头单元测试环境下 Application.Current 为空时安全跳过）
        LongScreenshotOverlayController? overlay = null;
        if (Application.Current != null)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                overlay = LongScreenshotOverlayController.Show(
                    targetBounds,
                    virtualBounds,
                    onStop: () => capturer.RequestStop(),
                    onCancel: () => capturer.RequestCancel());
            });
        }

        try
        {
            // 采集循环跑在线程池上，状态回调必须切回 UI 线程再改状态
            var result = await capturer.RunAsync(
                targetBounds,
                message => Dispatcher.UIThread.Post(() =>
                {
                    SetState(OcrState.Recognizing, message);
                    overlay?.UpdateStatus(message);
                }));

            if (result.Image is null)
            {
                SetState(OcrState.Idle, result.Status);
                if (_settingsService.Current.ShowCaptureImageNotification)
                {
                    ToastNotificationWindow.Show(result.Status, TimeSpan.Zero, "长截图未完成");
                }
                return;
            }

            // 拼接器返回的是裸位图，包一层 OcrImage 后才好复用剪贴板与会话（所有权随之移交）
            var longImage = new OcrImage(result.Image);

            // 3. 长图写入剪贴板
            bool copied = await _clipboardService.SetImageAsync(longImage);

            // 4. 更新当前会话供主窗口预览（图片所有权移交会话）
            _currentSession?.Dispose();
            _currentSession = new OcrSession(longImage, OcrResult.Empty());

            string status = copied
                ? $"✓ 长截图已复制到剪贴板（{result.FrameCount} 帧 / {result.Height} 像素高）"
                : $"✕ 长截图已生成但写入剪贴板失败（{result.Height} 像素高）";
            SetState(copied ? OcrState.Success : OcrState.Failed, status);

            if (_settingsService.Current.ShowCaptureImageNotification)
            {
                ToastNotificationWindow.Show(status, TimeSpan.Zero, "长截图完成");
            }

            SessionCompleted?.Invoke(_currentSession);
        }
        catch (Exception ex)
        {
            _logger?.Error("长截图流程失败", ex);
            SetState(OcrState.Failed, $"长截图错误: {ex.Message}");
        }
        finally
        {
            _longScreenshotCapturer = null;
            if (overlay != null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => overlay.Close());
            }
        }
    }

    public async Task StartAnnotateScreenshotAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        CheckAndTriggerWarmup();

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
            // 在进入框选截屏的同时在后台异步预热标注窗口。用户框选通常耗时数百毫秒至数秒，此时间足以完成离屏预热就绪
            Dispatcher.UIThread.Post(() => ScreenshotEditorWindow.Prewarm(_logger, "截图标注"), DispatcherPriority.Background);

            // 立即销毁未消失的气泡通知，避免二次截屏时被截入画面或阻挡框选视线
            ToastNotificationWindow.Dismiss();

            // 1. 抓取物理全屏（包括所有显示器）
            var virtualBounds = _captureService.GetVirtualScreenBounds();
            var fullScreenshot = _captureService.CaptureVirtualScreen();

            // 2. 唤起全屏选择遮罩交互，同时捕获裁剪图像与物理选区坐标
            var selection = await _selectionService.SelectRegionExAsync(fullScreenshot, virtualBounds);

            if (selection == null)
            {
                fullScreenshot.Dispose();
                SetState(OcrState.Idle, "截屏已取消");
                return;
            }

            SetState(OcrState.Idle, "截图标注已打开");

            // 计算选区在全屏快照上的精确物理像素相对偏移量
            var cropOffset = new ScreenPoint(selection.ScreenBounds.X - virtualBounds.X, selection.ScreenBounds.Y - virtualBounds.Y);

            // 3. 在 UI 线程打开截图标注编辑器（将 fullScreenshot 的完整生命周期所有权交由编辑器闭环管理）
            void ShowEditor()
            {
                var editor = new ScreenshotEditorWindow(
                    selection.Image,
                    _clipboardService,
                    async img => await RunOcrAsync(img),
                    async (img, bounds) => await PushTranslationPipelineAsync(img, bounds),
                    _settingsService,
                    _logger,
                    selection.ScreenBounds,
                    fullScreenshot: fullScreenshot,
                    cropOffset: cropOffset,
                    isPrewarm: false,
                    longScreenshotCallback: async bounds => await StartLongScreenshotAsync(bounds),
                    regionRecordCallback: bounds =>
                    {
                        RegionRecordingHandler?.Invoke(bounds);
                        return Task.CompletedTask;
                    });
                editor.Show();
                editor.Activate();
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                ShowEditor();
            }
            else
            {
                await Dispatcher.UIThread.InvokeAsync(ShowEditor, DispatcherPriority.Send);
            }
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

        CheckAndTriggerWarmup();

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
            await PushTranslationPipelineAsync(selection.Image, selection.ScreenBounds, selection.StartPoint);

            GC.Collect(2, GCCollectionMode.Optimized, false, false);
        }
        catch (Exception ex)
        {
            _logger?.Error("选区翻译流程失败", ex);
            SetState(OcrState.Failed, $"选区翻译错误: {ex.Message}");
        }
    }

    public async Task StartOcrPopupAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        CheckAndTriggerWarmup();

        lock (_stateLock)
        {
            if (_currentState is OcrState.Selecting or OcrState.Recognizing)
            {
                return;
            }
            SetState(OcrState.Selecting, "正在框选 OCR 区域...");
        }

        try
        {
            ToastNotificationWindow.Dismiss();

            var virtualBounds = _captureService.GetVirtualScreenBounds();
            var fullScreenshot = _captureService.CaptureVirtualScreen();

            var selection = await _selectionService.SelectRegionExAsync(fullScreenshot, virtualBounds);

            fullScreenshot.Dispose();

            if (selection == null)
            {
                SetState(OcrState.Idle, "OCR 窗格已取消");
                return;
            }

            SetState(OcrState.Idle, "OCR 窗格已就绪");

            await PushOcrPopupPipelineAsync(selection.Image, selection.ScreenBounds, selection.StartPoint);

            GC.Collect(2, GCCollectionMode.Optimized, false, false);
        }
        catch (Exception ex)
        {
            _logger?.Error("OCR 窗格流程失败", ex);
            SetState(OcrState.Failed, $"OCR 窗格错误: {ex.Message}");
        }
    }

    public async Task RunOcrAsync(OcrImage image)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(image);

        if (!TryRecoverFaultedEngine(out string recoverError))
        {
            SetState(OcrState.Failed, $"⚠️ 未检测到 OCR 模型: {recoverError}");
            ToastNotificationWindow.Show(
                "未在 models 文件夹下检测到任何 OCR 模型！请将随程序分发的 models 文件夹放置在与主程序同一目录下。",
                TimeSpan.Zero,
                "⚠️ 缺少 OCR 模型");
            return;
        }

        lock (_stateLock)
        {
            SetState(OcrState.Recognizing, "正在识别文字...");
        }

        try
        {
            // 如果休眠唤醒时的后台预热尚未完成，平滑等待就绪，避免并发重入冲突
            await AwaitWarmupAsync();

            // 表格线检测只读像素、与 OCR 结果无关，因此和推理并行执行：
            // 5~15ms 的扫描耗时被数百毫秒的推理完全掩盖，相当于零额外成本。
            // 并发安全：推理侧对 image.Bitmap 只做 GetPixelSpan 只读访问或 DrawBitmap 拷贝，
            // 线检测侧也只 ReadPixel，双方都不会修改源位图。
            bool wantTableSplit = _settingsService.Current.AutoCopyTableAsCells;
            Task<(List<(int Left, int Right)>? Columns, string Info)>? lineTask = null;
            if (wantTableSplit)
            {
                lineTask = Task.Run<(List<(int Left, int Right)>? Columns, string Info)>(() =>
                    TableLineDetector.TryDetectColumns(image.Bitmap, out var detectedColumns, out string info)
                        ? (detectedColumns, info)
                        : (null, info));
            }

            // 执行后台推理
            var ocrTask = Task.Run(async () =>
            {
                return await _ocrEngine.RecognizeAsync(image);
            });

            List<(int Left, int Right)>? lineColumns = null;
            string lineInfo = string.Empty;

            if (lineTask is not null)
            {
                await Task.WhenAll(ocrTask, lineTask);
                lineColumns = lineTask.Result.Columns;
                lineInfo = lineTask.Result.Info;
            }

            var result = await ocrTask;

            // 释放旧会话中的图片
            _currentSession?.Dispose();
            _currentSession = new OcrSession(image, result);

            if (result.IsSuccess)
            {
                // 表格智能分列：识别结果若呈表格结构（多行多列、检测框按几何对齐），
                // 剪贴板同时写入 HTML 表格（CF_HTML）与纯文本（CF_UNICODETEXT）两种格式：
                // - Excel / WPS / Word 等富文本目标优先采用 HTML，粘贴后直接落成单元格，合并表头保留 colspan；
                // - 聊天窗口、搜索框、记事本等纯文本目标只读纯文本，不会出现制表符造成的怪异空档。
                // 非表格内容仍只写纯文本，与改动前完全一致。
                string clipboardText = result.Text;
                string? clipboardHtml = null;
                int tableRowCount = 0;
                int tableColumnCount = 0;

                if (wantTableSplit && result.Blocks.Count > 0)
                {
                    // 线检测结果已在推理期间并行算好：有竖线按线分列（像素级稳定），无竖线才退回投影法
                    var layout = OcrTableLayoutBuilder.TryBuildWithColumns(result.Blocks, lineColumns, out string? tableReason);

                    if (!string.IsNullOrEmpty(lineInfo))
                    {
                        _logger?.Info($"[表格线] {lineInfo}");
                    }
                    if (layout != null)
                    {
                        clipboardHtml = layout.ToHtmlTable();
                        tableRowCount = layout.RowCount;
                        tableColumnCount = layout.ColumnCount;
                    }

                    if (!string.IsNullOrEmpty(tableReason))
                    {
                        _logger?.Info($"[表格分列] {tableReason}");
                    }
                }

                bool htmlApplied = false;
                bool copied = false;

                if (_settingsService.Current.AutoCopy && !string.IsNullOrWhiteSpace(clipboardText))
                {
                    if (clipboardHtml != null)
                    {
                        copied = await _clipboardService.SetTextWithHtmlAsync(clipboardText, clipboardHtml);
                        // 以剪贴板实际写入结果为准，避免「界面说已分列、粘出来还是一格」的假成功
                        htmlApplied = copied && _clipboardService.LastWriteIncludedHtml;

                        if (htmlApplied)
                        {
                            _logger?.Info($"[表格分列] 剪贴板已写入 CF_UNICODETEXT + HTML Format ({tableRowCount} 行 × {tableColumnCount} 列)");
                        }
                        else if (copied)
                        {
                            _logger?.Warn("[表格分列] HTML 格式写入剪贴板失败，已降级为纯文本；粘贴到 Excel / WPS 仍会挤在单个单元格。若反复出现请检查剪贴板是否被其他程序占用。");
                        }
                    }
                    else
                    {
                        copied = await _clipboardService.SetTextAsync(clipboardText);
                    }
                }

                string status = copied
                    ? (htmlApplied
                        ? $"✓ 已自动复制·表格已分列 ({tableRowCount} 行 × {tableColumnCount} 列, 耗时 {result.Elapsed.TotalMilliseconds:F0} ms)"
                        : $"✓ 已自动复制 (耗时 {result.Elapsed.TotalMilliseconds:F0} ms)")
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

        if (!TryRecoverFaultedEngine(out string recoverError))
        {
            return OcrResult.Failed(recoverError, TimeSpan.Zero);
        }

        await AwaitWarmupAsync();

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
            _activeTranslateWindow.NewSessionRequested += OnTranslateWindowNewSessionRequested;
            _activeTranslateWindow.Closed += (_, _) =>
            {
                _activeTranslateWindow = null;
                CancelTranslatePipeline();
            };
        }
        return _activeTranslateWindow;
    }

    private void OnTranslateWindowNewSessionRequested()
    {
        _ = StartTranslateSelectionAsync();
    }

    private async Task PushTranslationPipelineAsync(OcrImage image, ScreenRect bounds, ScreenPoint? startPoint = null)
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
                win.InitializePosition(bounds, startPoint);
                win.Show();
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
            // 如果休眠唤醒时的后台预热尚未完成，平滑等待就绪
            await AwaitWarmupAsync();

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
        string providerName = TranslationProviders.GetDisplayName(s.TranslateProvider);
        win.ShowLoading($"正在请求 {providerName} 翻译...");

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

    #region OCR 窗格数据流水线管理

    private TranslateFloatingWindow EnsureOcrPopupWindow()
    {
        if (_activeOcrPopupWindow == null || !_activeOcrPopupWindow.IsVisible)
        {
            _activeOcrPopupWindow = new TranslateFloatingWindow(
                _settingsService,
                _clipboardService,
                translationService: null,
                FloatingWindowMode.OcrPopup);

            _activeOcrPopupWindow.NewSessionRequested += OnOcrPopupNewSessionRequested;
            _activeOcrPopupWindow.Closed += (_, _) =>
            {
                _activeOcrPopupWindow = null;
            };
        }
        return _activeOcrPopupWindow;
    }

    private void OnOcrPopupNewSessionRequested()
    {
        _ = StartOcrPopupAsync();
    }

    private async Task PushOcrPopupPipelineAsync(OcrImage image, ScreenRect bounds, ScreenPoint? startPoint = null)
    {
        TranslateFloatingWindow win = null!;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            bool isNewWindow = (_activeOcrPopupWindow == null || !_activeOcrPopupWindow.IsVisible);
            win = EnsureOcrPopupWindow();
            if (isNewWindow)
            {
                win.InitializePosition(bounds, startPoint);
                win.Show();
            }
            else
            {
                win.BringToFront();
            }
            win.ShowLoading("正在 OCR 识别文字...");
        });

        var totalSw = Stopwatch.StartNew();
        try
        {
            await AwaitWarmupAsync();

            OcrResult ocrResult;
            try
            {
                ocrResult = await _ocrEngine.RecognizeAsync(image);
            }
            finally
            {
                image.Dispose();
            }

            totalSw.Stop();

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

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                string elapsedInfo = $"{totalSw.ElapsedMilliseconds} ms (OCR {ocrResult.Elapsed.TotalMilliseconds:F0}ms)";
                win.SetSuccessResult(ocrResult.Text, elapsedInfo, string.Empty);
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                win.SetError($"处理异常: {ex.Message}", $"{totalSw.ElapsedMilliseconds} ms (系统异常)");
            });
        }
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
            _activeOcrPopupWindow?.Close();
            _activeOcrPopupWindow = null;
            // 长截图采集循环是自持的，退出时必须显式叫停，否则会一直抓屏到帧数上限
            _longScreenshotCapturer?.RequestStop();
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
