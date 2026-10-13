using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShawMuOCR.Core.Hotkey;
using ShawMuOCR.Core.Logging;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.Core.Translation;
using ShawMuOCR.OCR;

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
    private bool _isSaving;
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

    [ObservableProperty]
    private string _ocrPopupHotkey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecordingAnyHotkey))]
    private bool _isRecordingOcrPopupHotkey;

    [ObservableProperty]
    private string _clipboardPanelHotkey;

    [ObservableProperty]
    private string _phrasesPanelHotkey;

    [ObservableProperty]
    private string _clipboardPanelBackground;

    [ObservableProperty]
    private bool _enableClipboard;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecordingAnyHotkey))]
    private bool _isRecordingClipboardPanelHotkey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecordingAnyHotkey))]
    private bool _isRecordingPhrasesPanelHotkey;

    public bool IsRecordingAnyHotkey => IsRecordingHotkey || IsRecordingCaptureImageHotkey || IsRecordingAnnotateHotkey || IsRecordingTranslateHotkey || IsRecordingOcrPopupHotkey || IsRecordingClipboardPanelHotkey || IsRecordingPhrasesPanelHotkey;

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

    public bool HasNoModels => AvailableModels.Count == 0;

    public bool HasAvailableModels => AvailableModels.Count > 0;

    public string ModelSelectionHint => HasNoModels
        ? "⚠️ 未在同级 models 文件夹下检测到任何可用模型！请放入模型后点击右侧 🔄 重新扫描"
        : $"已从同级 models 文件夹加载 {AvailableModels.Count} 个实际可用模型（选择后自动记录至 settings.json）";

    public bool IsEmbeddedSelected => true;
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
    [NotifyPropertyChangedFor(nameof(SelectedOcrBackendOption))]
    [NotifyPropertyChangedFor(nameof(OcrBackendHint))]
    [NotifyPropertyChangedFor(nameof(IsAutoBackendSelected))]
    [NotifyPropertyChangedFor(nameof(AutoResolvedBackendText))]
    [NotifyPropertyChangedFor(nameof(AutoResolvedReasonText))]
    [NotifyPropertyChangedFor(nameof(ShowGpuDeviceSelector))]
    private string _ocrBackend = "Auto";

    public IReadOnlyList<OcrBackendOption> AvailableOcrBackends { get; } =
    [
        new("Auto", "自动选择最优 (Auto - 智能优选 GPU/CPU)"),
        new("GPU", "GPU 加速 (Vulkan)"),
        new("CPU", "纯 CPU (AVX2 SIMD)")
    ];

    public OcrBackendOption SelectedOcrBackendOption
    {
        get
        {
            string normalized = NormalizeBackendValue(OcrBackend);
            return AvailableOcrBackends.FirstOrDefault(x => string.Equals(x.Value, normalized, StringComparison.OrdinalIgnoreCase))
                   ?? AvailableOcrBackends[0];
        }
        set
        {
            if (value != null && !string.Equals(OcrBackend, value.Value, StringComparison.OrdinalIgnoreCase))
            {
                OcrBackend = value.Value;
            }
        }
    }

    public bool IsAutoBackendSelected =>
        string.Equals(NormalizeBackendValue(OcrBackend), "Auto", StringComparison.OrdinalIgnoreCase);

    public string AutoResolvedBackendText =>
        IsAutoBackendSelected
            ? ShawMuOCR.OCR.PaddleOcrV6Engine.ProbeBackendInfo().AutoResolvedDisplayText
            : string.Empty;

    public string AutoResolvedReasonText =>
        IsAutoBackendSelected
            ? ShawMuOCR.OCR.PaddleOcrV6Engine.ProbeBackendInfo().AutoDecisionReason
            : string.Empty;

    public string OcrBackendHint => NormalizeBackendValue(OcrBackend) switch
    {
        "GPU" => "强制使用纯 C# Vulkan GPU 硬件加速推理（DET + CLS + REC）",
        "Auto" => "自动选择最优后端：支持硬件协作矩阵 (CoopMatrix) 或 AMD Wave64 (SubgroupMin≥64) 时自动启用 Vulkan，否则走 CPU AVX2 SIMD",
        _ => "默认使用纯托管 CPU SIMD (AVX2 / AVX-512) 向量指令加速，无首次编译开销且完全不占用 GPU"
    };

    // ==========================================
    // 物理显卡选择 (独显 / 核显)
    // ==========================================
    private IReadOnlyList<DiscoveredGpuDevice> _discoveredGpus = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedGpuDeviceOption))]
    [NotifyPropertyChangedFor(nameof(AutoResolvedBackendText))]
    [NotifyPropertyChangedFor(nameof(AutoResolvedReasonText))]
    [NotifyPropertyChangedFor(nameof(ShowGpuDeviceSelector))]
    private int _selectedGpuIndex = -1;

    partial void OnSelectedGpuIndexChanged(int value)
    {
        if (!_isInitializing)
        {
            try
            {
                ShawMuOCR.OCR.PaddleOcrV6Engine.SetSelectedGpuIndex(value);
                OnPropertyChanged(nameof(AutoResolvedBackendText));
                OnPropertyChanged(nameof(AutoResolvedReasonText));
                OnPropertyChanged(nameof(ShowGpuDeviceSelector));
            }
            catch (Exception ex)
            {
                StatusMessage = $"⚠️ 切换 GPU 设备失败: {ex.Message}";
            }
        }
    }

    public IReadOnlyList<GpuDeviceOption> AvailableGpuDevices { get; private set; } = [];

    public GpuDeviceOption SelectedGpuDeviceOption
    {
        get => AvailableGpuDevices.FirstOrDefault(x => x.Index == SelectedGpuIndex)
               ?? (AvailableGpuDevices.Count > 0 ? AvailableGpuDevices[0] : new GpuDeviceOption(-1, "自动选择 (默认 · 优先独立显卡)"));
        set
        {
            if (value != null && SelectedGpuIndex != value.Index)
            {
                SelectedGpuIndex = value.Index;
            }
        }
    }

    public bool HasMultipleGpus => _discoveredGpus.Count >= 2;

    public bool ShowGpuDeviceSelector
    {
        get
        {
            if (_discoveredGpus.Count == 0)
            {
                return false;
            }

            string normalized = NormalizeBackendValue(OcrBackend);
            if (string.Equals(normalized, "GPU", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(normalized, "Auto", StringComparison.OrdinalIgnoreCase))
            {
                return ShawMuOCR.OCR.PaddleOcrV6Engine.ProbeBackendInfo().AutoSelectsGpu;
            }

            return false;
        }
    }

    private static string NormalizeBackendValue(string? backend)
    {
        if (string.IsNullOrWhiteSpace(backend)) return "Auto";
        if (backend.Equals("Auto", StringComparison.OrdinalIgnoreCase)) return "Auto";
        if (backend.Equals("GPU", StringComparison.OrdinalIgnoreCase) ||
            backend.Equals("Vulkan", StringComparison.OrdinalIgnoreCase)) return "GPU";
        return "CPU";
    }

    [ObservableProperty]
    private bool _enableAngleDetection = true;

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
        _ocrBackend = NormalizeBackendValue(current.OcrBackend);
        _selectedGpuIndex = current.SelectedGpuIndex;
        InitializeGpuDevices();
        _enableAngleDetection = current.EnableAngleDetection;
        _maxSideLen = current.MaxSideLen;
        _enableIdleSleep = current.EnableIdleSleep;
        _idleSleepMinutes = current.IdleSleepMinutes;
        _enableLogging = current.EnableLogging;

        _translateHotkey = current.TranslateHotkey;
        _ocrPopupHotkey = current.OcrPopupHotkey;
        _enableClipboard = current.EnableClipboard;
        _clipboardPanelHotkey = current.ClipboardPanelHotkey;
        _phrasesPanelHotkey = current.PhrasesPanelHotkey;
        _clipboardPanelBackground = current.ClipboardPanelBackground;
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
        LoadMaxSideLens();
        LoadIdleSleepMinutes();
        _settingsService.SettingsChanged += OnExternalSettingsChanged;
        StatusMessage = HasNoModels
            ? "⚠️ 未在 models 目录下检测到任何 OCR 模型，请将 models 文件夹放入程序目录后点击刷新"
            : "⚡ 修改设置后将实时自动保存";
        _isInitializing = false;
    }

    private void OnExternalSettingsChanged(AppSettings settings)
    {
        if (_isSaving || _isInitializing || IsRecordingAnyHotkey) return;
        ReloadSettings();
    }

    private void InitializeGpuDevices()
    {
        _discoveredGpus = ShawMuOCR.OCR.PaddleOcrV6Engine.EnumerateAllGpuDevices();
        var gpuOptions = new List<GpuDeviceOption>
        {
            new(-1, "自动选择 (默认 · 优先独立显卡)")
        };
        foreach (var g in _discoveredGpus)
        {
            gpuOptions.Add(new(g.Index, g.DisplayName));
        }
        AvailableGpuDevices = gpuOptions;
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
        var models = _modelProvider.GetAvailableModels();

        bool listChanged = AvailableModels.Count != models.Count;
        if (!listChanged)
        {
            for (int i = 0; i < models.Count; i++)
            {
                if (!string.Equals(AvailableModels[i], models[i], StringComparison.OrdinalIgnoreCase))
                {
                    listChanged = true;
                    break;
                }
            }
        }

        bool wasInitializing = _isInitializing;
        _isInitializing = true;
        try
        {
            if (listChanged)
            {
                AvailableModels.Clear();
                AvailableEmbeddedModels.Clear();
                foreach (var m in models)
                {
                    AvailableModels.Add(m);
                    AvailableEmbeddedModels.Add(m);
                }
            }

            if (AvailableModels.Count > 0)
            {
                var matched = AvailableModels.FirstOrDefault(m => string.Equals(m, previousSelection, StringComparison.OrdinalIgnoreCase));
                if (matched != null)
                {
                    SelectedModel = matched;
                }
                else
                {
                    var fallback = AvailableModels.FirstOrDefault(m => m.Contains("small", StringComparison.OrdinalIgnoreCase))
                                   ?? AvailableModels[0];
                    SelectedModel = fallback;
                    // 若当前配置中的模型在磁盘上不存在，自动将实际选中的模型写入 settings.json
                    if (!string.Equals(_settingsService.Current.Model, fallback, StringComparison.OrdinalIgnoreCase))
                    {
                        var updated = _settingsService.Current.Clone();
                        updated.Model = fallback;
                        updated.EmbeddedModel = fallback;
                        _isSaving = true;
                        try
                        {
                            _settingsService.Save(updated);
                        }
                        finally
                        {
                            _isSaving = false;
                        }
                    }
                }
            }
            else
            {
                SelectedModel = string.Empty;
            }
        }
        finally
        {
            _isInitializing = wasInitializing;
        }

        OnPropertyChanged(nameof(HasNoModels));
        OnPropertyChanged(nameof(HasAvailableModels));
        OnPropertyChanged(nameof(ModelSelectionHint));
    }

    [RelayCommand]
    public void RefreshModels()
    {
        LoadModels();
        StatusMessage = HasNoModels
            ? "⚠️ 未在 models 文件夹下检测到任何可用模型，请确认模型已放入同级 models 目录"
            : $"✓ 模型列表已刷新，共加载 {AvailableModels.Count} 个可用模型 (当前生效: {SelectedModel})";
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
    private void StartRecordOcrPopupHotkey()
    {
        _hotkeyService.IsPaused = true;
        IsRecordingHotkey = false;
        IsRecordingCaptureImageHotkey = false;
        IsRecordingAnnotateHotkey = false;
        IsRecordingTranslateHotkey = false;
        IsRecordingOcrPopupHotkey = true;
        StatusMessage = "请按下【OCR 弹窗】快捷键 (按 Backspace 清空，按 Esc 取消)...";
    }

    public void OnOcrPopupHotkeyRecorded(HotkeyGesture gesture)
    {
        IsRecordingOcrPopupHotkey = false;
        _hotkeyService.IsPaused = false;
        OcrPopupHotkey = gesture.ToString();
        StatusMessage = $"✓ 已设置 OCR 弹窗快捷键: {OcrPopupHotkey}";
    }

    [RelayCommand]
    private void ClearOcrPopupHotkey()
    {
        IsRecordingOcrPopupHotkey = false;
        _hotkeyService.IsPaused = false;
        OcrPopupHotkey = "无";
        StatusMessage = "OCR 弹窗快捷键已清空(已禁用)";
    }

    [RelayCommand]
    private void StartRecordClipboardPanelHotkey()
    {
        _hotkeyService.IsPaused = true;
        IsRecordingHotkey = false;
        IsRecordingCaptureImageHotkey = false;
        IsRecordingAnnotateHotkey = false;
        IsRecordingTranslateHotkey = false;
        IsRecordingOcrPopupHotkey = false;
        IsRecordingClipboardPanelHotkey = true;
        IsRecordingPhrasesPanelHotkey = false;
        StatusMessage = "请按下【剪贴板快捷面板】快捷键 (默认 Ctrl+~，按 Backspace 清空，按 Esc 取消)...";
    }

    public void OnClipboardPanelHotkeyRecorded(HotkeyGesture gesture)
    {
        IsRecordingClipboardPanelHotkey = false;
        _hotkeyService.IsPaused = false;
        ClipboardPanelHotkey = gesture.ToString();
        StatusMessage = $"✓ 已设置剪贴板快捷键: {ClipboardPanelHotkey}";
    }

    [RelayCommand]
    private void ClearClipboardPanelHotkey()
    {
        IsRecordingClipboardPanelHotkey = false;
        _hotkeyService.IsPaused = false;
        ClipboardPanelHotkey = "无";
        StatusMessage = "剪贴板快捷键已清空(已禁用)";
    }

    [RelayCommand]
    private void StartRecordPhrasesPanelHotkey()
    {
        _hotkeyService.IsPaused = true;
        IsRecordingHotkey = false;
        IsRecordingCaptureImageHotkey = false;
        IsRecordingAnnotateHotkey = false;
        IsRecordingTranslateHotkey = false;
        IsRecordingOcrPopupHotkey = false;
        IsRecordingClipboardPanelHotkey = false;
        IsRecordingPhrasesPanelHotkey = true;
        StatusMessage = "请按下【常用语快捷呼出】快捷键 (默认 Ctrl+1，按 Backspace 清空，按 Esc 取消)...";
    }

    public void OnPhrasesPanelHotkeyRecorded(HotkeyGesture gesture)
    {
        IsRecordingPhrasesPanelHotkey = false;
        _hotkeyService.IsPaused = false;
        PhrasesPanelHotkey = gesture.ToString();
        StatusMessage = $"✓ 已设置常用语快捷键: {PhrasesPanelHotkey}";
    }

    [RelayCommand]
    private void ClearPhrasesPanelHotkey()
    {
        IsRecordingPhrasesPanelHotkey = false;
        _hotkeyService.IsPaused = false;
        PhrasesPanelHotkey = "无";
        StatusMessage = "常用语快捷键已清空(已禁用)";
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
        IsRecordingOcrPopupHotkey = false;
        IsRecordingClipboardPanelHotkey = false;
        IsRecordingPhrasesPanelHotkey = false;
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
            case nameof(OcrBackend):
            case nameof(SelectedGpuIndex):
            case nameof(EnableAngleDetection):
            case nameof(MaxSideLen):
            case nameof(EnableIdleSleep):
            case nameof(IdleSleepMinutes):
            case nameof(EnableLogging):
            case nameof(SelectedExternalModelTypeDisplay):
            case nameof(TranslateProvider):
            case nameof(SelectedTranslateSourceLang):
            case nameof(SelectedTranslateTargetLang):
            case nameof(Hotkey):
            case nameof(CaptureImageHotkey):
            case nameof(AnnotateHotkey):
            case nameof(TranslateHotkey):
            case nameof(OcrPopupHotkey):
            case nameof(EnableClipboard):
            case nameof(ClipboardPanelHotkey):
            case nameof(PhrasesPanelHotkey):
            case nameof(ClipboardPanelBackground):
                TriggerAutoSave(debounce: false);
                break;

            case nameof(SelectedModel):
                if (string.IsNullOrWhiteSpace(SelectedModel))
                {
                    if (AvailableModels.Count > 0)
                    {
                        var currentSaved = _settingsService.Current.Model;
                        SelectedModel = AvailableModels.FirstOrDefault(m => string.Equals(m, currentSaved, StringComparison.OrdinalIgnoreCase))
                                        ?? AvailableModels[0];
                    }
                    return;
                }
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
        if (!IsDisabledHotkey(OcrPopupHotkey)) activeHotkeys.Add(("OCR 弹窗", OcrPopupHotkey));
        if (EnableClipboard)
        {
            if (!IsDisabledHotkey(ClipboardPanelHotkey)) activeHotkeys.Add(("剪贴板面板", ClipboardPanelHotkey));
            if (!IsDisabledHotkey(PhrasesPanelHotkey)) activeHotkeys.Add(("常用语面板", PhrasesPanelHotkey));
        }

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
        if (!IsDisabledHotkey(OcrPopupHotkey) && !HotkeyGesture.TryParse(OcrPopupHotkey, out _))
        {
            StatusMessage = "⚠️ OCR 弹窗快捷键格式无效！";
            return false;
        }
        if (EnableClipboard)
        {
            if (!IsDisabledHotkey(ClipboardPanelHotkey) && !HotkeyGesture.TryParse(ClipboardPanelHotkey, out _))
            {
                StatusMessage = "⚠️ 剪贴板快捷键格式无效！";
                return false;
            }
            if (!IsDisabledHotkey(PhrasesPanelHotkey) && !HotkeyGesture.TryParse(PhrasesPanelHotkey, out _))
            {
                StatusMessage = "⚠️ 常用语快捷键格式无效！";
                return false;
            }
        }

        // 3. 更新并持久化设置
        var current = _settingsService.Current;
        var newSettings = current.Clone();
        newSettings.Hotkey = Hotkey;
        newSettings.CaptureImageHotkey = CaptureImageHotkey;
        newSettings.AnnotateHotkey = AnnotateHotkey;
        newSettings.TranslateHotkey = TranslateHotkey;
        newSettings.OcrPopupHotkey = OcrPopupHotkey;
        newSettings.EnableClipboard = EnableClipboard;
        newSettings.ClipboardPanelHotkey = ClipboardPanelHotkey;
        newSettings.PhrasesPanelHotkey = PhrasesPanelHotkey;
        newSettings.ClipboardPanelBackground = ClipboardPanelBackground;
        newSettings.TranslateProvider = TranslateProvider;
        newSettings.TranslateSourceLang = SelectedTranslateSourceLang?.Code ?? "auto";
        newSettings.TranslateTargetLang = SelectedTranslateTargetLang?.Code ?? "zh";
        newSettings.TranslationUsageMonth = current.TranslationUsageMonth;

        newSettings.BaiduAppId = BaiduAppId?.Trim() ?? string.Empty;
        newSettings.BaiduSecretKey = BaiduSecretKey?.Trim() ?? string.Empty;
        newSettings.BaiduApiKey = BaiduApiKey?.Trim() ?? string.Empty;
        newSettings.BaiduMonthlyLimit = BaiduMonthlyLimit;
        newSettings.BaiduUsedCount = BaiduUsedCount;

        newSettings.GoogleApiKey = GoogleApiKey?.Trim() ?? string.Empty;
        newSettings.GoogleMonthlyLimit = GoogleMonthlyLimit;
        newSettings.GoogleUsedCount = GoogleUsedCount;

        newSettings.OpenAiApiEndpoint = OpenAiApiEndpoint?.Trim() ?? "https://api.openai.com/v1/chat/completions";
        newSettings.OpenAiApiKey = OpenAiApiKey?.Trim() ?? string.Empty;
        newSettings.OpenAiModel = OpenAiModel?.Trim() ?? "gpt-4o-mini";
        newSettings.OpenAiMonthlyLimit = OpenAiMonthlyLimit;
        newSettings.OpenAiUsedCount = OpenAiUsedCount;

        newSettings.AutoCopy = AutoCopy;
        newSettings.SilentMode = SilentMode;
        newSettings.SilentStartup = SilentStartup;
        newSettings.ShowOcrNotification = ShowOcrNotification;
        newSettings.ShowCaptureImageNotification = ShowCaptureImageNotification;
        newSettings.ShowWindowAfterOcr = ShowWindowAfterOcr;
        newSettings.StartWithWindows = StartWithWindows;
        if (!string.IsNullOrWhiteSpace(SelectedModel))
        {
            newSettings.Model = SelectedModel;
            newSettings.EmbeddedModel = SelectedModel;
        }
        newSettings.ScanExternalModels = true;
        newSettings.UseExternalModel = true;
        newSettings.ExternalModelType = SelectedExternalModelType;
        newSettings.ExternalModelPath = ExternalModelPath;
        newSettings.ExternalApiUrl = ExternalApiUrl;
        newSettings.OcrBackend = NormalizeBackendValue(OcrBackend);
        newSettings.SelectedGpuIndex = SelectedGpuIndex;
        newSettings.EnableAngleDetection = EnableAngleDetection;
        newSettings.MaxSideLen = MaxSideLen;
        newSettings.EnableIdleSleep = EnableIdleSleep;
        newSettings.IdleSleepMinutes = IdleSleepMinutes;
        newSettings.EnableLogging = EnableLogging;

        _isSaving = true;
        try
        {
            _settingsService.Save(newSettings);
        }
        finally
        {
            _isSaving = false;
        }
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
            OcrPopupHotkey = current.OcrPopupHotkey;
            EnableClipboard = current.EnableClipboard;
            ClipboardPanelHotkey = current.ClipboardPanelHotkey;
            PhrasesPanelHotkey = current.PhrasesPanelHotkey;
            ClipboardPanelBackground = current.ClipboardPanelBackground;
            AutoCopy = current.AutoCopy;
            SilentMode = current.SilentMode;
            SilentStartup = current.SilentStartup;
            ShowOcrNotification = current.ShowOcrNotification;
            ShowCaptureImageNotification = current.ShowCaptureImageNotification;
            ShowWindowAfterOcr = current.ShowWindowAfterOcr;
            StartWithWindows = _startupService.IsEnabled();
            SelectedModel = current.Model;
            ScanExternalModels = current.ScanExternalModels;
            LoadModels();
            ExternalModelPath = current.ExternalModelPath;
            ExternalApiUrl = current.ExternalApiUrl;
            SelectedExternalModelType = current.ExternalModelType;
            OcrBackend = NormalizeBackendValue(current.OcrBackend);
            SelectedGpuIndex = current.SelectedGpuIndex;
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

public sealed record OcrBackendOption(string Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed record GpuDeviceOption(int Index, string DisplayName)
{
    public override string ToString() => DisplayName;
}
