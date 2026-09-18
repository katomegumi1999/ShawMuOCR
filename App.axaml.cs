using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
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

namespace ShawMuOCR;

public partial class App : Application
{
    private const int HOTKEY_ID_OCR = 1;
    private const int HOTKEY_ID_CAPTURE = 2;
    private const int HOTKEY_ID_ANNOTATE = 3;
    private const int HOTKEY_ID_TRANSLATE = 4;

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

    private MainWindow? _mainWindow;
    private MainViewModel? _mainViewModel;
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
            _settingsService = new JsonSettingsService();
            _loggerService = new ShawMuOCR.Core.Logging.FileLoggerService(_settingsService);

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
            _clipboardService = new WindowsClipboardService();
            _modelProvider = new OcrModelProvider();
            _textSorter = new DefaultOcrTextSorter();
            _engineFactory = new OcrEngineFactory(_textSorter, _modelProvider);
            _memoryDiagnosticsService = new ModelMemoryDiagnosticsService(_settingsService, _modelProvider);

            var captureService = new WindowsScreenCaptureService();
            var selectionService = new WindowsNativeScreenSelectionService();
            _coordinator = new OcrCoordinator(captureService, _engineFactory, _clipboardService, _settingsService, selectionService, _loggerService);
            _hotkeyService = new WindowsGlobalHotkeyService();
            _idleSleepService = new IdleSleepService(_settingsService, _memoryDiagnosticsService, _coordinator);

            // 2. 注册全局快捷键（截图 OCR 与 截图复制）
            RegisterHotkeys(_settingsService.Current);
            _settingsService.SettingsChanged += OnSettingsChanged;

            // 3. 构建 ViewModel 与主窗口
            _mainViewModel = new MainViewModel(
                _coordinator,
                _clipboardService,
                _settingsService,
                CreateSettingsViewModel,
                CreateAboutViewModel,
                _loggerService);

            _mainWindow = new MainWindow(_mainViewModel);
            _coordinator.RequestShowMainWindow += ShowMainWindow;
            desktop.Exit += OnDesktopExit;

            // 4. 根据静默启动配置决定是否呈现主界面
            if (!_settingsService.Current.SilentStartup)
            {
                desktop.MainWindow = _mainWindow;
                _mainWindow.Show();
            }
            else
            {
                _loggerService?.Info("已开启静默启动：程序常驻系统托盘，未显示主界面。");
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        RegisterHotkeys(settings);
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

        if (_mainViewModel != null)
        {
            if (!ocrSuccess && !captureSuccess && !annotateSuccess && !translateSuccess)
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
            else if (!translateSuccess)
            {
                _mainViewModel.StatusMessage = $"⚠️ 选区翻译快捷键 {settings.TranslateHotkey} 注册失败(可能被占用)";
            }
            else if (_mainViewModel.StatusMessage.StartsWith("⚠️ 快捷键") || _mainViewModel.StatusMessage.StartsWith("⚠️ 截图") || _mainViewModel.StatusMessage.StartsWith("⚠️ 选区"))
            {
                _mainViewModel.StatusMessage = "就绪";
            }
        }
    }

    private SettingsViewModel CreateSettingsViewModel()
    {
        return new SettingsViewModel(_settingsService!, _startupService!, _hotkeyService!, _modelProvider!, CreateAboutViewModel, _loggerService);
    }

    private AboutViewModel CreateAboutViewModel()
    {
        return new AboutViewModel(_memoryDiagnosticsService!);
    }

    private void ShowMainWindow()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_mainWindow != null)
            {
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

    private void OnTrayTranslateClick(object? sender, EventArgs e)
    {
        _ = _coordinator?.StartTranslateSelectionAsync();
    }

    private void OnTrayOpenMainClick(object? sender, EventArgs e)
    {
        _mainViewModel?.Navigate(AppNavPage.Ocr);
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
        _idleSleepService?.Dispose();
        _hotkeyService?.Dispose();
        _coordinator?.Dispose();
    }
}