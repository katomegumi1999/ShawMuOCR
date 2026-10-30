using System.Text.Json.Serialization;

namespace ShawMuOCR.Core.Settings;

/// <summary>
/// 应用程序主配置。
/// 内部按业务领域划分为 8 个 Section 分块子对象，支持独立序列化与分治维护；
/// 根级别保留与历史属性 100% 同名的 [JsonIgnore] 桥接访问器，保证既有代码及 ViewModel 无感调用。
/// </summary>
public sealed class AppSettings
{
    private GeneralSettings _general = new();
    public GeneralSettings General
    {
        get => _general;
        set => _general = value ?? new();
    }

    private HotkeySettings _hotkeys = new();
    public HotkeySettings Hotkeys
    {
        get => _hotkeys;
        set => _hotkeys = value ?? new();
    }

    private OcrSettings _ocr = new();
    public OcrSettings Ocr
    {
        get => _ocr;
        set => _ocr = value ?? new();
    }

    private RecordingSettings _recording = new();
    public RecordingSettings Recording
    {
        get => _recording;
        set => _recording = value ?? new();
    }

    private TranslationSettings _translation = new();
    public TranslationSettings Translation
    {
        get => _translation;
        set => _translation = value ?? new();
    }

    private ThemeSettings _theme = new();
    public ThemeSettings Theme
    {
        get => _theme;
        set => _theme = value ?? new();
    }

    private BatchSettings _batch = new();
    public BatchSettings Batch
    {
        get => _batch;
        set => _batch = value ?? new();
    }

    private HttpApiSettings _httpApi = new();
    public HttpApiSettings HttpApi
    {
        get => _httpApi;
        set => _httpApi = value ?? new();
    }

    // ==========================================
    // 快捷键向后兼容桥接属性 (指向 Hotkeys)
    // ==========================================

    [JsonIgnore]
    public string Hotkey
    {
        get => Hotkeys.Ocr;
        set => Hotkeys.Ocr = value;
    }

    [JsonIgnore]
    public string CaptureImageHotkey
    {
        get => Hotkeys.CaptureImage;
        set => Hotkeys.CaptureImage = value;
    }

    [JsonIgnore]
    public string AnnotateHotkey
    {
        get => Hotkeys.Annotate;
        set => Hotkeys.Annotate = value;
    }

    [JsonIgnore]
    public string TranslateHotkey
    {
        get => Hotkeys.Translate;
        set => Hotkeys.Translate = value;
    }

    [JsonIgnore]
    public string OcrPopupHotkey
    {
        get => Hotkeys.OcrPopup;
        set => Hotkeys.OcrPopup = value;
    }

    [JsonIgnore]
    public string QuickNoteHotkey
    {
        get => Hotkeys.QuickNote;
        set => Hotkeys.QuickNote = value;
    }

    [JsonIgnore]
    public string ClipboardPanelHotkey
    {
        get => Hotkeys.ClipboardPanel;
        set => Hotkeys.ClipboardPanel = value;
    }

    [JsonIgnore]
    public string PhrasesPanelHotkey
    {
        get => Hotkeys.PhrasesPanel;
        set => Hotkeys.PhrasesPanel = value;
    }

    [JsonIgnore]
    public string LongScreenshotHotkey
    {
        get => Hotkeys.LongScreenshot;
        set => Hotkeys.LongScreenshot = value;
    }

    [JsonIgnore]
    public string RecordingHotkey
    {
        get => Hotkeys.Recording;
        set => Hotkeys.Recording = value;
    }

    // ==========================================
    // 通用偏好向后兼容桥接属性 (指向 General)
    // ==========================================

    [JsonIgnore]
    public bool AutoCopy
    {
        get => General.AutoCopy;
        set => General.AutoCopy = value;
    }

    [JsonIgnore]
    public bool AutoCopyTableAsCells
    {
        get => General.AutoCopyTableAsCells;
        set => General.AutoCopyTableAsCells = value;
    }

    [JsonIgnore]
    public bool SilentMode
    {
        get => General.SilentMode;
        set => General.SilentMode = value;
    }

    [JsonIgnore]
    public bool SilentStartup
    {
        get => General.SilentStartup;
        set => General.SilentStartup = value;
    }

    [JsonIgnore]
    public bool ShowOcrNotification
    {
        get => General.ShowOcrNotification;
        set => General.ShowOcrNotification = value;
    }

    [JsonIgnore]
    public bool ShowCaptureImageNotification
    {
        get => General.ShowCaptureImageNotification;
        set => General.ShowCaptureImageNotification = value;
    }

    [JsonIgnore]
    public bool ShowNotificationBadge
    {
        get => General.ShowNotificationBadge;
        set => General.ShowNotificationBadge = value;
    }

    [JsonIgnore]
    public bool ShowWindowAfterOcr
    {
        get => General.ShowWindowAfterOcr;
        set => General.ShowWindowAfterOcr = value;
    }

    [JsonIgnore]
    public bool StartWithWindows
    {
        get => General.StartWithWindows;
        set => General.StartWithWindows = value;
    }

    [JsonIgnore]
    public bool EnableLogging
    {
        get => General.EnableLogging;
        set => General.EnableLogging = value;
    }

    [JsonIgnore]
    public bool EnableLogAutoClean
    {
        get => General.EnableLogAutoClean;
        set => General.EnableLogAutoClean = value;
    }

    [JsonIgnore]
    public int LogRetentionDays
    {
        get => General.LogRetentionDays;
        set => General.LogRetentionDays = value;
    }

    [JsonIgnore]
    public string DefaultSaveFormat
    {
        get => General.DefaultSaveFormat;
        set => General.DefaultSaveFormat = value;
    }

    [JsonIgnore]
    public int DefaultSaveQuality
    {
        get => General.DefaultSaveQuality;
        set => General.DefaultSaveQuality = value;
    }

    [JsonIgnore]
    public int DefaultSaveScale
    {
        get => General.DefaultSaveScale;
        set => General.DefaultSaveScale = value;
    }

    [JsonIgnore]
    public string? LastSaveDirectory
    {
        get => General.LastSaveDirectory;
        set => General.LastSaveDirectory = value;
    }

    [JsonIgnore]
    public float AnnotationStrokeWidth
    {
        get => General.AnnotationStrokeWidth;
        set => General.AnnotationStrokeWidth = value;
    }

    [JsonIgnore]
    public float AnnotationFontSize
    {
        get => General.AnnotationFontSize;
        set => General.AnnotationFontSize = value;
    }

    [JsonIgnore]
    public bool EnableClipboard
    {
        get => General.EnableClipboard;
        set => General.EnableClipboard = value;
    }

    [JsonIgnore]
    public string ClipboardPanelBackground
    {
        get => General.ClipboardPanelBackground;
        set => General.ClipboardPanelBackground = value;
    }

    // ==========================================
    // OCR 识别与模型向后兼容桥接属性 (指向 Ocr)
    // ==========================================

    [JsonIgnore]
    public bool ScanExternalModels
    {
        get => Ocr.ScanExternalModels;
        set => Ocr.ScanExternalModels = value;
    }

    [JsonIgnore]
    public bool UseExternalModel
    {
        get => Ocr.UseExternalModel;
        set => Ocr.UseExternalModel = value;
    }

    [JsonIgnore]
    public string ExternalModelType
    {
        get => Ocr.ExternalModelType;
        set => Ocr.ExternalModelType = value;
    }

    [JsonIgnore]
    public string ExternalModelPath
    {
        get => Ocr.ExternalModelPath;
        set => Ocr.ExternalModelPath = value;
    }

    [JsonIgnore]
    public string ExternalApiUrl
    {
        get => Ocr.ExternalApiUrl;
        set => Ocr.ExternalApiUrl = value;
    }

    [JsonIgnore]
    public string Model
    {
        get => Ocr.Model;
        set => Ocr.Model = value;
    }

    [JsonIgnore]
    public string EmbeddedModel
    {
        get => Ocr.EmbeddedModel;
        set => Ocr.EmbeddedModel = value;
    }

    [JsonIgnore]
    public string OcrBackend
    {
        get => Ocr.OcrBackend;
        set => Ocr.OcrBackend = value;
    }

    [JsonIgnore]
    public int SelectedGpuIndex
    {
        get => Ocr.SelectedGpuIndex;
        set => Ocr.SelectedGpuIndex = value;
    }

    [JsonIgnore]
    public bool EnableAngleDetection
    {
        get => Ocr.EnableAngleDetection;
        set => Ocr.EnableAngleDetection = value;
    }

    [JsonIgnore]
    public int MaxSideLen
    {
        get => Ocr.MaxSideLen;
        set => Ocr.MaxSideLen = value;
    }

    [JsonIgnore]
    public bool EnableIdleSleep
    {
        get => Ocr.EnableIdleSleep;
        set => Ocr.EnableIdleSleep = value;
    }

    [JsonIgnore]
    public int IdleSleepMinutes
    {
        get => Ocr.IdleSleepMinutes;
        set => Ocr.IdleSleepMinutes = value;
    }

    // ==========================================
    // 屏幕录制向后兼容桥接属性 (指向 Recording)
    // ==========================================

    [JsonIgnore]
    public int RecordingEncoder
    {
        get => Recording.Encoder;
        set => Recording.Encoder = value;
    }

    [JsonIgnore]
    public uint RecordingFps
    {
        get => Recording.Fps;
        set => Recording.Fps = value;
    }

    [JsonIgnore]
    public uint RecordingBitrateKbps
    {
        get => Recording.BitrateKbps;
        set => Recording.BitrateKbps = value;
    }

    [JsonIgnore]
    public bool RecordingShowRegionBorder
    {
        get => Recording.ShowRegionBorder;
        set => Recording.ShowRegionBorder = value;
    }

    [JsonIgnore]
    public string RecordingOutputDir
    {
        get => Recording.OutputDir;
        set => Recording.OutputDir = value;
    }

    [JsonIgnore]
    public string RecordingFilePrefix
    {
        get => Recording.FilePrefix;
        set => Recording.FilePrefix = value;
    }

    [JsonIgnore]
    public uint RecordingAudioBitrateKbps
    {
        get => Recording.AudioBitrateKbps;
        set => Recording.AudioBitrateKbps = value;
    }

    [JsonIgnore]
    public bool RecordingCaptureCursor
    {
        get => Recording.CaptureCursor;
        set => Recording.CaptureCursor = value;
    }

    [JsonIgnore]
    public bool RecordingCursorClickEffect
    {
        get => Recording.CursorClickEffect;
        set => Recording.CursorClickEffect = value;
    }

    [JsonIgnore]
    public bool RecordingCursorTrail
    {
        get => Recording.CursorTrail;
        set => Recording.CursorTrail = value;
    }

    [JsonIgnore]
    public uint RecordingCursorTrailMs
    {
        get => Recording.CursorTrailMs;
        set => Recording.CursorTrailMs = value;
    }

    [JsonIgnore]
    public bool RecordingExcludeFloatingBarFromCapture
    {
        get => Recording.ExcludeFloatingBarFromCapture;
        set => Recording.ExcludeFloatingBarFromCapture = value;
    }

    [JsonIgnore]
    public bool RecordingShowFloatingBar
    {
        get => Recording.ShowFloatingBar;
        set => Recording.ShowFloatingBar = value;
    }

    [JsonIgnore]
    public bool RecordingLiveCursorEffect
    {
        get => Recording.LiveCursorEffect;
        set => Recording.LiveCursorEffect = value;
    }

    // ==========================================
    // 翻译服务向后兼容桥接属性 (指向 Translation)
    // ==========================================

    [JsonIgnore]
    public string TranslateProvider
    {
        get => Translation.Provider;
        set => Translation.Provider = value;
    }

    [JsonIgnore]
    public string TranslateSourceLang
    {
        get => Translation.SourceLang;
        set => Translation.SourceLang = value;
    }

    [JsonIgnore]
    public string TranslateTargetLang
    {
        get => Translation.TargetLang;
        set => Translation.TargetLang = value;
    }

    [JsonIgnore]
    public string TranslationUsageMonth
    {
        get => Translation.UsageMonth;
        set => Translation.UsageMonth = value;
    }

    [JsonIgnore]
    public string BaiduAppId
    {
        get => Translation.BaiduAppId;
        set => Translation.BaiduAppId = value;
    }

    [JsonIgnore]
    public string BaiduSecretKey
    {
        get => Translation.BaiduSecretKey;
        set => Translation.BaiduSecretKey = value;
    }

    [JsonIgnore]
    public string BaiduApiKey
    {
        get => Translation.BaiduApiKey;
        set => Translation.BaiduApiKey = value;
    }

    [JsonIgnore]
    public int BaiduMonthlyLimit
    {
        get => Translation.BaiduMonthlyLimit;
        set => Translation.BaiduMonthlyLimit = value;
    }

    [JsonIgnore]
    public int BaiduUsedCount
    {
        get => Translation.BaiduUsedCount;
        set => Translation.BaiduUsedCount = value;
    }

    [JsonIgnore]
    public string GoogleApiKey
    {
        get => Translation.GoogleApiKey;
        set => Translation.GoogleApiKey = value;
    }

    [JsonIgnore]
    public int GoogleMonthlyLimit
    {
        get => Translation.GoogleMonthlyLimit;
        set => Translation.GoogleMonthlyLimit = value;
    }

    [JsonIgnore]
    public int GoogleUsedCount
    {
        get => Translation.GoogleUsedCount;
        set => Translation.GoogleUsedCount = value;
    }

    [JsonIgnore]
    public string OpenAiApiEndpoint
    {
        get => Translation.OpenAiApiEndpoint;
        set => Translation.OpenAiApiEndpoint = value;
    }

    [JsonIgnore]
    public string OpenAiApiKey
    {
        get => Translation.OpenAiApiKey;
        set => Translation.OpenAiApiKey = value;
    }

    [JsonIgnore]
    public string OpenAiModel
    {
        get => Translation.OpenAiModel;
        set => Translation.OpenAiModel = value;
    }

    [JsonIgnore]
    public int OpenAiMonthlyLimit
    {
        get => Translation.OpenAiMonthlyLimit;
        set => Translation.OpenAiMonthlyLimit = value;
    }

    [JsonIgnore]
    public int OpenAiUsedCount
    {
        get => Translation.OpenAiUsedCount;
        set => Translation.OpenAiUsedCount = value;
    }

    [JsonIgnore]
    public string VolcengineAccessKey
    {
        get => Translation.VolcengineAccessKey;
        set => Translation.VolcengineAccessKey = value;
    }

    [JsonIgnore]
    public string VolcengineSecretKey
    {
        get => Translation.VolcengineSecretKey;
        set => Translation.VolcengineSecretKey = value;
    }

    [JsonIgnore]
    public string VolcengineRegion
    {
        get => Translation.VolcengineRegion;
        set => Translation.VolcengineRegion = value;
    }

    [JsonIgnore]
    public int VolcengineMonthlyLimit
    {
        get => Translation.VolcengineMonthlyLimit;
        set => Translation.VolcengineMonthlyLimit = value;
    }

    [JsonIgnore]
    public int VolcengineUsedCount
    {
        get => Translation.VolcengineUsedCount;
        set => Translation.VolcengineUsedCount = value;
    }

    [JsonIgnore]
    public string AliyunAccessKeyId
    {
        get => Translation.AliyunAccessKeyId;
        set => Translation.AliyunAccessKeyId = value;
    }

    [JsonIgnore]
    public string AliyunAccessKeySecret
    {
        get => Translation.AliyunAccessKeySecret;
        set => Translation.AliyunAccessKeySecret = value;
    }

    [JsonIgnore]
    public string AliyunApiEndpoint
    {
        get => Translation.AliyunApiEndpoint;
        set => Translation.AliyunApiEndpoint = value;
    }

    [JsonIgnore]
    public int AliyunMonthlyLimit
    {
        get => Translation.AliyunMonthlyLimit;
        set => Translation.AliyunMonthlyLimit = value;
    }

    [JsonIgnore]
    public int AliyunUsedCount
    {
        get => Translation.AliyunUsedCount;
        set => Translation.AliyunUsedCount = value;
    }

    // ==========================================
    // 批量 OCR 转换向后兼容桥接属性 (指向 Batch)
    // ==========================================

    [JsonIgnore]
    public bool BatchExportTxt
    {
        get => Batch.ExportTxt;
        set => Batch.ExportTxt = value;
    }

    [JsonIgnore]
    public bool BatchExportXlsx
    {
        get => Batch.ExportXlsx;
        set => Batch.ExportXlsx = value;
    }

    [JsonIgnore]
    public bool BatchExportDocx
    {
        get => Batch.ExportDocx;
        set => Batch.ExportDocx = value;
    }

    [JsonIgnore]
    public bool BatchSplitColumnsByBlocks
    {
        get => Batch.SplitColumnsByBlocks;
        set => Batch.SplitColumnsByBlocks = value;
    }

    [JsonIgnore]
    public int BatchImageRotationIndex
    {
        get => Batch.ImageRotationIndex;
        set => Batch.ImageRotationIndex = value;
    }

    [JsonIgnore]
    public bool BatchIsSuffixEnabled
    {
        get => Batch.IsSuffixEnabled;
        set => Batch.IsSuffixEnabled = value;
    }

    [JsonIgnore]
    public string BatchSuffixText
    {
        get => Batch.SuffixText;
        set => Batch.SuffixText = value;
    }

    [JsonIgnore]
    public bool BatchUseOriginalFolder
    {
        get => Batch.UseOriginalFolder;
        set => Batch.UseOriginalFolder = value;
    }

    [JsonIgnore]
    public string BatchCustomOutputDir
    {
        get => Batch.CustomOutputDir;
        set => Batch.CustomOutputDir = value;
    }

    [JsonIgnore]
    public int BatchMaxCores
    {
        get => Batch.MaxCores;
        set => Batch.MaxCores = value;
    }

    [JsonIgnore]
    public int BatchGpuStreams
    {
        get => Batch.GpuStreams;
        set => Batch.GpuStreams = value;
    }

    [JsonIgnore]
    public int BatchCpuLimitPercent
    {
        get => Batch.CpuLimitPercent;
        set => Batch.CpuLimitPercent = value;
    }

    [JsonIgnore]
    public string BatchSchedulerMode
    {
        get => Batch.SchedulerMode;
        set => Batch.SchedulerMode = value;
    }

    // ==========================================
    // 内置 HTTP API 向后兼容桥接属性 (指向 HttpApi)
    // ==========================================

    [JsonIgnore]
    public int HttpApiPort
    {
        get => HttpApi.Port;
        set => HttpApi.Port = value;
    }

    [JsonIgnore]
    public bool HttpApiAllowRemote
    {
        get => HttpApi.AllowRemote;
        set => HttpApi.AllowRemote = value;
    }

    [JsonIgnore]
    public string HttpApiAccessToken
    {
        get => HttpApi.AccessToken;
        set => HttpApi.AccessToken = value;
    }

    [JsonIgnore]
    public bool HttpApiAllowAnonymousRemote
    {
        get => HttpApi.AllowAnonymousRemote;
        set => HttpApi.AllowAnonymousRemote = value;
    }

    [JsonIgnore]
    public int HttpApiMaxCores
    {
        get => HttpApi.MaxCores;
        set => HttpApi.MaxCores = value;
    }

    [JsonIgnore]
    public int HttpApiGpuStreams
    {
        get => HttpApi.GpuStreams;
        set => HttpApi.GpuStreams = value;
    }

    [JsonIgnore]
    public int HttpApiCpuLimitPercent
    {
        get => HttpApi.CpuLimitPercent;
        set => HttpApi.CpuLimitPercent = value;
    }

    [JsonIgnore]
    public string HttpApiSchedulerMode
    {
        get => HttpApi.SchedulerMode;
        set => HttpApi.SchedulerMode = value;
    }

    // ==========================================
    // 主题与外观记忆向后兼容桥接属性 (指向 Theme)
    // ==========================================

    [JsonIgnore]
    public bool IsSidebarExpanded
    {
        get => Theme.IsSidebarExpanded;
        set => Theme.IsSidebarExpanded = value;
    }

    [JsonIgnore]
    public string? ThemeBackgroundImagePath
    {
        get => Theme.BackgroundImagePath;
        set => Theme.BackgroundImagePath = value;
    }

    [JsonIgnore]
    public double ThemeBackgroundBlurRadius
    {
        get => Theme.BackgroundBlurRadius;
        set => Theme.BackgroundBlurRadius = value;
    }

    [JsonIgnore]
    public double ThemeComponentOpacity
    {
        get => Theme.ComponentOpacity;
        set => Theme.ComponentOpacity = value;
    }

    [JsonIgnore]
    public double ThemeButtonOpacity
    {
        get => Theme.ButtonOpacity;
        set => Theme.ButtonOpacity = value;
    }

    [JsonIgnore]
    public bool ThemeEnableAcrylic
    {
        get => Theme.EnableAcrylic;
        set => Theme.EnableAcrylic = value;
    }

    [JsonIgnore]
    public string? ThemeBackgroundColorHex
    {
        get => Theme.BackgroundColorHex;
        set => Theme.BackgroundColorHex = value;
    }

    [JsonIgnore]
    public string? ThemeCardColorHex
    {
        get => Theme.CardColorHex;
        set => Theme.CardColorHex = value;
    }

    [JsonIgnore]
    public string? ThemeTextColorHex
    {
        get => Theme.TextColorHex;
        set => Theme.TextColorHex = value;
    }

    [JsonIgnore]
    public string? ThemeFloatingWindowBackgroundColorHex
    {
        get => Theme.FloatingWindowBackgroundColorHex;
        set => Theme.FloatingWindowBackgroundColorHex = value;
    }

    [JsonIgnore]
    public string? ThemeFloatingWindowTextColorHex
    {
        get => Theme.FloatingWindowTextColorHex;
        set => Theme.FloatingWindowTextColorHex = value;
    }

    [JsonIgnore]
    public string LastActivePage
    {
        get => Theme.LastActivePage;
        set => Theme.LastActivePage = value;
    }

    public AppSettings Clone() => new()
    {
        General = General.Clone(),
        Hotkeys = Hotkeys.Clone(),
        Ocr = Ocr.Clone(),
        Recording = Recording.Clone(),
        Translation = Translation.Clone(),
        Theme = Theme.Clone(),
        Batch = Batch.Clone(),
        HttpApi = HttpApi.Clone()
    };
}
