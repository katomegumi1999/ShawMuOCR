using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShawMuOCR.Core.Hotkey;
using ShawMuOCR.Core.Logging;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.Core.Translation;

namespace ShawMuOCR.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;
    private readonly IStartupService _startupService;
    private readonly IGlobalHotkeyService _hotkeyService;
    private readonly IOcrModelProvider _modelProvider;
    private readonly ILoggerService? _loggerService;
    private readonly Func<AboutViewModel>? _aboutVmFactory;
    private bool _isInitializing = true;
    private CancellationTokenSource? _autoSaveCts;

    [ObservableProperty]
    private string _hotkey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecordingAnyHotkey))]
    private bool _isRecordingHotkey;

    [ObservableProperty]
    private string _captureImageHotkey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecordingAnyHotkey))]
    private bool _isRecordingCaptureImageHotkey;

    [ObservableProperty]
    private string _annotateHotkey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecordingAnyHotkey))]
    private bool _isRecordingAnnotateHotkey;

    [ObservableProperty]
    private string _translateHotkey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecordingAnyHotkey))]
    private bool _isRecordingTranslateHotkey;

    public bool IsRecordingAnyHotkey => IsRecordingHotkey || IsRecordingCaptureImageHotkey || IsRecordingAnnotateHotkey || IsRecordingTranslateHotkey;

    // ==========================================
    // 翻译服务配置属性
    // ==========================================
    [ObservableProperty]
    private string _translateProvider = "Baidu";

    [ObservableProperty]
    private LanguageItem _selectedTranslateSourceLang;

    [ObservableProperty]
    private LanguageItem _selectedTranslateTargetLang;

    public IReadOnlyList<LanguageItem> TranslateSourceLanguages { get; } = TranslationLanguages.All;
    public IReadOnlyList<LanguageItem> TranslateTargetLanguages { get; } = TranslationLanguages.AllTargets;
    public string[] AvailableTranslateProviders { get; } = ["Baidu", "Google", "OpenAI"];

    // 百度翻译
    [ObservableProperty]
    private string _baiduAppId = string.Empty;

    [ObservableProperty]
    private string _baiduSecretKey = string.Empty;

    [ObservableProperty]
    private string _baiduApiKey = string.Empty;

    [ObservableProperty]
    private int _baiduMonthlyLimit = 1000000;

    [ObservableProperty]
    private int _baiduUsedCount = 0;

    // 谷歌翻译
    [ObservableProperty]
    private string _googleApiKey = string.Empty;

    [ObservableProperty]
    private int _googleMonthlyLimit = 500000;

    [ObservableProperty]
    private int _googleUsedCount = 0;

    // OpenAI
    [ObservableProperty]
    private string _openAiApiEndpoint = "https://api.openai.com/v1/chat/completions";

    [ObservableProperty]
    private string _openAiApiKey = string.Empty;

    [ObservableProperty]
    private string _openAiModel = "gpt-4o-mini";

    [ObservableProperty]
    private int _openAiMonthlyLimit = 200000;

    [ObservableProperty]
    private int _openAiUsedCount = 0;

    [ObservableProperty]
    private bool _autoCopy;

    [ObservableProperty]
    private bool _silentMode;

    [ObservableProperty]
    private bool _silentStartup;

    [ObservableProperty]
    private bool _showOcrNotification;

    [ObservableProperty]
    private bool _showCaptureImageNotification;

    public bool ShowNotificationBadge
    {
        get => ShowOcrNotification;
        set
        {
            ShowOcrNotification = value;
            ShowCaptureImageNotification = value;
        }
    }

    [ObservableProperty]
    private bool _showWindowAfterOcr;

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedEmbeddedModel))]
    private string _selectedModel = "PP-OCRv6_small";

    public string SelectedEmbeddedModel
    {
        get => SelectedModel;
        set => SelectedModel = value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelSelectionHint))]
    [NotifyPropertyChangedFor(nameof(UseExternalModel))]
    [NotifyPropertyChangedFor(nameof(IsEmbeddedSelected))]
    private bool _scanExternalModels;

    partial void OnScanExternalModelsChanged(bool value)
    {
        LoadModels();
        OnPropertyChanged(nameof(ModelSelectionHint));
        OnPropertyChanged(nameof(UseExternalModel));
        OnPropertyChanged(nameof(IsEmbeddedSelected));
    }

    public bool UseExternalModel
    {
        get => ScanExternalModels;
        set => ScanExternalModels = value;
    }

    public string ModelSelectionHint => ScanExternalModels
        ? "已开启外部模型识别，可直接在右侧下拉框选用内置模型或 models/ 下的自定义 Paddle 模型"
        : "使用内置纯 C# SIMD 高精度推理模型，开箱即用，零文件依赖";

    public bool IsEmbeddedSelected => !ScanExternalModels;
    public bool IsExternalLocalSelected => false;
    public bool IsExternalWebApiSelected => false;

    [ObservableProperty]
    private string _selectedExternalModelTypeDisplay = "外部本地目录 (ONNX)";

    public string SelectedExternalModelType
    {
        get => SelectedExternalModelTypeDisplay.Contains("Web API", StringComparison.OrdinalIgnoreCase) ? "WebApi" : "LocalDirectory";
        set
        {
            SelectedExternalModelTypeDisplay = string.Equals(value, "WebApi", StringComparison.OrdinalIgnoreCase)
                ? "外部 Web API 接口 (HTTP)"
                : "外部本地目录 (ONNX)";
        }
    }

    [ObservableProperty]
    private string _externalModelPath = "models/v6";

    [ObservableProperty]
    private string _externalApiUrl = "http://127.0.0.1:8080/ocr";

    [ObservableProperty]
    private bool _enableAngleDetection;

    [ObservableProperty]
    private int _maxSideLen;

    [ObservableProperty]
    private bool _enableIdleSleep;

    [ObservableProperty]
    private int _idleSleepMinutes;

    [ObservableProperty]
    private bool _enableLogging;

    public string SettingsFilePath => _settingsService.SettingsFilePath;
    public string LogDirectory => _loggerService?.LogDirectory ?? string.Empty;
    public string CurrentLogFilePath => _loggerService?.CurrentLogFilePath ?? string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public ObservableCollection<string> AvailableModels { get; } = new();
    public ObservableCollection<string> AvailableEmbeddedModels { get; } = new();
    public ObservableCollection<string> AvailableExternalModelTypes { get; } = new()
    {
        "外部本地目录 (ONNX)",
        "外部 Web API 接口 (HTTP)"
    };
    public ObservableCollection<int> AvailableMaxSideLens { get; } = new();
    public ObservableCollection<int> AvailableIdleSleepMinutes { get; } = new();

    public event Action? RequestClose;
    public event Action<AboutViewModel>? RequestOpenAbout;
    public event Func<Task<string?>>? RequestPickModelFolder;

    public SettingsViewModel(
        ISettingsService settingsService,
        IStartupService startupService,
        IGlobalHotkeyService hotkeyService,
        IOcrModelProvider modelProvider,
        Func<AboutViewModel>? aboutVmFactory = null,
        ILoggerService? loggerService = null)
    {
        _settingsService = settingsService;
        _startupService = startupService;
        _hotkeyService = hotkeyService;
        _modelProvider = modelProvider;
        _aboutVmFactory = aboutVmFactory;
        _loggerService = loggerService;

        var current = _settingsService.Current;
        _hotkey = current.Hotkey;
        _captureImageHotkey = current.CaptureImageHotkey;
        _annotateHotkey = current.AnnotateHotkey;
        _autoCopy = current.AutoCopy;
        _silentMode = current.SilentMode;
        _silentStartup = current.SilentStartup;
        _showOcrNotification = current.ShowOcrNotification;
        _showCaptureImageNotification = current.ShowCaptureImageNotification;
        _showWindowAfterOcr = current.ShowWindowAfterOcr;
        _startWithWindows = _startupService.IsEnabled();
        _selectedModel = current.Model;
        _scanExternalModels = current.ScanExternalModels;
        _externalModelPath = current.ExternalModelPath;
        _externalApiUrl = current.ExternalApiUrl;
        SelectedExternalModelType = current.ExternalModelType;
        _enableAngleDetection = current.EnableAngleDetection;
        _maxSideLen = current.MaxSideLen;
        _enableIdleSleep = current.EnableIdleSleep;
        _idleSleepMinutes = current.IdleSleepMinutes;
        _enableLogging = current.EnableLogging;

        _translateHotkey = current.TranslateHotkey;
        _translateProvider = current.TranslateProvider;
        _selectedTranslateSourceLang = TranslateSourceLanguages.FirstOrDefault(x => x.Code == current.TranslateSourceLang) ?? TranslateSourceLanguages[0];
        _selectedTranslateTargetLang = TranslateTargetLanguages.FirstOrDefault(x => x.Code == current.TranslateTargetLang) ?? TranslateTargetLanguages[0];

        _baiduAppId = current.BaiduAppId;
        _baiduSecretKey = current.BaiduSecretKey;
        _baiduApiKey = current.BaiduApiKey;
        _baiduMonthlyLimit = current.BaiduMonthlyLimit;
        _baiduUsedCount = current.BaiduUsedCount;

        _googleApiKey = current.GoogleApiKey;
        _googleMonthlyLimit = current.GoogleMonthlyLimit;
        _googleUsedCount = current.GoogleUsedCount;

        _openAiApiEndpoint = current.OpenAiApiEndpoint;
        _openAiApiKey = current.OpenAiApiKey;
        _openAiModel = current.OpenAiModel;
        _openAiMonthlyLimit = current.OpenAiMonthlyLimit;
        _openAiUsedCount = current.OpenAiUsedCount;

        LoadModels();
        if (AvailableModels.Contains(current.Model))
        {
            SelectedModel = current.Model;
        }
        LoadMaxSideLens();
        LoadIdleSleepMinutes();
        StatusMessage = "⚡ 修改设置后将实时自动保存";
        _isInitializing = false;
    }

    private void LoadIdleSleepMinutes()
    {
        AvailableIdleSleepMinutes.Clear();
        AvailableIdleSleepMinutes.Add(1);
        AvailableIdleSleepMinutes.Add(2);
        AvailableIdleSleepMinutes.Add(3);
        AvailableIdleSleepMinutes.Add(5);
        AvailableIdleSleepMinutes.Add(10);
        AvailableIdleSleepMinutes.Add(15);

        if (!AvailableIdleSleepMinutes.Contains(IdleSleepMinutes))
        {
            IdleSleepMinutes = 3;
        }
    }

    private void LoadMaxSideLens()
    {
        AvailableMaxSideLens.Clear();
        AvailableMaxSideLens.Add(960);
        AvailableMaxSideLens.Add(1024);
        AvailableMaxSideLens.Add(1280);
        AvailableMaxSideLens.Add(1600);
        AvailableMaxSideLens.Add(2000);

        if (!AvailableMaxSideLens.Contains(MaxSideLen))
        {
            MaxSideLen = 1280;
        }
    }

    private void LoadModels()
    {
        string previousSelection = SelectedModel;
        AvailableModels.Clear();
        AvailableEmbeddedModels.Clear();

        var models = _modelProvider.GetAvailableModels(ScanExternalModels);
        foreach (var m in models)
        {
            AvailableModels.Add(m);
            AvailableEmbeddedModels.Add(m);
        }

        if (AvailableModels.Contains(previousSelection))
        {
            SelectedModel = previousSelection;
        }
        else if (AvailableModels.Count > 0)
        {
            SelectedModel = AvailableModels[0];
        }
        else
        {
            SelectedModel = "PP-OCRv6_small";
        }
    }

    [RelayCommand]
    public void RefreshModels()
    {
        LoadModels();
        int extCount = AvailableModels.Count(m => m.Contains("(外部)"));
        StatusMessage = $"模型列表已刷新，共加载 {AvailableModels.Count} 个可用模型 (含 {extCount} 个外部扫描模型)";
    }

    [RelayCommand]
    public void OpenModelsFolder()
    {
        try
        {
            string dir = _modelProvider.BaseModelsDirectory;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开 models 目录失败: {ex.Message}";
        }
    }

    private static bool IsDisabledHotkey(string? hk) =>
        string.IsNullOrWhiteSpace(hk) || hk.Equals("无", StringComparison.OrdinalIgnoreCase) || hk.Equals("None", StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private void StartRecordHotkey()
    {
        _hotkeyService.IsPaused = true;
        IsRecordingCaptureImageHotkey = false;
        IsRecordingAnnotateHotkey = false;
        IsRecordingHotkey = true;
        StatusMessage = "请按下【截图 OCR】快捷键 (按 Backspace 清空，按 Esc 取消)...";
    }

    public void OnHotkeyRecorded(HotkeyGesture gesture)
    {
        IsRecordingHotkey = false;
        _hotkeyService.IsPaused = false;
        Hotkey = gesture.ToString();
        StatusMessage = $"✓ 已设置截图 OCR 快捷键: {Hotkey}";
    }

    [RelayCommand]
    private void ClearHotkey()
    {
        IsRecordingHotkey = false;
        _hotkeyService.IsPaused = false;
        Hotkey = "无";
        StatusMessage = "截图 OCR 快捷键已清空(已禁用)";
    }

    [RelayCommand]
    private void StartRecordCaptureImageHotkey()
    {
        _hotkeyService.IsPaused = true;
        IsRecordingHotkey = false;
        IsRecordingAnnotateHotkey = false;
        IsRecordingCaptureImageHotkey = true;
        StatusMessage = "请按下【截图复制】快捷键 (按 Backspace 清空，按 Esc 取消)...";
    }

    public void OnCaptureImageHotkeyRecorded(HotkeyGesture gesture)
    {
        IsRecordingCaptureImageHotkey = false;
        _hotkeyService.IsPaused = false;
        CaptureImageHotkey = gesture.ToString();
        StatusMessage = $"✓ 已设置截图复制快捷键: {CaptureImageHotkey}";
    }

    [RelayCommand]
    private void ClearCaptureImageHotkey()
    {
        IsRecordingCaptureImageHotkey = false;
        _hotkeyService.IsPaused = false;
        CaptureImageHotkey = "无";
        StatusMessage = "截图复制快捷键已清空(已禁用)";
    }

    [RelayCommand]
    private void StartRecordAnnotateHotkey()
    {
        _hotkeyService.IsPaused = true;
        IsRecordingHotkey = false;
        IsRecordingCaptureImageHotkey = false;
        IsRecordingAnnotateHotkey = true;
        StatusMessage = "请按下【截图标注】快捷键 (按 Backspace 清空，按 Esc 取消)...";
    }

    public void OnAnnotateHotkeyRecorded(HotkeyGesture gesture)
    {
        IsRecordingAnnotateHotkey = false;
        _hotkeyService.IsPaused = false;
        AnnotateHotkey = gesture.ToString();
        StatusMessage = $"✓ 已设置截图标注快捷键: {AnnotateHotkey}";
    }

    [RelayCommand]
    private void ClearAnnotateHotkey()
    {
        IsRecordingAnnotateHotkey = false;
        _hotkeyService.IsPaused = false;
        AnnotateHotkey = "无";
        StatusMessage = "截图标注快捷键已清空(已禁用)";
    }

    [RelayCommand]
    private void StartRecordTranslateHotkey()
    {
        _hotkeyService.IsPaused = true;
        IsRecordingHotkey = false;
        IsRecordingCaptureImageHotkey = false;
        IsRecordingAnnotateHotkey = false;
        IsRecordingTranslateHotkey = true;
        StatusMessage = "请按下【选区翻译】快捷键 (按 Backspace 清空，按 Esc 取消)...";
    }

    public void OnTranslateHotkeyRecorded(HotkeyGesture gesture)
    {
        IsRecordingTranslateHotkey = false;
        _hotkeyService.IsPaused = false;
        TranslateHotkey = gesture.ToString();
        StatusMessage = $"✓ 已设置选区翻译快捷键: {TranslateHotkey}";
    }

    [RelayCommand]
    private void ClearTranslateHotkey()
    {
        IsRecordingTranslateHotkey = false;
        _hotkeyService.IsPaused = false;
        TranslateHotkey = "无";
        StatusMessage = "选区翻译快捷键已清空(已禁用)";
    }

    [RelayCommand]
    public void ResetBaiduUsedCount()
    {
        BaiduUsedCount = 0;
        TriggerAutoSave(debounce: false);
        StatusMessage = "已重置百度翻译当月已用字符计数";
    }

    [RelayCommand]
    public void ResetGoogleUsedCount()
    {
        GoogleUsedCount = 0;
        TriggerAutoSave(debounce: false);
        StatusMessage = "已重置谷歌翻译当月已用字符计数";
    }

    [RelayCommand]
    public void ResetOpenAiUsedCount()
    {
        OpenAiUsedCount = 0;
        TriggerAutoSave(debounce: false);
        StatusMessage = "已重置 OpenAI 当月已用 Token 计数";
    }

    [RelayCommand]
    public void CancelRecording()
    {
        IsRecordingHotkey = false;
        IsRecordingCaptureImageHotkey = false;
        IsRecordingAnnotateHotkey = false;
        IsRecordingTranslateHotkey = false;
        _hotkeyService.IsPaused = false;
        StatusMessage = "已取消快捷键录制";
    }

    [RelayCommand]
    public async Task BrowseExternalModelPathAsync()
    {
        if (RequestPickModelFolder != null)
        {
            var folder = await RequestPickModelFolder();
            if (!string.IsNullOrWhiteSpace(folder))
            {
                ExternalModelPath = folder;
                StatusMessage = $"已选择模型目录: {folder}";
            }
        }
    }

    [RelayCommand]
    public async Task TestExternalApiAsync()
    {
        if (string.IsNullOrWhiteSpace(ExternalApiUrl))
        {
            StatusMessage = "请输入有效的 Web API 接口地址！";
            return;
        }

        try
        {
            StatusMessage = "正在测试连接外部接口...";
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync(ExternalApiUrl);
            StatusMessage = $"接口响应成功 (HTTP {(int)response.StatusCode})";
        }
        catch (Exception ex)
        {
            StatusMessage = $"连接失败: {ex.Message}";
        }
    }

    public void FlushAutoSave()
    {
        if (_autoSaveCts != null)
        {
            _autoSaveCts.Cancel();
            _autoSaveCts.Dispose();
            _autoSaveCts = null;
            SaveInternal(isAutoSave: true);
        }
    }

    private void TriggerAutoSave(bool debounce = false)
    {
        if (_isInitializing || IsRecordingAnyHotkey) return;

        if (debounce)
        {
            _autoSaveCts?.Cancel();
            _autoSaveCts?.Dispose();
            _autoSaveCts = new CancellationTokenSource();
            var token = _autoSaveCts.Token;

            _ = Task.Delay(400, token).ContinueWith(t =>
            {
                if (!t.IsCanceled)
                {
                    try
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            if (!_isInitializing && !IsRecordingAnyHotkey)
                            {
                                SaveInternal(isAutoSave: true);
                            }
                        });
                    }
                    catch
                    {
                        if (!_isInitializing && !IsRecordingAnyHotkey)
                        {
                            SaveInternal(isAutoSave: true);
                        }
                    }
                }
            }, TaskScheduler.Default);
        }
        else
        {
            SaveInternal(isAutoSave: true);
        }
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (_isInitializing || IsRecordingAnyHotkey) return;

        switch (e.PropertyName)
        {
            // 即时自动保存：开关项、下拉项、快捷键
            case nameof(AutoCopy):
            case nameof(SilentMode):
            case nameof(SilentStartup):
            case nameof(ShowOcrNotification):
            case nameof(ShowCaptureImageNotification):
            case nameof(ShowWindowAfterOcr):
            case nameof(StartWithWindows):
            case nameof(ScanExternalModels):
            case nameof(EnableAngleDetection):
            case nameof(MaxSideLen):
            case nameof(EnableIdleSleep):
            case nameof(IdleSleepMinutes):
            case nameof(EnableLogging):
            case nameof(SelectedModel):
            case nameof(SelectedExternalModelTypeDisplay):
            case nameof(TranslateProvider):
            case nameof(SelectedTranslateSourceLang):
            case nameof(SelectedTranslateTargetLang):
            case nameof(Hotkey):
            case nameof(CaptureImageHotkey):
            case nameof(AnnotateHotkey):
            case nameof(TranslateHotkey):
                TriggerAutoSave(debounce: false);
                break;

            // 防抖自动保存：文本输入项
            case nameof(BaiduAppId):
            case nameof(BaiduSecretKey):
            case nameof(BaiduApiKey):
            case nameof(BaiduMonthlyLimit):
            case nameof(GoogleApiKey):
            case nameof(GoogleMonthlyLimit):
            case nameof(OpenAiApiEndpoint):
            case nameof(OpenAiApiKey):
            case nameof(OpenAiModel):
            case nameof(OpenAiMonthlyLimit):
            case nameof(ExternalModelPath):
            case nameof(ExternalApiUrl):
                TriggerAutoSave(debounce: true);
                break;
        }
    }

    private bool SaveInternal(bool isAutoSave)
    {
        if (_isInitializing) return false;

        _hotkeyService.IsPaused = false;

        // 1. 快捷键冲突互斥检查
        var activeHotkeys = new List<(string Name, string Value)>();
        if (!IsDisabledHotkey(Hotkey)) activeHotkeys.Add(("截图 OCR", Hotkey));
        if (!IsDisabledHotkey(CaptureImageHotkey)) activeHotkeys.Add(("截图复制", CaptureImageHotkey));
        if (!IsDisabledHotkey(AnnotateHotkey)) activeHotkeys.Add(("截图标注", AnnotateHotkey));
        if (!IsDisabledHotkey(TranslateHotkey)) activeHotkeys.Add(("选区翻译", TranslateHotkey));

        for (int i = 0; i < activeHotkeys.Count; i++)
        {
            for (int j = i + 1; j < activeHotkeys.Count; j++)
            {
                if (string.Equals(activeHotkeys[i].Value, activeHotkeys[j].Value, StringComparison.OrdinalIgnoreCase))
                {
                    StatusMessage = $"⚠️【{activeHotkeys[i].Name}】与【{activeHotkeys[j].Name}】快捷键冲突，请重新录制！";
                    return false;
                }
            }
        }

        // 2. 格式校验
        if (!IsDisabledHotkey(Hotkey) && !HotkeyGesture.TryParse(Hotkey, out _))
        {
            StatusMessage = "⚠️ 截图 OCR 快捷键格式无效！";
            return false;
        }
        if (!IsDisabledHotkey(CaptureImageHotkey) && !HotkeyGesture.TryParse(CaptureImageHotkey, out _))
        {
            StatusMessage = "⚠️ 截图复制快捷键格式无效！";
            return false;
        }
        if (!IsDisabledHotkey(AnnotateHotkey) && !HotkeyGesture.TryParse(AnnotateHotkey, out _))
        {
            StatusMessage = "⚠️ 截图标注快捷键格式无效！";
            return false;
        }
        if (!IsDisabledHotkey(TranslateHotkey) && !HotkeyGesture.TryParse(TranslateHotkey, out _))
        {
            StatusMessage = "⚠️ 选区翻译快捷键格式无效！";
            return false;
        }

        // 3. 更新并持久化设置
        var current = _settingsService.Current;
        var newSettings = new AppSettings
        {
            Hotkey = Hotkey,
            CaptureImageHotkey = CaptureImageHotkey,
            AnnotateHotkey = AnnotateHotkey,
            TranslateHotkey = TranslateHotkey,
            TranslateProvider = TranslateProvider,
            TranslateSourceLang = SelectedTranslateSourceLang?.Code ?? "auto",
            TranslateTargetLang = SelectedTranslateTargetLang?.Code ?? "zh",
            TranslationUsageMonth = current.TranslationUsageMonth,

            BaiduAppId = BaiduAppId?.Trim() ?? string.Empty,
            BaiduSecretKey = BaiduSecretKey?.Trim() ?? string.Empty,
            BaiduApiKey = BaiduApiKey?.Trim() ?? string.Empty,
            BaiduMonthlyLimit = BaiduMonthlyLimit,
            BaiduUsedCount = BaiduUsedCount,

            GoogleApiKey = GoogleApiKey?.Trim() ?? string.Empty,
            GoogleMonthlyLimit = GoogleMonthlyLimit,
            GoogleUsedCount = GoogleUsedCount,

            OpenAiApiEndpoint = OpenAiApiEndpoint?.Trim() ?? "https://api.openai.com/v1/chat/completions",
            OpenAiApiKey = OpenAiApiKey?.Trim() ?? string.Empty,
            OpenAiModel = OpenAiModel?.Trim() ?? "gpt-4o-mini",
            OpenAiMonthlyLimit = OpenAiMonthlyLimit,
            OpenAiUsedCount = OpenAiUsedCount,

            AutoCopy = AutoCopy,
            SilentMode = SilentMode,
            SilentStartup = SilentStartup,
            ShowOcrNotification = ShowOcrNotification,
            ShowCaptureImageNotification = ShowCaptureImageNotification,
            ShowWindowAfterOcr = ShowWindowAfterOcr,
            StartWithWindows = StartWithWindows,
            Model = SelectedModel,
            EmbeddedModel = SelectedModel,
            ScanExternalModels = ScanExternalModels,
            UseExternalModel = ScanExternalModels,
            ExternalModelType = SelectedExternalModelType,
            ExternalModelPath = ExternalModelPath,
            ExternalApiUrl = ExternalApiUrl,
            EnableAngleDetection = EnableAngleDetection,
            MaxSideLen = MaxSideLen,
            EnableIdleSleep = EnableIdleSleep,
            IdleSleepMinutes = IdleSleepMinutes,
            EnableLogging = EnableLogging,
            DefaultSaveFormat = current.DefaultSaveFormat,
            DefaultSaveQuality = current.DefaultSaveQuality,
            DefaultSaveScale = current.DefaultSaveScale,
            LastSaveDirectory = current.LastSaveDirectory
        };

        _settingsService.Save(newSettings);
        _startupService.SetEnabled(StartWithWindows);

        StatusMessage = isAutoSave ? "✓ 设置已自动保存" : "✓ 设置已保存";
        return true;
    }

    [RelayCommand]
    private void Save()
    {
        _autoSaveCts?.Cancel();
        _autoSaveCts?.Dispose();
        _autoSaveCts = null;
        SaveInternal(isAutoSave: false);
    }

    [RelayCommand]
    private void Cancel()
    {
        _hotkeyService.IsPaused = false;
        _autoSaveCts?.Cancel();
        _autoSaveCts?.Dispose();
        _autoSaveCts = null;
        ReloadSettings();
        StatusMessage = "已还原为已保存设置";
        RequestClose?.Invoke();
    }

    private void ReloadSettings()
    {
        _isInitializing = true;
        try
        {
            var current = _settingsService.Current;
            Hotkey = current.Hotkey;
            CaptureImageHotkey = current.CaptureImageHotkey;
            AnnotateHotkey = current.AnnotateHotkey;
            TranslateHotkey = current.TranslateHotkey;
            AutoCopy = current.AutoCopy;
            SilentMode = current.SilentMode;
            SilentStartup = current.SilentStartup;
            ShowOcrNotification = current.ShowOcrNotification;
            ShowCaptureImageNotification = current.ShowCaptureImageNotification;
            ShowWindowAfterOcr = current.ShowWindowAfterOcr;
            StartWithWindows = _startupService.IsEnabled();
            SelectedModel = current.Model;
            ScanExternalModels = current.ScanExternalModels;
            ExternalModelPath = current.ExternalModelPath;
            ExternalApiUrl = current.ExternalApiUrl;
            SelectedExternalModelType = current.ExternalModelType;
            EnableAngleDetection = current.EnableAngleDetection;
            MaxSideLen = current.MaxSideLen;
            EnableIdleSleep = current.EnableIdleSleep;
            IdleSleepMinutes = current.IdleSleepMinutes;
            EnableLogging = current.EnableLogging;

            TranslateProvider = current.TranslateProvider;
            SelectedTranslateSourceLang = TranslateSourceLanguages.FirstOrDefault(x => x.Code == current.TranslateSourceLang) ?? TranslateSourceLanguages[0];
            SelectedTranslateTargetLang = TranslateTargetLanguages.FirstOrDefault(x => x.Code == current.TranslateTargetLang) ?? TranslateTargetLanguages[0];

            BaiduAppId = current.BaiduAppId;
            BaiduSecretKey = current.BaiduSecretKey;
            BaiduApiKey = current.BaiduApiKey;
            BaiduMonthlyLimit = current.BaiduMonthlyLimit;
            BaiduUsedCount = current.BaiduUsedCount;

            GoogleApiKey = current.GoogleApiKey;
            GoogleMonthlyLimit = current.GoogleMonthlyLimit;
            GoogleUsedCount = current.GoogleUsedCount;

            OpenAiApiEndpoint = current.OpenAiApiEndpoint;
            OpenAiApiKey = current.OpenAiApiKey;
            OpenAiModel = current.OpenAiModel;
            OpenAiMonthlyLimit = current.OpenAiMonthlyLimit;
            OpenAiUsedCount = current.OpenAiUsedCount;
        }
        finally
        {
            _isInitializing = false;
        }
    }

    [RelayCommand]
    private void OpenAbout()
    {
        if (_aboutVmFactory != null)
        {
            var vm = _aboutVmFactory();
            RequestOpenAbout?.Invoke(vm);
        }
    }

    [RelayCommand]
    public void OpenSettingsFolder()
    {
        try
        {
            string? dir = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开配置目录失败: {ex.Message}";
        }
    }

    [RelayCommand]
    public void OpenSettingsFile()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                _settingsService.Save(_settingsService.Current);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = SettingsFilePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开配置文件失败: {ex.Message}";
        }
    }

    [RelayCommand]
    public void OpenLogFolder()
    {
        try
        {
            string dir = LogDirectory;
            if (!string.IsNullOrEmpty(dir))
            {
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开日志目录失败: {ex.Message}";
        }
    }
}
