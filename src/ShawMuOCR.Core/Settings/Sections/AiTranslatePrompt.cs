namespace ShawMuOCR.Core.Settings;

/// <summary>
/// AI 翻译提示词常量与默认模板。
/// 支持用户在设置中自定义提示词，支持宏变量 {TargetLanguage} 与 {SourceLanguage}。
/// </summary>
public static class AiTranslatePrompt
{
    /// <summary>
    /// 默认提示词模板：
    /// 支持 {TargetLanguage} 宏自动替换为目标语言全称（如 "Simplified Chinese", "English" 等），
    /// 若提示词中未包含 {TargetLanguage}，系统会自动在尾部追加目标语言要求，确保大模型输出正确语言。
    /// </summary>
    public const string Default =
        "You are a professional, accurate translator. Translate the given text into {TargetLanguage}. " +
        "Maintain the original layout, formatting, and punctuation. Output ONLY the translated text without any " +
        "explanation, preamble, notes, markdown quotation, or code fences.";

    /// <summary>
    /// 提示词最大字符上限
    /// </summary>
    public const int MaxLength = 4000;
}
