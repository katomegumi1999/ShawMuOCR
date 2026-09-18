namespace ShawMuOCR.Core.Translation;

public record ProviderQuotaInfo(
    string ProviderName,
    int UsedThisMonth,
    int MonthlyLimit,
    string UnitName, // "字符" or "Token"
    double Percentage);

public interface ITranslationService
{
    string CurrentProviderName { get; }

    Task<TranslationResult> TranslateAsync(
        string text,
        string? sourceLanguage = null,
        string? targetLanguage = null,
        CancellationToken cancellationToken = default);

    Task<TranslationResult> TranslateWithProviderAsync(
        string providerName,
        string text,
        string? sourceLanguage = null,
        string? targetLanguage = null,
        CancellationToken cancellationToken = default);

    ProviderQuotaInfo GetQuotaInfo(string providerName);

    event Action? QuotaUpdated;
}
