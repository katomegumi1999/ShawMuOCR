using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShawMuOCR.Core.Translation.Providers;

public sealed class VolcengineTranslator : ITranslator, IDisposable
{
    public string ProviderName => "Volcengine";

    private const string ServiceHost = "translate.volcengineapi.com";
    private const string ServiceName = "translate";
    private const string ActionName = "TranslateText";
    private const string ApiVersion = "2020-06-01";

    private readonly Func<string> _getAccessKey;
    private readonly Func<string> _getSecretKey;
    private readonly Func<string>? _getRegion;
    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;

    public VolcengineTranslator(
        Func<string> getAccessKey,
        Func<string> getSecretKey,
        Func<string>? getRegion = null,
        HttpClient? httpClient = null)
    {
        _getAccessKey = getAccessKey;
        _getSecretKey = getSecretKey;
        _getRegion = getRegion;

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
        string ak = _getAccessKey()?.Trim() ?? string.Empty;
        string sk = _getSecretKey()?.Trim() ?? string.Empty;
        string region = _getRegion?.Invoke()?.Trim() ?? "cn-north-1";
        if (string.IsNullOrWhiteSpace(region)) region = "cn-north-1";

        if (string.IsNullOrWhiteSpace(ak) || string.IsNullOrWhiteSpace(sk))
        {
            return TranslationResult.Failed(
                request.Text,
                request.SourceLanguage,
                request.TargetLanguage,
                ProviderName,
                "未配置火山引擎凭证，请前往【设置 ➔ 翻译设置】中配置 AccessKey 与 SecretKey。");
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
            // 构造请求体 (零反射 Native AOT 安全)
            string srcLang = request.SourceLanguage;
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                if (!string.IsNullOrWhiteSpace(srcLang) && !string.Equals(srcLang, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    writer.WriteString("SourceLanguage", MapLanguageCode(srcLang));
                }
                writer.WriteString("TargetLanguage", MapLanguageCode(request.TargetLanguage));
                writer.WriteStartArray("TextList");
                writer.WriteStringValue(request.Text);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            byte[] bodyBytes = stream.ToArray();
            string bodySha = Convert.ToHexString(SHA256.HashData(bodyBytes)).ToLowerInvariant();

            string datetime = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
            string date = datetime[..8];

            // 构造规范化请求
            const string queryString = $"Action={ActionName}&Version={ApiVersion}";
            const string signedHeaders = "content-type;host;x-content-sha256;x-date";
            string canonicalHeaders = $"content-type:application/json; charset=utf-8\nhost:{ServiceHost}\nx-content-sha256:{bodySha}\nx-date:{datetime}\n";
            string canonicalRequest = $"POST\n/\n{queryString}\n{canonicalHeaders}\n{signedHeaders}\n{bodySha}";
            string hashedCanonicalRequest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))).ToLowerInvariant();

            // 构造待签名字符串
            string credentialScope = $"{date}/{region}/{ServiceName}/request";
            string stringToSign = $"HMAC-SHA256\n{datetime}\n{credentialScope}\n{hashedCanonicalRequest}";

            // 派生签名密钥
            byte[] kDate = HMACSHA256.HashData(Encoding.UTF8.GetBytes(sk), Encoding.UTF8.GetBytes(date));
            byte[] kRegion = HMACSHA256.HashData(kDate, Encoding.UTF8.GetBytes(region));
            byte[] kService = HMACSHA256.HashData(kRegion, Encoding.UTF8.GetBytes(ServiceName));
            byte[] kSigning = HMACSHA256.HashData(kService, Encoding.UTF8.GetBytes("request"));
            string signature = Convert.ToHexString(HMACSHA256.HashData(kSigning, Encoding.UTF8.GetBytes(stringToSign))).ToLowerInvariant();

            string authHeader = $"HMAC-SHA256 Credential={ak}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";

            // 发送请求
            string requestUrl = $"https://{ServiceHost}/?{queryString}";
            using var reqMsg = new HttpRequestMessage(HttpMethod.Post, requestUrl);
            reqMsg.Headers.Host = ServiceHost;
            reqMsg.Headers.TryAddWithoutValidation("X-Date", datetime);
            reqMsg.Headers.TryAddWithoutValidation("X-Content-Sha256", bodySha);
            reqMsg.Headers.TryAddWithoutValidation("Authorization", authHeader);

            reqMsg.Content = new ByteArrayContent(bodyBytes);
            reqMsg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

            var resp = await _httpClient.SendAsync(reqMsg, cancellationToken);
            string responseText = await resp.Content.ReadAsStringAsync(cancellationToken);
            sw.Stop();

            using var doc = JsonDocument.Parse(responseText);
            var root = doc.RootElement;

            // 检查错误响应
            if (root.TryGetProperty("ResponseMetadata", out var meta))
            {
                if (meta.TryGetProperty("Error", out var errorElem) && errorElem.ValueKind == JsonValueKind.Object)
                {
                    string code = errorElem.TryGetProperty("Code", out var c) ? c.GetString() ?? "" : "";
                    string msg = errorElem.TryGetProperty("Message", out var m) ? m.GetString() ?? "" : "";
                    return TranslationResult.Failed(
                        request.Text,
                        request.SourceLanguage,
                        request.TargetLanguage,
                        ProviderName,
                        $"火山引擎接口错误 [{code}]: {msg}",
                        sw.Elapsed);
                }
            }

            if (root.TryGetProperty("TranslationList", out var listElem) && listElem.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                string detectedLang = request.SourceLanguage;

                foreach (var item in listElem.EnumerateArray())
                {
                    if (item.TryGetProperty("Translation", out var transElem))
                    {
                        if (sb.Length > 0) sb.AppendLine();
                        sb.Append(transElem.GetString());
                    }
                    if (item.TryGetProperty("DetectedSourceLanguage", out var detElem) && detElem.ValueKind == JsonValueKind.String)
                    {
                        string? d = detElem.GetString();
                        if (!string.IsNullOrWhiteSpace(d)) detectedLang = d;
                    }
                }

                return TranslationResult.Success(
                    originalText: request.Text,
                    translatedText: sb.ToString(),
                    sourceLang: detectedLang,
                    targetLang: request.TargetLanguage,
                    providerName: ProviderName,
                    consumedChars: request.Text.Length,
                    elapsed: sw.Elapsed);
            }

            return TranslationResult.Failed(
                request.Text,
                request.SourceLanguage,
                request.TargetLanguage,
                ProviderName,
                $"火山引擎返回未预期的格式: {responseText}",
                sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return TranslationResult.Failed(
                request.Text,
                request.SourceLanguage,
                request.TargetLanguage,
                ProviderName,
                "火山引擎翻译请求超时或已取消",
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
                $"火山引擎翻译异常: {ex.Message}",
                sw.Elapsed);
        }
    }

    private static string MapLanguageCode(string lang)
    {
        return lang.ToLowerInvariant() switch
        {
            "zh" or "zh-cn" or "zh-hans" => "zh",
            "zh-tw" or "zh-hant" => "zh-Hant",
            "en" => "en",
            "ja" => "ja",
            "ko" => "ko",
            "fr" => "fr",
            "de" => "de",
            "es" => "es",
            "ru" => "ru",
            "pt" => "pt",
            "it" => "it",
            "vi" => "vi",
            "th" => "th",
            "ar" => "ar",
            _ => lang
        };
    }

    public void Dispose()
    {
        if (_disposeClient)
        {
            _httpClient.Dispose();
        }
    }
}
