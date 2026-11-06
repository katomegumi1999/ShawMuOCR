using ShawMuOCR.Core.Ai;

namespace ShawMuOCR.Core.Translation.Providers;

/// <summary>
/// 基于 API 节点池的翻译器：把翻译请求翻译成一次对话补全调用。
/// 与 <see cref="OpenAiTranslator"/> 的区别是它不持有配置，节点由调用方按 Id 从池里解析，
/// 额度统一走 <see cref="IAiQuotaService"/>。
/// </summary>
public sealed class AiNodeTranslator : ITranslator
{
    private readonly ILlmClient _client;
    private readonly IAiQuotaService? _quota;

    public string ProviderName => "AI";

    public AiNodeTranslator(ILlmClient client, IAiQuotaService? quota = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _quota = quota;
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
        string systemPrompt =
            $"You are a professional, accurate translator. Translate the given text into {targetLanguageName}. " +
            "Maintain the original layout and punctuation. Output ONLY the translated text without any " +
            "explanation, markdown quotation or code fences.";

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
