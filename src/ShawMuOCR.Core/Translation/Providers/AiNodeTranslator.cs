using ShawMuOCR.Core.Ai;
using ShawMuOCR.Core.Settings;

namespace ShawMuOCR.Core.Translation.Providers;

/// <summary>
/// 基于 API 节点池的翻译器：把翻译请求翻译成一次对话补全调用。
/// 节点由调用方按 Id 从池里解析，额度统一走 <see cref="IAiQuotaService"/>。
/// 提示词支持从设置中读取自定义模板或采用默认模板。
/// </summary>
public sealed class AiNodeTranslator : ITranslator
{
    private readonly ILlmClient _client;
    private readonly IAiQuotaService? _quota;
    private readonly Func<string>? _promptTemplateProvider;

    public string ProviderName => "AI";

    public AiNodeTranslator(
        ILlmClient client,
        IAiQuotaService? quota = null,
        Func<string>? promptTemplateProvider = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _quota = quota;
        _promptTemplateProvider = promptTemplateProvider;
    }

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return TranslationResult.Success(
                string.Empty, string.Empty,
                request.SourceLanguage, request.TargetLanguage,
                ProviderName);
        }

        if (_quota != null && !_quota.TryCheck(_client.ProfileId, out string? deny))
        {
            return TranslationResult.Failed(
                request.Text, request.SourceLanguage, request.TargetLanguage,
                ProviderName, deny ?? "该 AI 节点的本月额度已用尽");
        }

        string targetLanguageName = TranslationLanguages.GetDisplayName(request.TargetLanguage);
        string sourceLanguageName = string.IsNullOrWhiteSpace(request.SourceLanguage) || string.Equals(request.SourceLanguage, "auto", StringComparison.OrdinalIgnoreCase)
            ? "the detected language"
            : TranslationLanguages.GetDisplayName(request.SourceLanguage);

        string template = _promptTemplateProvider?.Invoke() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(template))
        {
            template = AiTranslatePrompt.Default;
        }

        if (template.Length > AiTranslatePrompt.MaxLength)
        {
            template = template[..AiTranslatePrompt.MaxLength];
        }

        // 宏替换
        string systemPrompt = template
            .Replace("{TargetLanguage}", targetLanguageName, StringComparison.OrdinalIgnoreCase)
            .Replace("{SourceLanguage}", sourceLanguageName, StringComparison.OrdinalIgnoreCase);

        // 如果用户自定义的提示词里没有写 {TargetLanguage} 宏，自动在尾部补齐目标语言要求，避免大模型自由发挥导致语言错误
        if (!template.Contains("{TargetLanguage}", StringComparison.OrdinalIgnoreCase) &&
            !systemPrompt.Contains(targetLanguageName, StringComparison.OrdinalIgnoreCase))
        {
            systemPrompt = $"{systemPrompt}\nTarget Language: {targetLanguageName}. Output ONLY the translated text.";
        }

        var response = await _client.CompleteAsync(
            new LlmRequest
            {
                SystemPrompt = systemPrompt,
                Prompt = request.Text,
                Temperature = 0.3
            },
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccess)
        {
            return TranslationResult.Failed(
                request.Text, request.SourceLanguage, request.TargetLanguage,
                ProviderName,
                $"AI 翻译请求失败: {response.ErrorMessage}",
                response.Elapsed);
        }

        _quota?.Commit(_client.ProfileId, response.TotalTokens);

        return TranslationResult.Success(
            request.Text,
            response.Content,
            request.SourceLanguage,
            request.TargetLanguage,
            ProviderName,
            consumedChars: request.Text.Length,
            consumedTokens: response.TotalTokens,
            elapsed: response.Elapsed);
    }
}
