namespace ShawMuOCR.Core.Settings;

/// <summary>
/// 【旧版】单节点 AI OCR 配置，仅用于读取历史 settings.json 完成迁移。
/// 迁移后由 <c>JsonSettingsService</c> 置为 null，新代码不应再读写它。
/// 节点连接信息现已统一收敛到 <see cref="AiSettings.Profiles"/>。
/// </summary>
public sealed class AiOcrSettings
{
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "qwen-vl-max";
    public string Prompt { get; set; } = AiOcrPrompt.Default;

    public AiOcrSettings Clone() => new()
    {
        BaseUrl = BaseUrl,
        ApiKey = ApiKey,
        Model = Model,
        Prompt = Prompt
    };
}

/// <summary>
/// AI OCR 的提示词默认值与边界常量
/// </summary>
public static class AiOcrPrompt
{
    /// <summary>
    /// 默认提示词：只做文字识别，不做改写、不翻译、不补全。
    /// 明确禁止 Markdown 代码块围栏，因为很多模型会把纯文本结果包进 ``` 里，
    /// 直接落盘 .txt 时会把符号一并写进文件。
    /// </summary>
    public const string Default =
        "识别并输出图片中的全部文字内容，保持原有的阅读顺序与分段。\n" +
        "要求：\n" +
        "1. 只输出识别到的文字本身，不要任何解释、前言、总结，也不要用 Markdown 代码块包裹。\n" +
        "2. 表格按行输出，同一行的单元格之间用制表符分隔。\n" +
        "3. 数学公式用 LaTeX 表示，行内公式用 $...$，独立公式用 $$...$$。\n" +
        "4. 保留原文的标点、大小写与错别字，不要纠正、不要翻译、不要补全。\n" +
        "5. 如果图片中没有可识别的文字，输出空内容。";

    /// <summary>
    /// 提示词长度上限。过长的提示词会挤占图片与输出预算，且难以在设置界面中维护。
    /// </summary>
    public const int MaxLength = 4000;
}
