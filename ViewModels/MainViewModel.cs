using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShawMuOCR.Core.Clipboard;
using ShawMuOCR.Core.Editor;
using ShawMuOCR.Core.Logging;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Services;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.Core.Translation;
using ShawMuOCR.Views;
using ShawMuOCR.Windows.Win32;
using MuClip.Services;
using MuClip.ViewModels;
using ShawMuOCR.Services;
using SkiaSharp;
using UglyToad.PdfPig;

namespace ShawMuOCR.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly IOcrCoordinator _coordinator;
    private readonly IClipboardService _clipboardService;
    private readonly ISettingsService _settingsService;
    private readonly ILoggerService? _logger;
    private readonly Func<SettingsViewModel> _settingsVmFactory;
    private readonly Func<AboutViewModel>? _aboutVmFactory;

    public SettingsViewModel SettingsVm { get; }
    public AboutViewModel? AboutVm { get; }
    public TranslateViewModel TranslateVm { get; }
    public BatchOcrViewModel BatchVm { get; }
    public HttpApiViewModel HttpApiVm { get; }
    public ClipboardViewModel? ClipboardVm { get; private set; }
    public ThemeViewModel ThemeVm { get; }
    public LogViewModel LogVm { get; }
    private readonly IThemeService _themeService;
    private IMuClipService? _muClipService;

    public Bitmap? CustomBackgroundBitmap => _themeService.CurrentBackgroundBitmap;
    public bool HasCustomBackgroundImage => _themeService.HasBackgroundImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOcrPage))]
    [NotifyPropertyChangedFor(nameof(IsBatchPage))]
    [NotifyPropertyChangedFor(nameof(IsHttpApiPage))]
    [NotifyPropertyChangedFor(nameof(IsCapturePage))]
    [NotifyPropertyChangedFor(nameof(IsTranslatePage))]
    [NotifyPropertyChangedFor(nameof(IsClipboardPage))]
    [NotifyPropertyChangedFor(nameof(IsSettingsPage))]
    [NotifyPropertyChangedFor(nameof(IsAboutPage))]
    [NotifyPropertyChangedFor(nameof(IsThemePage))]
    [NotifyPropertyChangedFor(nameof(IsLogPage))]
    [NotifyPropertyChangedFor(nameof(CurrentPageTitle))]
    [NotifyPropertyChangedFor(nameof(AppTitleText))]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyPropertyChangedFor(nameof(AppLogo))]
    [NotifyPropertyChangedFor(nameof(WindowIcon))]
    private AppNavPage _currentPage = AppNavPage.Ocr;

    partial void OnCurrentPageChanged(AppNavPage value)
    {
        if (_settingsService != null)
        {
            var s = _settingsService.Current.Clone();
            if (s.LastActivePage != value.ToString())
            {
                s.LastActivePage = value.ToString();
                _settingsService.Save(s);
            }
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidebarWidth))]
    private bool _isSidebarExpanded = true;

    partial void OnIsSidebarExpandedChanged(bool value)
    {
        if (_settingsService != null)
        {
            var s = _settingsService.Current.Clone();
            if (s.IsSidebarExpanded != value)
            {
                s.IsSidebarExpanded = value;
                _settingsService.Save(s);
            }
        }
    }

    public double SidebarWidth => IsSidebarExpanded ? 190 : 56;

    public bool IsOcrPage => CurrentPage == AppNavPage.Ocr;
    public bool IsBatchPage => CurrentPage == AppNavPage.Batch;
    public bool IsHttpApiPage => CurrentPage == AppNavPage.HttpApi;
    public bool IsCapturePage => CurrentPage == AppNavPage.Capture;
    public bool IsTranslatePage => CurrentPage == AppNavPage.Translate;
    public bool IsClipboardPage => CurrentPage == AppNavPage.Clipboard;
    public bool IsSettingsPage => CurrentPage == AppNavPage.Settings;
    public bool IsAboutPage => CurrentPage == AppNavPage.About;
    public bool IsThemePage => CurrentPage == AppNavPage.Theme;
    public bool IsLogPage => CurrentPage == AppNavPage.Log;

    public bool IsClipboardEnabled => _settingsService.Current.EnableClipboard;
    public bool IsLoggingEnabled => _settingsService.Current.EnableLogging;

    public string CurrentPageTitle => CurrentPage switch
    {
        AppNavPage.Ocr => "OCR 识别",
        AppNavPage.Batch => "批量转换",
        AppNavPage.HttpApi => "HTTP 调用",
        AppNavPage.Capture => "截屏工具",
        AppNavPage.Translate => "文本翻译",
        AppNavPage.Clipboard => "小沐剪贴板",
        AppNavPage.Theme => "个性主题",
        AppNavPage.Settings => "系统设置",
        AppNavPage.About => "关于软件",
        AppNavPage.Log => "日志记录",
        _ => "小沐 OCR"
    };

    public string AppTitleText => IsClipboardPage ? "小沐剪贴板" : "小沐 OCR";
    public string WindowTitle => IsClipboardPage ? "小沐剪贴板" : "小沐 OCR";

    private static Bitmap? _ocrLogoBitmap;
    private static Bitmap? _muclipLogoBitmap;
    private static WindowIcon? _ocrWindowIcon;
    private static WindowIcon? _muclipWindowIcon;

    public Bitmap? AppLogo
    {
        get
        {
            try
            {
                if (IsClipboardPage)
                {
                    return _muclipLogoBitmap ??= new Bitmap(AssetLoader.Open(new Uri("avares://ShawMuOCR/Assets/MuClip.png")));
                }
                return _ocrLogoBitmap ??= new Bitmap(AssetLoader.Open(new Uri("avares://ShawMuOCR/Assets/logo.png")));
            }
            catch
            {
                return null;
            }
        }
    }

    public WindowIcon? WindowIcon
    {
        get
        {
            try
            {
                if (IsClipboardPage)
                {
                    return _muclipWindowIcon ??= new WindowIcon(AssetLoader.Open(new Uri("avares://ShawMuOCR/Assets/MuClip.ico")));
                }
                return _ocrWindowIcon ??= new WindowIcon(AssetLoader.Open(new Uri("avares://ShawMuOCR/Assets/logo.ico")));
            }
            catch
            {
                return null;
            }
        }
    }

    [ObservableProperty]
    private Bitmap? _previewImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CharCount))]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string _recognizedText = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "就绪";

    [ObservableProperty]
    private object? _topBarCustomContent;

    [ObservableProperty]
    private string _elapsedText = "耗时: -- ms";

    [ObservableProperty]
    private bool _isRecognizing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TopmostButtonText))]
    [NotifyPropertyChangedFor(nameof(TopmostTooltip))]
    private bool _isAlwaysOnTop;

    public string TopmostButtonText => IsAlwaysOnTop ? "已置顶" : "窗口置顶";
    public string TopmostTooltip => IsAlwaysOnTop ? "当前已置顶（点击取消置顶）" : "保持主窗口最前置顶（点击开启置顶）";

    [RelayCommand]
    public void ToggleAlwaysOnTop()
    {
        IsAlwaysOnTop = !IsAlwaysOnTop;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHotkey))]
    private string _currentHotkey = "Alt+Z";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCaptureImageHotkey))]
    private string _currentCaptureImageHotkey = "Alt+X";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnnotateHotkey))]
    private string _currentAnnotateHotkey = "Alt+C";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTranslateHotkey))]
    private string _currentTranslateHotkey = "Alt+T";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOcrPopupHotkey))]
    private string _currentOcrPopupHotkey = "Alt+D";

    [ObservableProperty]
    private string _currentModel = "PP-OCRv6_small";

    public bool HasHotkey => !string.IsNullOrWhiteSpace(CurrentHotkey) && CurrentHotkey != "无" && CurrentHotkey != "None";
    public bool HasCaptureImageHotkey => !string.IsNullOrWhiteSpace(CurrentCaptureImageHotkey) && CurrentCaptureImageHotkey != "无" && CurrentCaptureImageHotkey != "None";
    public bool HasAnnotateHotkey => !string.IsNullOrWhiteSpace(CurrentAnnotateHotkey) && CurrentAnnotateHotkey != "无" && CurrentAnnotateHotkey != "None";
    public bool HasTranslateHotkey => !string.IsNullOrWhiteSpace(CurrentTranslateHotkey) && CurrentTranslateHotkey != "无" && CurrentTranslateHotkey != "None";
    public bool HasOcrPopupHotkey => !string.IsNullOrWhiteSpace(CurrentOcrPopupHotkey) && CurrentOcrPopupHotkey != "无" && CurrentOcrPopupHotkey != "None";

    public int CharCount => RecognizedText?.Length ?? 0;
    public bool HasResult => !string.IsNullOrEmpty(RecognizedText);

    public MainViewModel(
        IOcrCoordinator coordinator,
        IClipboardService clipboardService,
        ISettingsService settingsService,
        Func<SettingsViewModel> settingsVmFactory,
        Func<AboutViewModel>? aboutVmFactory = null,
        ILoggerService? logger = null,
        ITranslationService? translationService = null,
        IMuClipService? muClipService = null,
        IOcrEngineFactory? engineFactory = null,
        BatchOcrViewModel? batchVm = null,
        HttpApiViewModel? httpApiVm = null,
        IThemeService? themeService = null,
        ThemeViewModel? themeVm = null)
    {
        _coordinator = coordinator;
        _clipboardService = clipboardService;
        _settingsService = settingsService;
        _logger = logger;
        _settingsVmFactory = settingsVmFactory;
        _aboutVmFactory = aboutVmFactory;
        _muClipService = muClipService;

        _themeService = themeService ?? new ThemeService(settingsService);
        ThemeVm = themeVm ?? new ThemeViewModel(_themeService);
        _themeService.ThemeVisualChanged += () =>
        {
            OnPropertyChanged(nameof(CustomBackgroundBitmap));
            OnPropertyChanged(nameof(HasCustomBackgroundImage));
        };
        _themeService.InitializeTheme();

        SettingsVm = settingsVmFactory();
        AboutVm = aboutVmFactory?.Invoke();
        if (AboutVm != null)
        {
            SettingsVm.RequestOpenAbout += _ => Navigate(AppNavPage.About);
        }

        BatchVm = batchVm ?? new BatchOcrViewModel(
            engineFactory ?? new ShawMuOCR.OCR.OcrEngineFactory(),
            settingsService,
            logger);

        HttpApiVm = httpApiVm ?? new HttpApiViewModel(
            engineFactory ?? new ShawMuOCR.OCR.OcrEngineFactory(),
            settingsService,
            clipboardService,
            logger);

        if (_muClipService != null)
        {
            InitClipboardVm(_muClipService);
        }

        var transService = translationService ?? (coordinator is ShawMuOCR.Services.OcrCoordinator oc ? oc.TranslationService : new TranslationService(settingsService));
        TranslateVm = new TranslateViewModel(transService, settingsService, clipboardService, coordinator);
        LogVm = new LogViewModel(logger, _settingsService);

        _currentHotkey = _settingsService.Current.Hotkey;
        _currentCaptureImageHotkey = _settingsService.Current.CaptureImageHotkey;
        _currentAnnotateHotkey = _settingsService.Current.AnnotateHotkey;
        _currentTranslateHotkey = _settingsService.Current.TranslateHotkey;
        _currentOcrPopupHotkey = _settingsService.Current.OcrPopupHotkey;
        _currentModel = _settingsService.Current.Model;
        _isSidebarExpanded = _settingsService.Current.IsSidebarExpanded;

        if (Enum.TryParse<AppNavPage>(_settingsService.Current.LastActivePage, true, out var savedPage))
        {
            _currentPage = savedPage;
        }

        _coordinator.StateChanged += OnCoordinatorStateChanged;
        _coordinator.SessionCompleted += OnSessionCompleted;
        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    private void OnCoordinatorStateChanged(OcrState state, string? message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsRecognizing = state is OcrState.Recognizing or OcrState.Selecting;
            StatusMessage = message ?? state switch
            {
                OcrState.Idle => "就绪",
                OcrState.Selecting => "正在框选截屏...",
                OcrState.Recognizing => "正在识别中...",
                OcrState.Success => "✓ 识别完成",
                OcrState.Failed => "✕ 识别失败",
                _ => "就绪"
            };
        });
    }

    private void OnSessionCompleted(OcrSession session)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (session.Result != null)
            {
                RecognizedText = session.Result.Text;
                ElapsedText = $"耗时: {session.Result.Elapsed.TotalMilliseconds:F0} ms";
            }

            if (session.Image != null)
            {
                try
                {
                    SetCurrentBitmap(session.Image.Bitmap);
                    UpdatePreviewBitmap(session.Image.Bitmap);
                }
                catch (Exception ex)
                {
                    _logger?.Error("更新会话预览图异常", ex);
                }
            }
        });
    }

    public void AttachMuClipService(IMuClipService service)
    {
        _muClipService = service;
        if (ClipboardVm == null)
        {
            InitClipboardVm(_muClipService);
            OnPropertyChanged(nameof(ClipboardVm));
        }
        OnPropertyChanged(nameof(IsClipboardEnabled));
    }

    public void DetachMuClipService()
    {
        ClipboardVm?.SetHostViewActive(false);
        _muClipService = null;
        ClipboardVm = null;
        OnPropertyChanged(nameof(ClipboardVm));
        OnPropertyChanged(nameof(IsClipboardEnabled));
    }

    private void InitClipboardVm(IMuClipService service)
    {
        ClipboardVm = new ClipboardViewModel(service);
        ClipboardVm.SetHostViewActive(CurrentPage == AppNavPage.Clipboard);
        ClipboardVm.PanelBackgroundColor = _settingsService.Current.ClipboardPanelBackground;
        ClipboardVm.PanelBackgroundChanged += newColor =>
        {
            var updated = _settingsService.Current.Clone();
            updated.ClipboardPanelBackground = newColor;
            _settingsService.Save(updated);
        };
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        Dispatcher.UIThread.Post(() =>
        {
            CurrentHotkey = settings.Hotkey;
            CurrentCaptureImageHotkey = settings.CaptureImageHotkey;
            CurrentAnnotateHotkey = settings.AnnotateHotkey;
            CurrentTranslateHotkey = settings.TranslateHotkey;
            CurrentOcrPopupHotkey = settings.OcrPopupHotkey;
            CurrentModel = settings.Model;
            OnPropertyChanged(nameof(IsClipboardEnabled));
            OnPropertyChanged(nameof(IsLoggingEnabled));
            if (!settings.EnableClipboard && CurrentPage == AppNavPage.Clipboard)
            {
                Navigate(AppNavPage.Ocr);
            }
            if (!settings.EnableLogging && CurrentPage == AppNavPage.Log)
            {
                Navigate(AppNavPage.Ocr);
            }
        });
    }

    [RelayCommand]
    private async Task StartCaptureAsync()
    {
        User32.AllowSetForegroundWindow(User32.ASFW_ANY);
        await _coordinator.StartScreenCaptureAsync();
    }

    [RelayCommand]
    private async Task StartCaptureImageAsync()
    {
        User32.AllowSetForegroundWindow(User32.ASFW_ANY);
        await _coordinator.StartCaptureImageAsync();
    }

    [RelayCommand]
    private async Task StartAnnotateAsync()
    {
        User32.AllowSetForegroundWindow(User32.ASFW_ANY);
        await _coordinator.StartAnnotateScreenshotAsync();
    }

    [RelayCommand]
    private async Task StartTranslateSelectionAsync()
    {
        User32.AllowSetForegroundWindow(User32.ASFW_ANY);
        await _coordinator.StartTranslateSelectionAsync();
    }

    [RelayCommand]
    private async Task StartOcrPopupAsync()
    {
        User32.AllowSetForegroundWindow(User32.ASFW_ANY);
        await _coordinator.StartOcrPopupAsync();
    }

    [RelayCommand]
    private async Task CopyTextAsync()
    {
        if (!string.IsNullOrEmpty(RecognizedText))
        {
            bool success = await _clipboardService.SetTextAsync(RecognizedText);
            StatusMessage = success ? "✓ 已复制到剪贴板" : "✕ 复制失败";
        }
    }

    [RelayCommand]
    private async Task SavePreviewImageAsync()
    {
        var sessionImage = _coordinator.CurrentSession?.Image;
        if (sessionImage == null || sessionImage.Bitmap == null || sessionImage.Bitmap.IsNull)
        {
            StatusMessage = "当前暂无图像可供保存";
            return;
        }

        try
        {
            // 复制一份位图副本供保存对话框使用，避免生命周期冲突
            using var copiedImage = new OcrImage(sessionImage.Bitmap.Copy());
            using var doc = new ImageDocument(copiedImage);

            var dialog = new SaveImageDialog(doc, _settingsService);
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow != null)
            {
                await dialog.ShowDialog<bool>(desktop.MainWindow);
            }
        }
        catch (Exception ex)
        {
            _logger?.Error("主界面保存预览图异常", ex);
            StatusMessage = $"保存图片出错: {ex.Message}";
        }
    }

    private SKBitmap? _currentSkBitmap;
    private readonly object _bitmapLock = new();

    private void SetCurrentBitmap(SKBitmap? bmp)
    {
        lock (_bitmapLock)
        {
            _currentSkBitmap?.Dispose();
            _currentSkBitmap = bmp?.Copy();
        }
    }

    [RelayCommand]
    public async Task RotatePreviewImageAsync()
    {
        if (IsRecognizing) return;

        SKBitmap? rotated;
        lock (_bitmapLock)
        {
            if (_currentSkBitmap == null) return;
            rotated = ImageOrientationHelper.Rotate(_currentSkBitmap, 90);
            _currentSkBitmap.Dispose();
            _currentSkBitmap = rotated.Copy();
        }

        UpdatePreviewBitmap(rotated);
        StatusMessage = "已顺时针旋转 90°，正在重新识别...";

        var ocrImage = new OcrImage(rotated);
        await _coordinator.RunOcrAsync(ocrImage);
    }

    [RelayCommand]
    private void Clear()
    {
        RecognizedText = string.Empty;
        PreviewImage?.Dispose();
        PreviewImage = null;
        SetCurrentBitmap(null);
        ElapsedText = "耗时: -- ms";
        StatusMessage = "已清空";
    }

    [RelayCommand]
    public void ToggleSidebar()
    {
        IsSidebarExpanded = !IsSidebarExpanded;
    }

    [RelayCommand]
    public void Navigate(AppNavPage page)
    {
        if (page == AppNavPage.Clipboard && !IsClipboardEnabled)
        {
            return;
        }

        if (page == AppNavPage.Log && !IsLoggingEnabled)
        {
            return;
        }

        if (CurrentPage == AppNavPage.Settings && page != AppNavPage.Settings)
        {
            SettingsVm.CancelRecording();
            SettingsVm.FlushAutoSave();
        }

        CurrentPage = page;
        ClipboardVm?.SetHostViewActive(page == AppNavPage.Clipboard);

        if (page == AppNavPage.About)
        {
            AboutVm?.RefreshMemoryCommand.Execute(null);
        }
        else if (page == AppNavPage.Clipboard)
        {
            ClipboardVm?.LoadItems();
        }
        else if (page == AppNavPage.Log)
        {
            LogVm?.Refresh();
        }
    }

    [RelayCommand]
    private void NavigateToOcr() => Navigate(AppNavPage.Ocr);

    [RelayCommand]
    private void NavigateToBatch() => Navigate(AppNavPage.Batch);

    [RelayCommand]
    private void NavigateToHttpApi() => Navigate(AppNavPage.HttpApi);

    [RelayCommand]
    private void NavigateToCapture() => Navigate(AppNavPage.Capture);

    [RelayCommand]
    private void NavigateToTranslate() => Navigate(AppNavPage.Translate);

    [RelayCommand]
    private void NavigateToClipboard()
    {
        if (!IsClipboardEnabled)
        {
            return;
        }
        Navigate(AppNavPage.Clipboard);
    }

    [RelayCommand]
    private void NavigateToTheme() => Navigate(AppNavPage.Theme);

    [RelayCommand]
    private void NavigateToSettings() => Navigate(AppNavPage.Settings);

    [RelayCommand]
    private void NavigateToAbout() => Navigate(AppNavPage.About);

    [RelayCommand]
    private void NavigateToLog()
    {
        if (!IsLoggingEnabled)
        {
            return;
        }
        Navigate(AppNavPage.Log);
    }

    public event Action? RequestOpenFilePicker;

    [RelayCommand]
    public void OpenFile()
    {
        RequestOpenFilePicker?.Invoke();
    }

    [RelayCommand]
    public async Task RecognizeFileAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            StatusMessage = "文件不存在或路径无效";
            return;
        }

        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext == ".pdf")
        {
            await RecognizePdfFileAsync(filePath);
        }
        else
        {
            await RecognizeImageFileAsync(filePath);
        }
    }

    public async Task RecognizeImageFileAsync(string filePath)
    {
        try
        {
            StatusMessage = "正在读取图片...";
            IsRecognizing = true;

            using var stream = File.OpenRead(filePath);
            using var orientedBitmap = ImageOrientationHelper.DecodeAndOrient(stream, ImageRotationMode.Auto);
            if (orientedBitmap == null)
            {
                StatusMessage = "图片解码失败，请确认文件格式";
                IsRecognizing = false;
                return;
            }

            SetCurrentBitmap(orientedBitmap);
            UpdatePreviewBitmap(orientedBitmap);

            // 注意：OcrCoordinator 会接管并最终释放 session 中的图片，此处不可 using 释放
            var ocrImage = new OcrImage(orientedBitmap.Copy());
            await _coordinator.RunOcrAsync(ocrImage);
        }
        catch (Exception ex)
        {
            _logger?.Error($"图片识别失败: {filePath}", ex);
            StatusMessage = $"图片识别失败: {ex.Message}";
            IsRecognizing = false;
        }
    }

    public async Task RecognizePdfFileAsync(string filePath)
    {
        try
        {
            StatusMessage = "正在解析 PDF 文档...";
            IsRecognizing = true;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var sb = new StringBuilder();
            Bitmap? firstPagePreview = null;
            int totalPages = 0;

            await Task.Run(async () =>
            {
                using var document = PdfDocument.Open(filePath);
                totalPages = document.NumberOfPages;

                for (int pageNum = 1; pageNum <= totalPages; pageNum++)
                {
                    var page = document.GetPage(pageNum);
                    int pageRotation = (int)page.Rotation.Value;
                    int targetRotation = ImageOrientationHelper.ResolvePdfRotation(pageRotation, ImageRotationMode.Auto);

                    var pageText = page.Text?.Trim();
                    var images = page.GetImages().ToList();

                    sb.AppendLine($"--- 第 {pageNum} 页 ---");

                    if (!string.IsNullOrEmpty(pageText))
                    {
                        sb.AppendLine(pageText);
                    }

                    if (images.Count > 0)
                    {
                        foreach (var img in images)
                        {
                            try
                            {
                                byte[]? bytes = null;
                                if (img.TryGetPng(out var png))
                                {
                                    bytes = png;
                                }
                                else
                                {
                                    bytes = img.RawBytes.ToArray();
                                }

                                if (bytes != null && bytes.Length > 0)
                                {
                                    using var rawBmp = SKBitmap.Decode(bytes);
                                    if (rawBmp != null)
                                    {
                                        using var skBmp = ImageOrientationHelper.Rotate(rawBmp, targetRotation);
                                        if (firstPagePreview == null)
                                        {
                                            firstPagePreview = CreateAvaloniaBitmap(skBmp);
                                            SetCurrentBitmap(skBmp);
                                        }

                                        using var ocrImg = new OcrImage(skBmp.Copy());
                                        var ocrRes = await _coordinator.RecognizeDirectAsync(ocrImg);
                                        if (ocrRes.IsSuccess && !string.IsNullOrWhiteSpace(ocrRes.Text))
                                        {
                                            sb.AppendLine(ocrRes.Text);
                                        }
                                    }
                                }
                            }
                            catch
                            {
                                // 容错处理单张图像解析异常
                            }
                        }
                    }

                    sb.AppendLine();
                }
            });

            sw.Stop();
            IsRecognizing = false;

            if (firstPagePreview != null)
            {
                var old = PreviewImage;
                PreviewImage = firstPagePreview;
                old?.Dispose();
            }

            string resultText = sb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(resultText))
            {
                StatusMessage = $"✓ PDF 解析完成（共 {totalPages} 页，未提取到有效文字或图像）";
            }
            else
            {
                RecognizedText = resultText;
                ElapsedText = $"耗时: {sw.ElapsedMilliseconds} ms (共 {totalPages} 页)";
                StatusMessage = $"✓ PDF 识别完成 (共 {totalPages} 页)";

                if (_settingsService.Current.AutoCopy)
                {
                    await _clipboardService.SetTextAsync(resultText);
                }

                if (_settingsService.Current.ShowOcrNotification)
                {
                    ToastNotificationWindow.Show(resultText, sw.Elapsed);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Error($"PDF 识别异常: {filePath}", ex);
            StatusMessage = $"PDF 识别异常: {ex.Message}";
            IsRecognizing = false;
        }
    }

    private static Bitmap? CreateAvaloniaBitmap(SKBitmap? skBitmap)
    {
        if (skBitmap == null || skBitmap.IsNull || skBitmap.Handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var wb = new WriteableBitmap(
                new PixelSize(skBitmap.Width, skBitmap.Height),
                new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888,
                Avalonia.Platform.AlphaFormat.Premul);

            using (var fb = wb.Lock())
            {
                if (fb.Address != IntPtr.Zero)
                {
                    var info = new SKImageInfo(skBitmap.Width, skBitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                    using var surface = SKSurface.Create(info, fb.Address, fb.RowBytes);
                    if (surface != null)
                    {
                        surface.Canvas.DrawBitmap(skBitmap, 0, 0, SKSamplingOptions.Default);
                    }
                    else
                    {
                        // 备用：按行跨度安全逐行拷贝
                        IntPtr pixels = skBitmap.GetPixels();
                        if (pixels != IntPtr.Zero)
                        {
                            unsafe
                            {
                                int height = skBitmap.Height;
                                int srcRowBytes = skBitmap.RowBytes;
                                int dstRowBytes = fb.RowBytes;
                                int bytesPerRow = Math.Min(srcRowBytes, dstRowBytes);
                                byte* pSrc = (byte*)pixels;
                                byte* pDst = (byte*)fb.Address;
                                for (int y = 0; y < height; y++)
                                {
                                    Buffer.MemoryCopy(pSrc + (y * srcRowBytes), pDst + (y * dstRowBytes), dstRowBytes, bytesPerRow);
                                }
                            }
                        }
                    }
                }
            }
            return wb;
        }
        catch
        {
            // 兜底方案：通过内存 PNG 流加载 Avalonia Bitmap
            try
            {
                using var image = SKImage.FromBitmap(skBitmap);
                if (image != null)
                {
                    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                    if (data != null)
                    {
                        using var stream = data.AsStream();
                        return new Bitmap(stream);
                    }
                }
            }
            catch
            {
                // ignore
            }
            return null;
        }
    }

    private void UpdatePreviewBitmap(SKBitmap? skBitmap)
    {
        if (skBitmap == null) return;

        try
        {
            var wb = CreateAvaloniaBitmap(skBitmap);
            if (wb != null)
            {
                var old = PreviewImage;
                PreviewImage = wb;
                old?.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger?.Error("更新主界面预览图异常", ex);
        }
    }

    [RelayCommand]
    private void OpenSettings()
    {
        Navigate(AppNavPage.Settings);
    }

    [RelayCommand]
    private void OpenAbout()
    {
        Navigate(AppNavPage.About);
    }
}
