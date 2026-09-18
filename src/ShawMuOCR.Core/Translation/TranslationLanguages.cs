namespace ShawMuOCR.Core.Translation;

public record LanguageItem(string Code, string DisplayName);

public static class TranslationLanguages
{
    public const string Auto = "auto";
    public const string Chinese = "zh";
    public const string English = "en";
    public const string Japanese = "ja";
    public const string Korean = "ko";
    public const string French = "fr";
    public const string German = "de";
    public const string Russian = "ru";
    public const string Spanish = "es";

    public static IReadOnlyList<LanguageItem> SupportedSourceLanguages { get; } = new[]
    {
        new LanguageItem("auto", "自动检测"),
        new LanguageItem("en", "英语"),
        new LanguageItem("zh", "中文 (简体)"),
        new LanguageItem("ja", "日语"),
        new LanguageItem("ko", "韩语"),
        new LanguageItem("fr", "法语"),
        new LanguageItem("de", "德语"),
        new LanguageItem("ru", "俄语"),
        new LanguageItem("es", "西班牙语")
    };

    public static IReadOnlyList<LanguageItem> SupportedTargetLanguages { get; } = new[]
    {
        new LanguageItem("zh", "中文 (简体)"),
        new LanguageItem("en", "英语"),
        new LanguageItem("ja", "日语"),
        new LanguageItem("ko", "韩语"),
        new LanguageItem("fr", "法语"),
        new LanguageItem("de", "德语"),
        new LanguageItem("ru", "俄语"),
        new LanguageItem("es", "西班牙语")
    };

    public static IReadOnlyList<LanguageItem> All => SupportedSourceLanguages;
    public static IReadOnlyList<LanguageItem> AllTargets => SupportedTargetLanguages;

    public static string GetName(string code) => GetDisplayName(code);

    public static string GetDisplayName(string code)
    {
        var item = SupportedSourceLanguages.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase))
                   ?? SupportedTargetLanguages.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));
        return item?.DisplayName ?? code;
    }
}
