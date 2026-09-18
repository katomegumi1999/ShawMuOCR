namespace ShawMuOCR.Core.Translation;

public record TranslationRequest(
    string Text,
    string SourceLanguage = "auto",
    string TargetLanguage = "zh");

public record TranslationResult(
    bool IsSuccess,
    string OriginalText,
    string TranslatedText,
    string SourceLanguage,
    string TargetLanguage,
    string ProviderName,
    string? ErrorMessage = null,
    int ConsumedCharacters = 0,
    int ConsumedTokens = 0,
    TimeSpan Elapsed = default)
{
    public static TranslationResult Success(
        string originalText,
        string translatedText,
        string sourceLang,
        string targetLang,
        string providerName,
        int consumedChars = 0,
        int consumedTokens = 0,
        TimeSpan elapsed = default) =>
        new(
            IsSuccess: true,
            OriginalText: originalText,
            TranslatedText: translatedText,
            SourceLanguage: sourceLang,
            TargetLanguage: targetLang,
            ProviderName: providerName,
            ConsumedCharacters: consumedChars,
            ConsumedTokens: consumedTokens,
            Elapsed: elapsed);

    public static TranslationResult Failed(
        string originalText,
        string sourceLang,
        string targetLang,
        string providerName,
        string errorMessage,
        TimeSpan elapsed = default) =>
        new(
            IsSuccess: false,
            OriginalText: originalText,
            TranslatedText: string.Empty,
            SourceLanguage: sourceLang,
            TargetLanguage: targetLang,
            ProviderName: providerName,
            ErrorMessage: errorMessage,
            Elapsed: elapsed);
}

public interface ITranslator
{
    string ProviderName { get; }

    Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default);
}
