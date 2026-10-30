namespace ShawMuOCR.Core.Settings;

/// <summary>
/// 翻译服务与额度保护分块配置
/// </summary>
public sealed class TranslationSettings
{
    /// <summary>
    /// 默认生效的翻译提供商 ("Baidu", "Google", "OpenAI", "Volcengine", "Aliyun")
    /// </summary>
    public string Provider { get; set; } = "Baidu";

    /// <summary>
    /// 翻译源语言代码（默认 "auto" 自动检测）
    /// </summary>
    public string SourceLang { get; set; } = "auto";

    /// <summary>
    /// 翻译目标语言代码（默认 "zh" 简体中文）
    /// </summary>
    public string TargetLang { get; set; } = "zh";

    /// <summary>
    /// 翻译额度记录月份（格式 "yyyy-MM"），用于跨月自动清零计数器
    /// </summary>
    public string UsageMonth { get; set; } = string.Empty;

    // --- 百度翻译 (双模支持：aiTextTranslate 大模型 + 通用翻译) ---
    public string BaiduAppId { get; set; } = string.Empty;
    public string BaiduSecretKey { get; set; } = string.Empty; // 开发者信息中的密钥 (用于通用翻译 MD5 签名)
    public string BaiduApiKey { get; set; } = string.Empty;    // API Key 管理中的 Key (用于大模型翻译 Bearer 鉴权)
    public int BaiduMonthlyLimit { get; set; } = 1000000;      // 默认 100 万字符
    public int BaiduUsedCount { get; set; } = 0;

    // --- 谷歌翻译 (NMT API) ---
    public string GoogleApiKey { get; set; } = string.Empty;
    public int GoogleMonthlyLimit { get; set; } = 500000;      // 默认 50 万字符
    public int GoogleUsedCount { get; set; } = 0;

    // --- OpenAI 兼容接口 ---
    public string OpenAiApiEndpoint { get; set; } = "https://api.openai.com/v1/chat/completions";
    public string OpenAiApiKey { get; set; } = string.Empty;
    public string OpenAiModel { get; set; } = "gpt-4o-mini";
    public int OpenAiMonthlyLimit { get; set; } = 200000;      // 默认 20 万 Token
    public int OpenAiUsedCount { get; set; } = 0;

    // --- 火山引擎翻译 (Volcengine TranslateText API) ---
    public string VolcengineAccessKey { get; set; } = string.Empty;
    public string VolcengineSecretKey { get; set; } = string.Empty;
    public string VolcengineRegion { get; set; } = "cn-north-1";
    public int VolcengineMonthlyLimit { get; set; } = 2000000; // 默认 200 万字符
    public int VolcengineUsedCount { get; set; } = 0;

    // --- 阿里云机器翻译 (Alibaba Cloud Machine Translation API) ---
    public string AliyunAccessKeyId { get; set; } = string.Empty;
    public string AliyunAccessKeySecret { get; set; } = string.Empty;
    public string AliyunApiEndpoint { get; set; } = "http://mt.cn-hangzhou.aliyuncs.com/api/translate/web/general";
    public int AliyunMonthlyLimit { get; set; } = 1000000;    // 默认 100 万字符
    public int AliyunUsedCount { get; set; } = 0;

    public TranslationSettings Clone() => new()
    {
        Provider = Provider,
        SourceLang = SourceLang,
        TargetLang = TargetLang,
        UsageMonth = UsageMonth,
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
        AliyunUsedCount = AliyunUsedCount
    };
}
