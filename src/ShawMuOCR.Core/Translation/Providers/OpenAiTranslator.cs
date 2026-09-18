using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ShawMuOCR.Core.Translation.Providers;

public sealed class OpenAiTranslator : ITranslator, IDisposable
{
    public string ProviderName => "OpenAI";

    private readonly Func<string> _getBaseUrl;
    private readonly Func<string> _getApiKey;
    private readonly Func<string> _getModel;
    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;

    public OpenAiTranslator(
        Func<string> getBaseUrl,
        Func<string> getApiKey,
        Func<string> getModel,
        HttpClient? httpClient = null)
    {
        _getBaseUrl = getBaseUrl;
        _getApiKey = getApiKey;
        _getModel = getModel;
        if (httpClient != null)
        {
            _httpClient = httpClient;
            _disposeClient = false;
        }
        else
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            _disposeClient = true;
        }
    }

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        string apiKey = _getApiKey()?.Trim() ?? string.Empty;
        string baseUrl = _getBaseUrl()?.Trim() ?? string.Empty;
        string model = _getModel()?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return TranslationResult.Failed(
                request.Text,
                request.SourceLanguage,
                request.TargetLanguage,
                ProviderName,
                "未配置 OpenAI API Key，请前往【设置】中配置。");
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = "https://api.openai.com/v1";
        }
        if (string.IsNullOrWhiteSpace(model))
        {
            model = "gpt-4o-mini";
        }

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return TranslationResult.Success(
                string.Empty,
                string.Empty,
                request.SourceLanguage,
                request.TargetLanguage,
                ProviderName);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            string targetLanguageName = TranslationLanguages.GetDisplayName(request.TargetLanguage);
            string systemPrompt = $"You are a professional, accurate translator. Translate the given text into {targetLanguageName}. Maintain the original layout and punctuation. Output ONLY the translated text without any explanation, markdown quotation or code fences.";

            using var memStream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(memStream))
            {
                writer.WriteStartObject();
                writer.WriteString("model", model);
                writer.WriteNumber("temperature", 0.3);
                writer.WriteStartArray("messages");

                writer.WriteStartObject();
                writer.WriteString("role", "system");
                writer.WriteString("content", systemPrompt);
                writer.WriteEndObject();

                writer.WriteStartObject();
                writer.WriteString("role", "user");
                writer.WriteString("content", request.Text);
                writer.WriteEndObject();

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            string requestJson = Encoding.UTF8.GetString(memStream.ToArray());
            using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

            string endpoint = baseUrl.TrimEnd('/');
            if (!endpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                endpoint += "/chat/completions";
            }

            using var reqMsg = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = content
            };
            reqMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await _httpClient.SendAsync(reqMsg, cancellationToken);
            string responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            sw.Stop();

            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errorProp))
            {
                string errorMsg = errorProp.TryGetProperty("message", out var msgProp) ? msgProp.GetString() ?? "" : "";
                return TranslationResult.Failed(
                    request.Text,
                    request.SourceLanguage,
                    request.TargetLanguage,
                    ProviderName,
                    $"OpenAI 接口错误: {errorMsg}",
                    sw.Elapsed);
            }

            int totalTokens = 0;
            if (root.TryGetProperty("usage", out var usageProp) &&
                usageProp.TryGetProperty("total_tokens", out var totalTokensProp))
            {
                totalTokens = totalTokensProp.GetInt32();
            }

            if (root.TryGetProperty("choices", out var choicesProp) &&
                choicesProp.ValueKind == JsonValueKind.Array)
            {
                var firstChoice = choicesProp.EnumerateArray().FirstOrDefault();
                if (firstChoice.ValueKind != JsonValueKind.Undefined &&
                    firstChoice.TryGetProperty("message", out var msgObj) &&
                    msgObj.TryGetProperty("content", out var contentProp))
                {
                    string translated = contentProp.GetString()?.Trim() ?? "";
                    return TranslationResult.Success(
                        request.Text,
                        translated,
                        request.SourceLanguage,
                        request.TargetLanguage,
                        ProviderName,
                        consumedChars: request.Text.Length,
                        consumedTokens: totalTokens,
                        elapsed: sw.Elapsed);
                }
            }

            return TranslationResult.Failed(
                request.Text,
                request.SourceLanguage,
                request.TargetLanguage,
                ProviderName,
                "OpenAI 返回了未知的响应格式",
                sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return TranslationResult.Failed(
                request.Text,
                request.SourceLanguage,
                request.TargetLanguage,
                ProviderName,
                $"OpenAI 翻译请求失败: {ex.Message}",
                sw.Elapsed);
        }
    }

    public void Dispose()
    {
        if (_disposeClient)
        {
            _httpClient.Dispose();
        }
    }
}
