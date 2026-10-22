using ShawMuOCR.Core.Settings;
using ShawMuOCR.Core.Translation.Providers;

namespace ShawMuOCR.Core.Translation;

public sealed class TranslationService : ITranslationService, IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly Dictionary<string, ITranslator> _translators = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient? _httpClient;
    private readonly object _lock = new();

    public event Action? QuotaUpdated;

    public string CurrentProviderName => _settingsService.Current.TranslateProvider;

    public TranslationService(ISettingsService settingsService, HttpClient? httpClient = null)
    {
        _settingsService = settingsService;
        _httpClient = httpClient;

        _translators["Baidu"] = new BaiduTranslator(
            () => _settingsService.Current.BaiduAppId,
            () => _settingsService.Current.BaiduSecretKey,
            () => _settingsService.Current.BaiduApiKey,
            _httpClient);

        _translators["Google"] = new GoogleTranslator(
            () => _settingsService.Current.GoogleApiKey,
            _httpClient);

        _translators["OpenAI"] = new OpenAiTranslator(
            () => _settingsService.Current.OpenAiApiEndpoint,
            () => _settingsService.Current.OpenAiApiKey,
            () => _settingsService.Current.OpenAiModel,
            _httpClient);

        _translators["Volcengine"] = new VolcengineTranslator(
            () => _settingsService.Current.VolcengineAccessKey,
            () => _settingsService.Current.VolcengineSecretKey,
            () => _settingsService.Current.VolcengineRegion,
            _httpClient);

        _translators["Aliyun"] = new AliyunTranslator(
            () => _settingsService.Current.AliyunAccessKeyId,
            () => _settingsService.Current.AliyunAccessKeySecret,
            () => _settingsService.Current.AliyunApiEndpoint,
            _httpClient);
    }

    public Task<TranslationResult> TranslateAsync(
        string text,
        string? sourceLanguage = null,
        string? targetLanguage = null,
        CancellationToken cancellationToken = default)
    {
        return TranslateWithProviderAsync(
            CurrentProviderName,
            text,
            sourceLanguage,
            targetLanguage,
            cancellationToken);
    }

    public async Task<TranslationResult> TranslateWithProviderAsync(
        string providerName,
        string text,
        string? sourceLanguage = null,
        string? targetLanguage = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return TranslationResult.Success(
                originalText: text ?? string.Empty,
                translatedText: string.Empty,
                sourceLang: sourceLanguage ?? _settingsService.Current.TranslateSourceLang,
                targetLang: targetLanguage ?? _settingsService.Current.TranslateTargetLang,
                providerName: providerName);
        }

        string source = !string.IsNullOrWhiteSpace(sourceLanguage) ? sourceLanguage : _settingsService.Current.TranslateSourceLang;
        string target = !string.IsNullOrWhiteSpace(targetLanguage) ? targetLanguage : _settingsService.Current.TranslateTargetLang;

        if (!_translators.TryGetValue(providerName, out var translator))
        {
            return TranslationResult.Failed(
                text, source, target, providerName,
                $"未知的翻译提供商: {providerName}，请在设置中选择 百度/谷歌/OpenAI。");
        }

        // 1. 检查并处理跨月额度重置
        CheckAndResetMonthlyQuota();

        // 2. 检查当月配额上限拦截
        var settings = _settingsService.Current;
        if (string.Equals(providerName, "Baidu", StringComparison.OrdinalIgnoreCase))
        {
            if (settings.BaiduMonthlyLimit > 0 && settings.BaiduUsedCount >= settings.BaiduMonthlyLimit)
            {
                return TranslationResult.Failed(
                    text, source, target, providerName,
                    $"已达到本月百度翻译字符额度上限（{settings.BaiduMonthlyLimit:N0} 字符），已自动拦截请求以防产生额外费用。您可以在【系统设置 ➔ 翻译设置】中调整额度或清空计数。");
            }
        }
        else if (string.Equals(providerName, "Google", StringComparison.OrdinalIgnoreCase))
        {
            if (settings.GoogleMonthlyLimit > 0 && settings.GoogleUsedCount >= settings.GoogleMonthlyLimit)
            {
                return TranslationResult.Failed(
                    text, source, target, providerName,
                    $"已达到本月谷歌翻译字符额度上限（{settings.GoogleMonthlyLimit:N0} 字符），已自动拦截请求以防产生额外费用。您可以在【系统设置 ➔ 翻译设置】中调整额度或清空计数。");
            }
        }
        else if (string.Equals(providerName, "OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            if (settings.OpenAiMonthlyLimit > 0 && settings.OpenAiUsedCount >= settings.OpenAiMonthlyLimit)
            {
                return TranslationResult.Failed(
                    text, source, target, providerName,
                    $"已达到本月 OpenAI Token 额度上限（{settings.OpenAiMonthlyLimit:N0} Token），已自动拦截请求以防产生额外费用。您可以在【系统设置 ➔ 翻译设置】中调整额度或清空计数。");
            }
        }
        else if (string.Equals(providerName, "Volcengine", StringComparison.OrdinalIgnoreCase))
        {
            if (settings.VolcengineMonthlyLimit > 0 && settings.VolcengineUsedCount >= settings.VolcengineMonthlyLimit)
            {
                return TranslationResult.Failed(
                    text, source, target, providerName,
                    $"已达到本月火山引擎翻译字符额度上限（{settings.VolcengineMonthlyLimit:N0} 字符），已自动拦截请求以防产生额外费用。您可以在【系统设置 ➔ 翻译设置】中调整额度或清空计数。");
            }
        }
        else if (string.Equals(providerName, "Aliyun", StringComparison.OrdinalIgnoreCase))
        {
            if (settings.AliyunMonthlyLimit > 0 && settings.AliyunUsedCount >= settings.AliyunMonthlyLimit)
            {
                return TranslationResult.Failed(
                    text, source, target, providerName,
                    $"已达到本月阿里云机器翻译字符额度上限（{settings.AliyunMonthlyLimit:N0} 字符），已自动拦截请求以防产生额外费用。您可以在【系统设置 ➔ 翻译设置】中调整额度或清空计数。");
            }
        }

        // 3. 执行翻译
        var req = new TranslationRequest(text, source, target);
        var result = await translator.TranslateAsync(req, cancellationToken);

        // 4. 成功后扣减额度并持久化
        if (result.IsSuccess)
        {
            lock (_lock)
            {
                var updated = _settingsService.Current.Clone();
                if (string.Equals(providerName, "Baidu", StringComparison.OrdinalIgnoreCase))
                {
                    updated.BaiduUsedCount += result.ConsumedCharacters > 0 ? result.ConsumedCharacters : text.Length;
                }
                else if (string.Equals(providerName, "Google", StringComparison.OrdinalIgnoreCase))
                {
                    updated.GoogleUsedCount += result.ConsumedCharacters > 0 ? result.ConsumedCharacters : text.Length;
                }
                else if (string.Equals(providerName, "OpenAI", StringComparison.OrdinalIgnoreCase))
                {
                    updated.OpenAiUsedCount += result.ConsumedTokens > 0 ? result.ConsumedTokens : Math.Max(1, text.Length / 2);
                }
                else if (string.Equals(providerName, "Volcengine", StringComparison.OrdinalIgnoreCase))
                {
                    updated.VolcengineUsedCount += result.ConsumedCharacters > 0 ? result.ConsumedCharacters : text.Length;
                }
                else if (string.Equals(providerName, "Aliyun", StringComparison.OrdinalIgnoreCase))
                {
                    updated.AliyunUsedCount += result.ConsumedCharacters > 0 ? result.ConsumedCharacters : text.Length;
                }

                _settingsService.Save(updated);
            }

            QuotaUpdated?.Invoke();
        }

        return result;
    }

    public ProviderQuotaInfo GetQuotaInfo(string providerName)
    {
        CheckAndResetMonthlyQuota();
        var s = _settingsService.Current;

        if (string.Equals(providerName, "Google", StringComparison.OrdinalIgnoreCase))
        {
            double pct = s.GoogleMonthlyLimit > 0 ? Math.Min(100.0, (double)s.GoogleUsedCount / s.GoogleMonthlyLimit * 100.0) : 0;
            return new ProviderQuotaInfo("Google", s.GoogleUsedCount, s.GoogleMonthlyLimit, "字符", pct);
        }
        if (string.Equals(providerName, "OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            double pct = s.OpenAiMonthlyLimit > 0 ? Math.Min(100.0, (double)s.OpenAiUsedCount / s.OpenAiMonthlyLimit * 100.0) : 0;
            return new ProviderQuotaInfo("OpenAI", s.OpenAiUsedCount, s.OpenAiMonthlyLimit, "Token", pct);
        }
        if (string.Equals(providerName, "Volcengine", StringComparison.OrdinalIgnoreCase))
        {
            double pct = s.VolcengineMonthlyLimit > 0 ? Math.Min(100.0, (double)s.VolcengineUsedCount / s.VolcengineMonthlyLimit * 100.0) : 0;
            return new ProviderQuotaInfo("Volcengine", s.VolcengineUsedCount, s.VolcengineMonthlyLimit, "字符", pct);
        }
        if (string.Equals(providerName, "Aliyun", StringComparison.OrdinalIgnoreCase))
        {
            double pct = s.AliyunMonthlyLimit > 0 ? Math.Min(100.0, (double)s.AliyunUsedCount / s.AliyunMonthlyLimit * 100.0) : 0;
            return new ProviderQuotaInfo("Aliyun", s.AliyunUsedCount, s.AliyunMonthlyLimit, "字符", pct);
        }

        // Default Baidu
        double baiduPct = s.BaiduMonthlyLimit > 0 ? Math.Min(100.0, (double)s.BaiduUsedCount / s.BaiduMonthlyLimit * 100.0) : 0;
        return new ProviderQuotaInfo("Baidu", s.BaiduUsedCount, s.BaiduMonthlyLimit, "字符", baiduPct);
    }

    private void CheckAndResetMonthlyQuota()
    {
        string currentMonth = DateTime.Now.ToString("yyyy-MM");
        lock (_lock)
        {
            var s = _settingsService.Current;
            if (s.TranslationUsageMonth != currentMonth)
            {
                var clone = s.Clone();
                clone.TranslationUsageMonth = currentMonth;
                clone.BaiduUsedCount = 0;
                clone.GoogleUsedCount = 0;
                clone.OpenAiUsedCount = 0;
                clone.VolcengineUsedCount = 0;
                clone.AliyunUsedCount = 0;
                _settingsService.Save(clone);
                QuotaUpdated?.Invoke();
            }
        }
    }

    public void Dispose()
    {
        foreach (var translator in _translators.Values)
        {
            if (translator is IDisposable disp)
            {
                disp.Dispose();
            }
        }
    }
}
