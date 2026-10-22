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
    public bool SilentMode { get; set; } = true;

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
    /// 自动扫描同目录下 models 文件夹中的 Paddle 模型
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
    /// 当前生效的模型标识（默认 "PP-OCRv6_small"，如 "PP-OCRv6_tiny", "PP-OCRv6_medium" 等）
    /// </summary>
    public string Model
    {
        get
        {
            if (string.Equals(ExternalModelType, "WebApi", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(ExternalApiUrl))
            {
                return $"External:WebApi ({ExternalApiUrl})";
            }
            if (string.Equals(ExternalModelType, "LocalDirectory", StringComparison.OrdinalIgnoreCase) &&
                _model == "PP-OCRv6_small" && !string.IsNullOrWhiteSpace(ExternalModelPath) && ExternalModelPath != "models/v6")
            {
                return $"External:Local ({ExternalModelPath})";
            }
            return string.IsNullOrWhiteSpace(_model) ? "PP-OCRv6_small" : _model;
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                _model = "PP-OCRv6_small";
                return;
            }
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
    /// OCR 推理硬件后端模式："Auto" (默认，自动选择最优后端), "GPU" (Vulkan GPU 加速), "CPU" (纯 CPU AVX2/SIMD)
    /// </summary>
    public string OcrBackend { get; set; } = "Auto";

    /// <summary>
    /// 指定使用的物理 GPU 设备索引（-1 为自动优选，0 为首选 GPU，1 为次选 GPU...）
    /// </summary>
    public int SelectedGpuIndex { get; set; } = -1;

    /// <summary>
    /// 文字方向检测：开启后检测倒立/旋转文字（默认开启）
    /// </summary>
    public bool EnableAngleDetection { get; set; } = true;

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
    /// 开启日志定期自动清理
    /// </summary>
    public bool EnableLogAutoClean { get; set; } = true;

    /// <summary>
    /// 日志保留天数（超过该天数的日志文件自动清理删除，默认 7 天）
    /// </summary>
    public int LogRetentionDays { get; set; } = 7;

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
    /// OCR 窗格全局快捷键（默认 "Alt+D"），截屏识别后在悬浮窗中显示结果而不自动复制
    /// </summary>
    public string OcrPopupHotkey { get; set; } = "Alt+D";

    /// <summary>
    /// 随手便笺全局快捷键（默认 "Alt+N"），呼出自由输入浮窗
    /// </summary>
    public string QuickNoteHotkey { get; set; } = "Alt+N";

    /// <summary>
    /// 是否启用小沐剪贴板功能（默认关闭）
    /// </summary>
    public bool EnableClipboard { get; set; } = false;

    /// <summary>
    /// 极速剪贴板快捷面板全局快捷键（默认 "Ctrl+~"）
    /// </summary>
    public string ClipboardPanelHotkey { get; set; } = "Ctrl+~";

    /// <summary>
    /// 常用语快捷呼出键（默认 "Ctrl+1"）
    /// </summary>
    public string PhrasesPanelHotkey { get; set; } = "Ctrl+1";

    /// <summary>
    /// 快捷面板背景底色（十六进制颜色，默认 "#F4F4F6" 浅灰）
    /// </summary>
    public string ClipboardPanelBackground { get; set; } = "#F4F4F6";

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

    // --- 火山引擎翻译 (Volcengine TranslateText API) ---
    public string VolcengineAccessKey { get; set; } = "";
    public string VolcengineSecretKey { get; set; } = "";
    public string VolcengineRegion { get; set; } = "cn-north-1";
    public int VolcengineMonthlyLimit { get; set; } = 2000000; // 默认 200 万字符
    public int VolcengineUsedCount { get; set; } = 0;

    // --- 阿里云机器翻译 (Alibaba Cloud Machine Translation API) ---
    public string AliyunAccessKeyId { get; set; } = "";
    public string AliyunAccessKeySecret { get; set; } = "";
    public string AliyunApiEndpoint { get; set; } = "http://mt.cn-hangzhou.aliyuncs.com/api/translate/web/general";
    public int AliyunMonthlyLimit { get; set; } = 1000000; // 默认 100 万字符
    public int AliyunUsedCount { get; set; } = 0;

    // ==========================================
    // 截图标注编辑器配置 (粗细与字号记忆)
    // ==========================================
    public float AnnotationStrokeWidth { get; set; } = 4f;
    public float AnnotationFontSize { get; set; } = 40f;

    // ==========================================
    // 批量 OCR 转换配置
    // ==========================================
    public bool BatchExportTxt { get; set; } = true;
    public bool BatchExportXlsx { get; set; } = true;
    public bool BatchExportDocx { get; set; } = false;
    public bool BatchSplitColumnsByBlocks { get; set; } = true;
    public int BatchImageRotationIndex { get; set; } = 0;
    public bool BatchIsSuffixEnabled { get; set; } = true;
    public string BatchSuffixText { get; set; } = "_ocr";
    public bool BatchUseOriginalFolder { get; set; } = true;
    public string BatchCustomOutputDir { get; set; } = "";
    public int BatchMaxCores { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);
    public int BatchGpuStreams { get; set; } = 1;
    public int BatchCpuLimitPercent { get; set; } = 50;

    // ==========================================
    // 内置 HTTP API 服务配置
    // ==========================================
    public int HttpApiPort { get; set; } = 8080;
    public bool HttpApiAllowRemote { get; set; } = false;

    /// <summary>
    /// 局域网访问令牌（Bearer）。非空时 HTTP 接口强制鉴权；
    /// 开启 <see cref="HttpApiAllowRemote"/> 时若无令牌会自动生成，服务端拒绝无令牌的远程暴露。
    /// </summary>
    public string HttpApiAccessToken { get; set; } = "";

    /// <summary>
    /// 显式允许局域网无令牌访问（默认关闭）。
    /// 用于"临时分享给同网段同事"：开启后同网段任何设备都可调用本机 OCR 并占用 CPU，
    /// 但接口不读取任何本机文件。仅当 <see cref="HttpApiAccessToken"/> 为空时才有意义。
    /// </summary>
    public bool HttpApiAllowAnonymousRemote { get; set; } = false;

    public int HttpApiMaxCores { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);
    public int HttpApiGpuStreams { get; set; } = 1;
    public int HttpApiCpuLimitPercent { get; set; } = 50;

    // ==========================================
    // UI 体验、个性主题与布局记忆
    // ==========================================
    public bool IsSidebarExpanded { get; set; } = true;
    public string? ThemeBackgroundImagePath { get; set; }
    public double ThemeBackgroundBlurRadius { get; set; } = 0.0;
    public double ThemeComponentOpacity { get; set; } = 0.12;
    public double ThemeButtonOpacity { get; set; } = 0.12;
    public bool ThemeEnableAcrylic { get; set; } = true;
    public string? ThemeBackgroundColorHex { get; set; }
    public string? ThemeCardColorHex { get; set; }
    public string? ThemeTextColorHex { get; set; }
    public string? ThemeFloatingWindowBackgroundColorHex { get; set; }
    public string? ThemeFloatingWindowTextColorHex { get; set; }

    // 推理设备调度模式 ("Auto", "GPU", "CPU")
    public string BatchSchedulerMode { get; set; } = "Auto";
    public string HttpApiSchedulerMode { get; set; } = "Auto";

    /// <summary>
    /// 上次退出的导航子页面，下次非静默启动时优先先渲染该页面
    /// </summary>
    public string LastActivePage { get; set; } = "Ocr";

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
        OcrBackend = OcrBackend,
        SelectedGpuIndex = SelectedGpuIndex,
        EnableAngleDetection = EnableAngleDetection,
        MaxSideLen = MaxSideLen,
        EnableIdleSleep = EnableIdleSleep,
        IdleSleepMinutes = IdleSleepMinutes,
        EnableLogging = EnableLogging,
        EnableLogAutoClean = EnableLogAutoClean,
        LogRetentionDays = LogRetentionDays,
        DefaultSaveFormat = DefaultSaveFormat,
        DefaultSaveQuality = DefaultSaveQuality,
        DefaultSaveScale = DefaultSaveScale,
        LastSaveDirectory = LastSaveDirectory,

        TranslateHotkey = TranslateHotkey,
        OcrPopupHotkey = OcrPopupHotkey,
        QuickNoteHotkey = QuickNoteHotkey,
        EnableClipboard = EnableClipboard,
        ClipboardPanelHotkey = ClipboardPanelHotkey,
        PhrasesPanelHotkey = PhrasesPanelHotkey,
        ClipboardPanelBackground = ClipboardPanelBackground,
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
        OpenAiUsedCount = OpenAiUsedCount,

        VolcengineAccessKey = VolcengineAccessKey,
        VolcengineSecretKey = VolcengineSecretKey,
        VolcengineRegion = VolcengineRegion,
        VolcengineMonthlyLimit = VolcengineMonthlyLimit,
        VolcengineUsedCount = VolcengineUsedCount,

        AliyunAccessKeyId = AliyunAccessKeyId,
        AliyunAccessKeySecret = AliyunAccessKeySecret,
        AliyunApiEndpoint = AliyunApiEndpoint,
        AliyunMonthlyLimit = AliyunMonthlyLimit,
        AliyunUsedCount = AliyunUsedCount,

        BatchExportTxt = BatchExportTxt,
        BatchExportXlsx = BatchExportXlsx,
        BatchExportDocx = BatchExportDocx,
        BatchSplitColumnsByBlocks = BatchSplitColumnsByBlocks,
        BatchImageRotationIndex = BatchImageRotationIndex,
        BatchIsSuffixEnabled = BatchIsSuffixEnabled,
        BatchSuffixText = BatchSuffixText,
        BatchUseOriginalFolder = BatchUseOriginalFolder,
        BatchCustomOutputDir = BatchCustomOutputDir,
        BatchMaxCores = BatchMaxCores,
        BatchGpuStreams = BatchGpuStreams,
        BatchCpuLimitPercent = BatchCpuLimitPercent,

        HttpApiPort = HttpApiPort,
        HttpApiAllowRemote = HttpApiAllowRemote,
        HttpApiAccessToken = HttpApiAccessToken,
        HttpApiAllowAnonymousRemote = HttpApiAllowAnonymousRemote,
        HttpApiMaxCores = HttpApiMaxCores,
        HttpApiGpuStreams = HttpApiGpuStreams,
        HttpApiCpuLimitPercent = HttpApiCpuLimitPercent,

        IsSidebarExpanded = IsSidebarExpanded,
        ThemeBackgroundImagePath = ThemeBackgroundImagePath,
        ThemeBackgroundBlurRadius = ThemeBackgroundBlurRadius,
        ThemeComponentOpacity = ThemeComponentOpacity,
        ThemeButtonOpacity = ThemeButtonOpacity,
        ThemeEnableAcrylic = ThemeEnableAcrylic,
        ThemeBackgroundColorHex = ThemeBackgroundColorHex,
        ThemeCardColorHex = ThemeCardColorHex,
        ThemeTextColorHex = ThemeTextColorHex,
        ThemeFloatingWindowBackgroundColorHex = ThemeFloatingWindowBackgroundColorHex,
        ThemeFloatingWindowTextColorHex = ThemeFloatingWindowTextColorHex,
        BatchSchedulerMode = BatchSchedulerMode,
        HttpApiSchedulerMode = HttpApiSchedulerMode,
        AnnotationStrokeWidth = AnnotationStrokeWidth,
        AnnotationFontSize = AnnotationFontSize,
        LastActivePage = LastActivePage
    };
}
