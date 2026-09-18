namespace ShawMuOCR.Core.Settings;

public sealed class AppSettings
{
    public string Hotkey { get; set; } = "Alt+Z";
    public string CaptureImageHotkey { get; set; } = "Alt+X";
    public string AnnotateHotkey { get; set; } = "Alt+C";

    public bool AutoCopy { get; set; } = true;

    /// <summary>
    /// 静默模式：开启后识别完成不再自动弹出主窗口
    /// </summary>
    public bool SilentMode { get; set; } = false;

    /// <summary>
    /// 静默启动：开启后启动直接最小化到系统托盘，不弹出主界面UI
    /// </summary>
    public bool SilentStartup { get; set; } = false;

    /// <summary>
    /// OCR 识别气泡提醒：开启后在屏幕底部弹出识别结果与耗时卡片
    /// </summary>
    public bool ShowOcrNotification { get; set; } = true;

    /// <summary>
    /// 截图复制气泡提醒：开启后在屏幕底部弹出截图复制成功提示
    /// </summary>
    public bool ShowCaptureImageNotification { get; set; } = true;

    /// <summary>
    /// 兼容旧版气泡提醒配置
    /// </summary>
    public bool ShowNotificationBadge
    {
        get => ShowOcrNotification;
        set
        {
            ShowOcrNotification = value;
            ShowCaptureImageNotification = value;
        }
    }

    public bool ShowWindowAfterOcr { get; set; } = true;

    public bool StartWithWindows { get; set; } = false;

    /// <summary>
    /// 识别外部模型：开启后自动扫描同目录下 models 文件夹中的外部 Paddle 模型
    /// </summary>
    public bool ScanExternalModels { get; set; } = false;

    /// <summary>
    /// 兼容旧版配置字段
    /// </summary>
    public bool UseExternalModel
    {
        get => ScanExternalModels;
        set => ScanExternalModels = value;
    }

    /// <summary>
    /// 外部模型类型："LocalDirectory" (本地自定义目录) 或 "WebApi" (HTTP 接口)
    /// </summary>
    public string ExternalModelType { get; set; } = "LocalDirectory";

    /// <summary>
    /// 外部本地模型目录路径（默认 "models/v6"）
    /// </summary>
    public string ExternalModelPath { get; set; } = "models/v6";

    /// <summary>
    /// 外部 Web API 接口地址（默认 "http://127.0.0.1:8080/ocr"）
    /// </summary>
    public string ExternalApiUrl { get; set; } = "http://127.0.0.1:8080/ocr";

    private string _model = "PP-OCRv6_small";

    /// <summary>
    /// 当前生效的模型标识（如 "PP-OCRv6_small", "PP-OCRv6_tiny", "v6 (外部)"）
    /// </summary>
    public string Model
    {
        get
        {
            if (ScanExternalModels && string.Equals(ExternalModelType, "WebApi", StringComparison.OrdinalIgnoreCase))
            {
                return $"External:WebApi ({ExternalApiUrl})";
            }
            if (ScanExternalModels && string.Equals(ExternalModelType, "LocalDirectory", StringComparison.OrdinalIgnoreCase) &&
                _model == "PP-OCRv6_small" && !string.IsNullOrWhiteSpace(ExternalModelPath) && ExternalModelPath != "models/v6")
            {
                return $"External:Local ({ExternalModelPath})";
            }
            return _model;
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (value.StartsWith("External:WebApi", StringComparison.OrdinalIgnoreCase))
            {
                ScanExternalModels = true;
                ExternalModelType = "WebApi";
            }
            else if (value.StartsWith("External:Local", StringComparison.OrdinalIgnoreCase))
            {
                ScanExternalModels = true;
                ExternalModelType = "LocalDirectory";
                _model = value;
            }
            else
            {
                _model = value;
            }
        }
    }

    /// <summary>
    /// 兼容旧版配置字段
    /// </summary>
    public string EmbeddedModel
    {
        get => _model;
        set => _model = value;
    }

    /// <summary>
    /// 文字方向检测：开启后检测倒立/旋转文字（默认关闭，关闭可节省约 150MB 内存并显著提速）
    /// </summary>
    public bool EnableAngleDetection { get; set; } = false;

    /// <summary>
    /// 文本检测最大边长限制：等比缩放送检，兼顾显存与清晰度（推荐 1280，识别仍采用原图高清切片）
    /// </summary>
    public int MaxSideLen { get; set; } = 1280;

    /// <summary>
    /// 闲置自动休眠：开启后无操作达到指定时长时自动深度整理并释放非活跃物理内存
    /// </summary>
    public bool EnableIdleSleep { get; set; } = true;

    /// <summary>
    /// 闲置休眠等待时间（分钟）
    /// </summary>
    public int IdleSleepMinutes { get; set; } = 3;

    /// <summary>
    /// 开启本地文件日志服务
    /// </summary>
    public bool EnableLogging { get; set; } = false;

    /// <summary>
    /// 截图标注默认保存格式（PNG, JPEG, WEBP, BMP）
    /// </summary>
    public string DefaultSaveFormat { get; set; } = "PNG";

    /// <summary>
    /// 截图标注默认保存画质（10-100）
    /// </summary>
    public int DefaultSaveQuality { get; set; } = 90;

    /// <summary>
    /// 截图标注默认保存缩放百分比（如 100 代表 100%）
    /// </summary>
    public int DefaultSaveScale { get; set; } = 100;

    /// <summary>
    /// 截图上次保存的文件夹路径（为空时默认使用系统图片目录）
    /// </summary>
    public string? LastSaveDirectory { get; set; }

    // ==========================================
    // 翻译服务配置与月度额度保护
    // ==========================================

    /// <summary>
    /// 选区翻译全局快捷键（默认 "Alt+T"）
    /// </summary>
    public string TranslateHotkey { get; set; } = "Alt+T";

    /// <summary>
    /// 默认生效的翻译提供商 ("Baidu", "Google", "OpenAI")
    /// </summary>
    public string TranslateProvider { get; set; } = "Baidu";

    /// <summary>
    /// 翻译源语言代码（默认 "auto" 自动检测）
    /// </summary>
    public string TranslateSourceLang { get; set; } = "auto";

    /// <summary>
    /// 翻译目标语言代码（默认 "zh" 简体中文）
    /// </summary>
    public string TranslateTargetLang { get; set; } = "zh";

    /// <summary>
    /// 翻译额度记录月份（格式 "yyyy-MM"），用于跨月自动清零计数器
    /// </summary>
    public string TranslationUsageMonth { get; set; } = "";

    // --- 百度翻译 (双模支持：aiTextTranslate 大模型 + 通用翻译) ---
    public string BaiduAppId { get; set; } = "";
    public string BaiduSecretKey { get; set; } = ""; // 开发者信息中的密钥 (用于通用翻译 MD5 签名)
    public string BaiduApiKey { get; set; } = "";    // API Key 管理中的 Key (用于大模型翻译 Bearer 鉴权)
    public int BaiduMonthlyLimit { get; set; } = 1000000; // 默认 100 万字符
    public int BaiduUsedCount { get; set; } = 0;

    // --- 谷歌翻译 (NMT API) ---
    public string GoogleApiKey { get; set; } = "";
    public int GoogleMonthlyLimit { get; set; } = 500000; // 默认 50 万字符
    public int GoogleUsedCount { get; set; } = 0;

    // --- OpenAI 兼容接口 ---
    public string OpenAiApiEndpoint { get; set; } = "https://api.openai.com/v1/chat/completions";
    public string OpenAiApiKey { get; set; } = "";
    public string OpenAiModel { get; set; } = "gpt-4o-mini";
    public int OpenAiMonthlyLimit { get; set; } = 200000; // 默认 20 万 Token
    public int OpenAiUsedCount { get; set; } = 0;

    public AppSettings Clone() => new()
    {
        Hotkey = Hotkey,
        CaptureImageHotkey = CaptureImageHotkey,
        AnnotateHotkey = AnnotateHotkey,
        AutoCopy = AutoCopy,
        SilentMode = SilentMode,
        SilentStartup = SilentStartup,
        ShowOcrNotification = ShowOcrNotification,
        ShowCaptureImageNotification = ShowCaptureImageNotification,
        ShowWindowAfterOcr = ShowWindowAfterOcr,
        StartWithWindows = StartWithWindows,
        Model = Model,
        EmbeddedModel = EmbeddedModel,
        ScanExternalModels = ScanExternalModels,
        UseExternalModel = UseExternalModel,
        ExternalModelType = ExternalModelType,
        ExternalModelPath = ExternalModelPath,
        ExternalApiUrl = ExternalApiUrl,
        EnableAngleDetection = EnableAngleDetection,
        MaxSideLen = MaxSideLen,
        EnableIdleSleep = EnableIdleSleep,
        IdleSleepMinutes = IdleSleepMinutes,
        EnableLogging = EnableLogging,
        DefaultSaveFormat = DefaultSaveFormat,
        DefaultSaveQuality = DefaultSaveQuality,
        DefaultSaveScale = DefaultSaveScale,
        LastSaveDirectory = LastSaveDirectory,

        TranslateHotkey = TranslateHotkey,
        TranslateProvider = TranslateProvider,
        TranslateSourceLang = TranslateSourceLang,
        TranslateTargetLang = TranslateTargetLang,
        TranslationUsageMonth = TranslationUsageMonth,

        BaiduAppId = BaiduAppId,
        BaiduSecretKey = BaiduSecretKey,
        BaiduApiKey = BaiduApiKey,
        BaiduMonthlyLimit = BaiduMonthlyLimit,
        BaiduUsedCount = BaiduUsedCount,

        GoogleApiKey = GoogleApiKey,
        GoogleMonthlyLimit = GoogleMonthlyLimit,
        GoogleUsedCount = GoogleUsedCount,

        OpenAiApiEndpoint = OpenAiApiEndpoint,
        OpenAiApiKey = OpenAiApiKey,
        OpenAiModel = OpenAiModel,
        OpenAiMonthlyLimit = OpenAiMonthlyLimit,
        OpenAiUsedCount = OpenAiUsedCount
    };
}
