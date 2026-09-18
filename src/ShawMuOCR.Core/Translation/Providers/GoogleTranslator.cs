using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ShawMuOCR.Core.Translation.Providers;

public sealed class GoogleTranslator : ITranslator, IDisposable
{
    public string ProviderName => "Google";

    private readonly Func<string> _getApiKey;
    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;

    public GoogleTranslator(
        Func<string> getApiKey,
        HttpClient? httpClient = null)
    {
        _getApiKey = getApiKey;
        if (httpClient != null)
        {
            _httpClient = httpClient;
            _disposeClient = false;
        }
        else
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            _disposeClient = true;
        }
    }

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        string apiKey = _getApiKey()?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return TranslationResult.Failed(
                request.Text,
                request.SourceLanguage,
                request.TargetLanguage,
                ProviderName,
                "未配置谷歌翻译 API Key，请前往【设置】中配置。");
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
            string targetCode = MapLanguageCode(request.TargetLanguage);
            string? sourceCode = !string.Equals(request.SourceLanguage, "auto", StringComparison.OrdinalIgnoreCase)
                ? MapLanguageCode(request.SourceLanguage)
                : null;

            using var memStream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(memStream))
            {
                writer.WriteStartObject();
                writer.WriteStartArray("q");
                writer.WriteStringValue(request.Text);
                writer.WriteEndArray();
                writer.WriteString("target", targetCode);
                writer.WriteString("format", "text");
                writer.WriteString("model", "nmt");
                if (!string.IsNullOrEmpty(sourceCode))
                {
                    writer.WriteString("source", sourceCode);
                }
                writer.WriteEndObject();
            }

            string requestJson = Encoding.UTF8.GetString(memStream.ToArray());
            using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

            string url = $"https://translation.googleapis.com/language/translate/v2?key={Uri.EscapeDataString(apiKey)}";
            using var response = await _httpClient.PostAsync(url, content, cancellationToken);
            string responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            sw.Stop();

            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errorProp))
            {
                string errorMsg = errorProp.TryGetProperty("message", out var msgProp) ? msgProp.GetString() ?? "" : "";
                int errorCode = errorProp.TryGetProperty("code", out var codeProp) ? codeProp.GetInt32() : 0;

                string friendlyTip = errorCode switch
                {
                    403 => "（提示：请求被拒绝。请检查 GCP 控制台：1. 是否在【API 库】中启用了「Cloud Translation API」；2. API 密钥是否设置了【IP/网址/API 限制】；3. 项目是否已关联结算账户）",
                    400 => "（提示：请求参数或 API Key 无效，请检查 Key 是否复制完整）",
                    _ => string.Empty
                };

                return TranslationResult.Failed(
                    request.Text,
                    request.SourceLanguage,
                    request.TargetLanguage,
                    ProviderName,
                    $"谷歌翻译接口错误 [{errorCode}]: {errorMsg}{friendlyTip}",
                    sw.Elapsed);
            }

            if (root.TryGetProperty("data", out var dataProp) &&
                dataProp.TryGetProperty("translations", out var transArrayProp) &&
                transArrayProp.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var item in transArrayProp.EnumerateArray())
                {
                    if (item.TryGetProperty("translatedText", out var textProp))
                    {
                        if (sb.Length > 0) sb.AppendLine();
                        // 谷歌翻译可能对部分字符返回 HTML 实体（如 &#39;），进行标准实体解码
                        sb.Append(WebUtility.HtmlDecode(textProp.GetString() ?? ""));
                    }
                }

                return TranslationResult.Success(
                    request.Text,
                    sb.ToString(),
                    request.SourceLanguage,
                    request.TargetLanguage,
                    ProviderName,
                    consumedChars: request.Text.Length,
                    consumedTokens: 0,
                    elapsed: sw.Elapsed);
            }

            return TranslationResult.Failed(
                request.Text,
                request.SourceLanguage,
                request.TargetLanguage,
                ProviderName,
                "谷歌翻译返回了未知的响应格式",
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
                $"谷歌翻译请求失败: {ex.Message}",
                sw.Elapsed);
        }
    }

    private static string MapLanguageCode(string code) => code.ToLowerInvariant() switch
    {
        "auto" => "auto",
        "zh" or "zh-cn" => "zh-CN",
        "zh-tw" => "zh-TW",
        "en" => "en",
        "ja" or "jp" => "ja",
        "ko" or "kor" => "ko",
        "fr" or "fra" => "fr",
        "de" => "de",
        "ru" => "ru",
        "es" or "spa" => "es",
        _ => code
    };

    public void Dispose()
    {
        if (_disposeClient)
        {
            _httpClient.Dispose();
        }
    }
}
