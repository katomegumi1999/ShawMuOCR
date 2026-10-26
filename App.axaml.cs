using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using System.IO;
using ShawMuOCR.Core.Capture;
using ShawMuOCR.Core.Clipboard;
using ShawMuOCR.Core.Hotkey;
using ShawMuOCR.Core.Logging;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Services;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.Core.Sorting;
using ShawMuOCR.OCR;
using ShawMuOCR.Services;
using ShawMuOCR.ViewModels;
using ShawMuOCR.Views;
using ShawMuOCR.Windows.Clipboard;
using ShawMuOCR.Windows.Hotkey;
using ShawMuOCR.Windows.ScreenCapture;
using ShawMuOCR.Windows.Startup;
using MuClip.Services;
using MuRecorder.ViewModels;
using ShawMuOCR.Views.Clipboard;

namespace ShawMuOCR;

public partial class App : Application
{
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(NativeMenuItem))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(NativeMenu))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Comparison<IObserver<AvaloniaPropertyChangedEventArgs<NativeMenuItem>>>))]
    static App()
    {
        // 显式固化 Native AOT 泛型比较器虚表，防止 MSVC 链接器在全程序优化时产生无法解析的外部符号 vftable
        _ = typeof(Comparison<IObserver<AvaloniaPropertyChangedEventArgs<NativeMenuItem>>>);
        _ = typeof(AvaloniaPropertyChangedEventArgs<NativeMenuItem>);
    }

    private const int HOTKEY_ID_OCR = 1;
    private const int HOTKEY_ID_CAPTURE = 2;
    private const int HOTKEY_ID_ANNOTATE = 3;
    private const int HOTKEY_ID_TRANSLATE = 4;
    private const int HOTKEY_ID_CLIPBOARD = 5;
    private const int HOTKEY_ID_PHRASES = 6;
    private const int HOTKEY_ID_OCR_POPUP = 7;
    private const int HOTKEY_ID_NOTE = 8;
    private const int HOTKEY_ID_LONG_SCREENSHOT = 9;
    private const int HOTKEY_ID_RECORDING = 10;

    private ISettingsService? _settingsService;
    private IStartupService? _startupService;
    private IClipboardService? _clipboardService;
    private IOcrModelProvider? _modelProvider;
    private IOcrTextSorter? _textSorter;
    private IOcrEngineFactory? _engineFactory;
    private OcrCoordinator? _coordinator;
    private IGlobalHotkeyService? _hotkeyService;
    private IModelMemoryDiagnosticsService? _memoryDiagnosticsService;
    private IIdleSleepService? _idleSleepService;
    private ILoggerService? _loggerService;
    private IMuClipService? _muClipService;

    private MainWindow? _mainWindow;
    private MainViewModel? _mainViewModel;
    private TranslateFloatingWindow? _activeNoteWindow;
    private static App? s_instance;

    public App()
    {
        s_instance = this;
    }

    public static void WakeupMainWindow()
    {
        s_instance?.ShowMainWindow();
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;

            // 1. 初始化核心领域服务
            var settingsService = new JsonSettingsService();
            _settingsService = settingsService;
            _loggerService = new ShawMuOCR.Core.Logging.FileLoggerService(_settingsService);

            // 配置读写失败一律留痕，不再静默丢弃（避免"设置莫名重置"无从排查）
            settingsService.LoadFailed += (message, ex) => _loggerService?.Error(message, ex);
            settingsService.SaveFailed += (message, ex) => _loggerService?.Error(message, ex);
            if (settingsService.LastLoadError is { Length: > 0 } initialLoadError)
            {
                _loggerService?.Warn($"启动时读取配置失败，已回退默认配置（原文件已备份为 *.bad-时间戳）：{initialLoadError}");
            }

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                _loggerService?.Error("未捕获的 AppDomain 全局异常", e.ExceptionObject as Exception);
            };
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                _loggerService?.Error("未观察到的 Task 异步异常", e.Exception);
                e.SetObserved();
            };

            _startupService = new WindowsStartupService();
            _clipboardService = WindowsClipboardService.Shared;
            _modelProvider = new OcrModelProvider();
            _textSorter = new DefaultOcrTextSorter();
            _engineFactory = new OcrEngineFactory(_textSorter, _modelProvider);
            _memoryDiagnosticsService = new ModelMemoryDiagnosticsService(_settingsService, _modelProvider);

            var captureService = new WindowsScreenCaptureService();
            var selectionService = new WindowsNativeScreenSelectionService();
            _coordinator = new OcrCoordinator(captureService, _engineFactory, _clipboardService, _settingsService, selectionService, _loggerService);
            _hotkeyService = new WindowsGlobalHotkeyService();
            _idleSleepService = new IdleSleepService(_settingsService, _memoryDiagnosticsService, _coordinator);
            _coordinator.AttachIdleSleepService(_idleSleepService);

            // 2. 初始化小沐剪贴板服务（仅在用户开启小沐剪贴板设置时初始化）
            if (_settingsService.Current.EnableClipboard)
            {
                EnsureMuClipServiceInitialized();
            }
            UpdateTrayClipboardMenuVisibility(_settingsService.Current.EnableClipboard);

            // 2.5 注册全局快捷键（截图 OCR、截图复制、截图标注、选区翻译、小沐剪贴板快捷面板）
            RegisterHotkeys(_settingsService.Current);
            _settingsService.SettingsChanged += OnSettingsChanged;

            // 3. 构建 ViewModel 与主窗口
            _mainViewModel = new MainViewModel(
                _coordinator,
                _clipboardService,
                _settingsService,
                CreateSettingsViewModel,
                CreateAboutViewModel,
                _loggerService,
                null,
                _muClipService,
                _engineFactory,
                recordingVm: new RecordingViewModel(captureService, selectionService, _loggerService, _settingsService));

            _mainWindow = new MainWindow(_mainViewModel);
            _coordinator.RequestShowMainWindow += ShowMainWindow;
            _coordinator.RegionRecordingHandler = rect =>
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    var recVm = _mainViewModel?.RecordingVm;
                    if (recVm == null) return;
                    ShawMuOCR.Views.RecordingFloatingWindow.ShowOverlay(recVm);
                    await recVm.StartRegionRecordingAsync(rect);
                });
            };
            desktop.Exit += OnDesktopExit;

            // 4. 根据静默启动配置或独立剪贴板模式决定是否呈现主界面
            if (Program.IsClipStandaloneMode)
            {
                _mainViewModel.CurrentPage = ViewModels.AppNavPage.Clipboard;
                _mainWindow.EnsurePageLoaded(ViewModels.AppNavPage.Clipboard);
                _loggerService?.Info("已开启小沐剪贴板轻量模式：常驻系统托盘，按快捷键即可呼出快捷面板。");
                Dispatcher.UIThread.Post(() => _mainWindow.PreloadRemainingPages(), DispatcherPriority.Background);
            }
            else if (!_settingsService.Current.SilentStartup)
            {
                // 普通正常启动：先且仅先渲染用户退出前记忆的默认页面，实现秒级冷启动直现！
                _mainWindow.EnsurePageLoaded(_mainViewModel.CurrentPage);
                desktop.MainWindow = _mainWindow;
                _mainWindow.Show();

                // 启动完全呈现后，在后台空闲帧平滑逐步预加载其余页面与编辑器，不阻塞当前界面交互
                Dispatcher.UIThread.Post(() =>
                {
                    _mainWindow.PreloadRemainingPages();
                    ScreenshotEditorWindow.Prewarm(_loggerService, "程序启动");
                }, DispatcherPriority.Background);
            }
            else
            {
                // 静默启动：启动时不渲染任何页面，以最高速度完成托盘常驻！
                _loggerService?.Info("已开启静默启动：程序常驻系统托盘，未显示主界面。");

                // 程序启动就绪后，在后台空闲队列中逐步完成页面渲染预热与编辑器预热
                Dispatcher.UIThread.Post(() =>
                {
                    _mainWindow.PreloadRemainingPages();
                    ScreenshotEditorWindow.Prewarm(_loggerService, "程序启动");
                }, DispatcherPriority.Background);
            }

            // 5. 若启动时未在同级 models 文件夹下检测到任何模型，主动弹出气泡提醒用户（剪贴板轻量模式下不弹出）
            if (!Program.IsClipStandaloneMode && _modelProvider.GetAvailableModels().Count == 0)
            {
                _loggerService?.Warn($"启动检测：未在 models 目录 ({_modelProvider.BaseModelsDirectory}) 下发现任何可用 OCR 模型。");
                Dispatcher.UIThread.Post(() =>
                {
                    ToastNotificationWindow.Show(
                        "未在同级 models 文件夹下检测到任何 OCR 模型！请将分发的 models 文件夹放入程序目录后在设置中刷新。",
                        TimeSpan.Zero,
                        "⚠️ 未检测到 OCR 模型");
                });
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        if (settings.EnableClipboard)
        {
            EnsureMuClipServiceInitialized();
        }
        else
        {
            ClipboardQuickPanelWindow.CloseInstance();
            if (_muClipService != null)
            {
                try
                {
                    _muClipService.Dispose();
                }
                catch { }
                _muClipService = null;
                _loggerService?.Info("小沐剪贴板已关闭，后台网络同步与热键监听服务已释放");
            }
            _mainViewModel?.DetachMuClipService();
        }

        UpdateTrayClipboardMenuVisibility(settings.EnableClipboard);
        RegisterHotkeys(settings);
    }

    private void UpdateTrayClipboardMenuVisibility(bool enabled)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var icons = TrayIcon.GetIcons(this);
                if (icons == null) return;
                foreach (var icon in icons)
                {
                    if (icon.Menu?.Items == null) continue;
                    foreach (var item in icon.Menu.Items)
                    {
                        if (item is NativeMenuItem menuItem &&
                            (menuItem.Header == "小沐剪贴板" || menuItem.Header == "极速剪贴板"))
                        {
                            menuItem.IsVisible = enabled;
                        }
                    }
                }
            }
            catch { }
        });
    }

    private void EnsureMuClipServiceInitialized()
    {
        if (_muClipService != null) return;
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string clipDbPath = Path.Combine(appData, "ShawMuOCR", "Muclip", "muclip.db");
            _muClipService = new MuClipService(_clipboardService);
            _muClipService.Initialize(clipDbPath, startSync: true, syncPort: 42424);
            _loggerService?.Info($"小沐剪贴板服务已就绪，当前配对信任凭证已加载 (PIN: {_muClipService.PairingPin})");

            _muClipService.IncomingPairRequested += (reqId, devId, devName, ip) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    PairRequestDialog.ShowRequest(_mainWindow, _muClipService, reqId, devId, devName, ip);
                });
            };

            _mainViewModel?.AttachMuClipService(_muClipService);
        }
        catch (Exception ex)
        {
            _loggerService?.Error("初始化小沐剪贴板服务失败", ex);
        }
    }

    private static bool IsDisabledHotkey(string? hk) =>
        string.IsNullOrWhiteSpace(hk) || hk.Equals("无", StringComparison.OrdinalIgnoreCase) || hk.Equals("None", StringComparison.OrdinalIgnoreCase);

    private void RegisterHotkeys(AppSettings settings)
    {
        if (_hotkeyService == null || _coordinator == null) return;

        bool ocrSuccess = true;
        if (!IsDisabledHotkey(settings.Hotkey))
        {
            if (HotkeyGesture.TryParse(settings.Hotkey, out var ocrGesture))
            {
                ocrSuccess = _hotkeyService.Register(HOTKEY_ID_OCR, ocrGesture, () =>
                {
                    _ = _coordinator.StartScreenCaptureAsync();
                });
            }
            else
            {
                ocrSuccess = false;
            }
        }
        else
        {
            _hotkeyService.Unregister(HOTKEY_ID_OCR);
        }

        bool captureSuccess = true;
        if (!IsDisabledHotkey(settings.CaptureImageHotkey))
        {
            if (HotkeyGesture.TryParse(settings.CaptureImageHotkey, out var captureGesture))
            {
                captureSuccess = _hotkeyService.Register(HOTKEY_ID_CAPTURE, captureGesture, () =>
                {
                    _ = _coordinator.StartCaptureImageAsync();
                });
            }
            else
            {
                captureSuccess = false;
            }
        }
        else
        {
            _hotkeyService.Unregister(HOTKEY_ID_CAPTURE);
        }

        bool annotateSuccess = true;
        if (!IsDisabledHotkey(settings.AnnotateHotkey))
        {
            if (HotkeyGesture.TryParse(settings.AnnotateHotkey, out var annotateGesture))
            {
                annotateSuccess = _hotkeyService.Register(HOTKEY_ID_ANNOTATE, annotateGesture, () =>
                {
                    _ = _coordinator.StartAnnotateScreenshotAsync();
                });
            }
            else
            {
                annotateSuccess = false;
            }
        }
        else
        {
            _hotkeyService.Unregister(HOTKEY_ID_ANNOTATE);
        }

        bool longScreenshotSuccess = true;
        if (!IsDisabledHotkey(settings.LongScreenshotHotkey))
        {
            if (HotkeyGesture.TryParse(settings.LongScreenshotHotkey, out var longShotGesture))
            {
                longScreenshotSuccess = _hotkeyService.Register(HOTKEY_ID_LONG_SCREENSHOT, longShotGesture, () =>
                {
                    _ = _coordinator.StartLongScreenshotAsync();
                });
            }
            else
            {
                longScreenshotSuccess = false;
            }
        }
        else
        {
            _hotkeyService.Unregister(HOTKEY_ID_LONG_SCREENSHOT);
        }

        bool translateSuccess = true;
        if (!IsDisabledHotkey(settings.TranslateHotkey))
        {
            if (HotkeyGesture.TryParse(settings.TranslateHotkey, out var translateGesture))
            {
                translateSuccess = _hotkeyService.Register(HOTKEY_ID_TRANSLATE, translateGesture, () =>
                {
                    _ = _coordinator.StartTranslateSelectionAsync();
                });
            }
            else
            {
                translateSuccess = false;
            }
        }
        else
        {
            _hotkeyService.Unregister(HOTKEY_ID_TRANSLATE);
        }

        bool ocrPopupSuccess = true;
        if (!IsDisabledHotkey(settings.OcrPopupHotkey))
        {
            if (HotkeyGesture.TryParse(settings.OcrPopupHotkey, out var ocrPopupGesture))
            {
                ocrPopupSuccess = _hotkeyService.Register(HOTKEY_ID_OCR_POPUP, ocrPopupGesture, () =>
                {
                    _ = _coordinator.StartOcrPopupAsync();
                });
            }
            else
            {
                ocrPopupSuccess = false;
            }
        }
        else
        {
            _hotkeyService.Unregister(HOTKEY_ID_OCR_POPUP);
        }

        bool clipboardSuccess = true;
        if (settings.EnableClipboard && !IsDisabledHotkey(settings.ClipboardPanelHotkey))
        {
            EnsureMuClipServiceInitialized();
            if (_muClipService != null && HotkeyGesture.TryParse(settings.ClipboardPanelHotkey, out var clipGesture))
            {
                clipboardSuccess = _hotkeyService.Register(HOTKEY_ID_CLIPBOARD, clipGesture, () =>
                {
                    ClipboardQuickPanelWindow.ShowInstance(_muClipService, _settingsService, initialTabIndex: 0);
                });
            }
            else
            {
                clipboardSuccess = false;
            }
        }
        else
        {
            _hotkeyService.Unregister(HOTKEY_ID_CLIPBOARD);
        }

        bool phrasesSuccess = true;
        if (settings.EnableClipboard && !IsDisabledHotkey(settings.PhrasesPanelHotkey))
        {
            EnsureMuClipServiceInitialized();
            if (_muClipService != null && HotkeyGesture.TryParse(settings.PhrasesPanelHotkey, out var phrasesGesture))
            {
                phrasesSuccess = _hotkeyService.Register(HOTKEY_ID_PHRASES, phrasesGesture, () =>
                {
                    ClipboardQuickPanelWindow.ShowInstance(_muClipService, _settingsService, initialTabIndex: 1);
                });
            }
            else
            {
                phrasesSuccess = false;
            }
        }
        else
        {
            _hotkeyService.Unregister(HOTKEY_ID_PHRASES);
        }

        bool noteSuccess = true;
        if (!IsDisabledHotkey(settings.QuickNoteHotkey))
        {
            if (HotkeyGesture.TryParse(settings.QuickNoteHotkey, out var noteGesture))
            {
                noteSuccess = _hotkeyService.Register(HOTKEY_ID_NOTE, noteGesture, () =>
                {
                    OpenQuickNoteWindow();
                });
            }
            else
            {
                noteSuccess = false;
            }
        }
        else
        {
            _hotkeyService.Unregister(HOTKEY_ID_NOTE);
        }

        bool recordingSuccess = true;
        if (!IsDisabledHotkey(settings.RecordingHotkey))
        {
            if (HotkeyGesture.TryParse(settings.RecordingHotkey, out var recGesture))
            {
                recordingSuccess = _hotkeyService.Register(HOTKEY_ID_RECORDING, recGesture, () =>
                {
                    Dispatcher.UIThread.Post(async () =>
                    {
                        var recVm = _mainViewModel?.RecordingVm;
                        if (recVm == null) return;
                        if (recVm.IsRecording)
                        {
                            await recVm.StopRecordingCommand.ExecuteAsync(null);
                        }
                        else
                        {
                            // 触发桌面悬浮条并开始框选录制
                            ShawMuOCR.Views.RecordingFloatingWindow.ShowOverlay(recVm);
                            await recVm.SelectRegionCommand.ExecuteAsync(null);
                        }
                    });
                });
            }
            else
            {
                recordingSuccess = false;
            }
        }
        else
        {
            _hotkeyService.Unregister(HOTKEY_ID_RECORDING);
        }

        if (_mainViewModel != null)
        {
            if (!ocrSuccess && !captureSuccess && !annotateSuccess && !longScreenshotSuccess && !translateSuccess && !ocrPopupSuccess && !clipboardSuccess && !noteSuccess && !recordingSuccess)
            {
                _mainViewModel.StatusMessage = $"⚠️ 快捷键注册失败，请检查冲突并重新设置";
            }
            else if (!ocrSuccess)
            {
                _mainViewModel.StatusMessage = $"⚠️ 截图 OCR 快捷键 {settings.Hotkey} 注册失败(可能被占用)";
            }
            else if (!captureSuccess)
            {
                _mainViewModel.StatusMessage = $"⚠️ 截图复制快捷键 {settings.CaptureImageHotkey} 注册失败(可能被占用)";
            }
            else if (!annotateSuccess)
            {
                _mainViewModel.StatusMessage = $"⚠️ 截图标注快捷键 {settings.AnnotateHotkey} 注册失败(可能被占用)";
            }
            else if (!longScreenshotSuccess)
            {
                _mainViewModel.StatusMessage = $"⚠️ 长截图快捷键 {settings.LongScreenshotHotkey} 注册失败(可能被占用)";
            }
            else if (!translateSuccess)
            {
                _mainViewModel.StatusMessage = $"⚠️ 选区翻译快捷键 {settings.TranslateHotkey} 注册失败(可能被占用)";
            }
            else if (!ocrPopupSuccess)
            {
                _mainViewModel.StatusMessage = $"⚠️ OCR 窗格快捷键 {settings.OcrPopupHotkey} 注册失败(可能被占用)";
            }
            else if (!clipboardSuccess)
            {
                _mainViewModel.StatusMessage = $"⚠️ 剪贴板快捷键 {settings.ClipboardPanelHotkey} 注册失败(可能被占用)";
            }
            else if (!noteSuccess)
            {
                _mainViewModel.StatusMessage = $"⚠️ 随手便笺快捷键 {settings.QuickNoteHotkey} 注册失败(可能被占用)";
            }
            else if (_mainViewModel.StatusMessage.StartsWith("⚠️ 快捷键") || _mainViewModel.StatusMessage.StartsWith("⚠️ 截图") || _mainViewModel.StatusMessage.StartsWith("⚠️ 选区") || _mainViewModel.StatusMessage.StartsWith("⚠️ OCR") || _mainViewModel.StatusMessage.StartsWith("⚠️ 剪贴板") || _mainViewModel.StatusMessage.StartsWith("⚠️ 随手便笺"))
            {
                _mainViewModel.StatusMessage = "就绪";
            }
        }
    }

    public void OpenQuickNoteWindow()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_activeNoteWindow != null && _activeNoteWindow.IsVisible)
            {
                _activeNoteWindow.WindowState = Avalonia.Controls.WindowState.Normal;
                _activeNoteWindow.Activate();
                return;
            }

            EnsureMuClipServiceInitialized();

            _activeNoteWindow = new TranslateFloatingWindow(
                _settingsService!,
                _clipboardService!,
                translationService: null,
                FloatingWindowMode.Note,
                _muClipService);

            _activeNoteWindow.Closed += (_, _) => _activeNoteWindow = null;
            _activeNoteWindow.InitializeCenterPosition();
            _activeNoteWindow.Show();
            _activeNoteWindow.Activate();
        });
    }

    private SettingsViewModel CreateSettingsViewModel()
    {
        return new SettingsViewModel(_settingsService!, _startupService!, _hotkeyService!, _modelProvider!, CreateAboutViewModel, _loggerService);
    }

    private AboutViewModel CreateAboutViewModel()
    {
        return new AboutViewModel(_memoryDiagnosticsService!, _clipboardService);
    }

    private void ShowMainWindow()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_mainWindow != null)
            {
                if (_mainViewModel != null)
                {
                    _mainWindow.EnsurePageLoaded(_mainViewModel.CurrentPage);
                }
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow == null)
                {
                    desktop.MainWindow = _mainWindow;
                }
                _mainWindow.Show();
                _mainWindow.WindowState = Avalonia.Controls.WindowState.Normal;
                _mainWindow.Activate();
            }
        });
    }

    private void OnTrayIconClicked(object? sender, EventArgs e)
    {
        ShowMainWindow();
    }

    private void OnTrayCaptureClick(object? sender, EventArgs e)
    {
        _ = _coordinator?.StartScreenCaptureAsync();
    }

    private void OnTrayCaptureImageClick(object? sender, EventArgs e)
    {
        _ = _coordinator?.StartCaptureImageAsync();
    }

    private void OnTrayAnnotateClick(object? sender, EventArgs e)
    {
        _ = _coordinator?.StartAnnotateScreenshotAsync();
    }

    private void OnTrayLongScreenshotClick(object? sender, EventArgs e)
    {
        _ = _coordinator?.StartLongScreenshotAsync();
    }

    private void OnTrayTranslateClick(object? sender, EventArgs e)
    {
        _ = _coordinator?.StartTranslateSelectionAsync();
    }

    private void OnTrayOcrPopupClick(object? sender, EventArgs e)
    {
        _ = _coordinator?.StartOcrPopupAsync();
    }

    private void OnTrayQuickNoteClick(object? sender, EventArgs e)
    {
        OpenQuickNoteWindow();
    }

    private void OnTrayOpenMainClick(object? sender, EventArgs e)
    {
        _mainViewModel?.Navigate(AppNavPage.Ocr);
        ShowMainWindow();
    }

    private void OnTrayClipboardClick(object? sender, EventArgs e)
    {
        if (_settingsService?.Current.EnableClipboard != true) return;
        _mainViewModel?.Navigate(AppNavPage.Clipboard);
        ShowMainWindow();
    }

    private void OnTraySettingsClick(object? sender, EventArgs e)
    {
        _mainViewModel?.Navigate(AppNavPage.Settings);
        ShowMainWindow();
    }

    private void OnTrayAboutClick(object? sender, EventArgs e)
    {
        _mainViewModel?.Navigate(AppNavPage.About);
        ShowMainWindow();
    }

    private void OnTrayExitClick(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _mainWindow?.ExplicitExit();
            desktop.Shutdown();
        }
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        // 录屏收尾必须先于其他服务：停止录制才会回填 moov，退出前保证文件完整可播
        try
        {
            _mainViewModel?.RecordingVm.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _loggerService?.Error("退出时释放录屏服务失败", ex);
        }

        _mainViewModel?.HttpApiVm.Dispose();
        _muClipService?.Dispose();
        _idleSleepService?.Dispose();
        _hotkeyService?.Dispose();
        _coordinator?.Dispose();
    }
}