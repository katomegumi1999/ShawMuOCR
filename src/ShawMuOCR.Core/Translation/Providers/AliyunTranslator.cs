using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShawMuOCR.Core.Translation.Providers;

public sealed class AliyunTranslator : ITranslator, IDisposable
{
    public string ProviderName => "Aliyun";

    public const string DefaultEndpoint = "http://mt.cn-hangzhou.aliyuncs.com/api/translate/web/general";

    private readonly Func<string> _getAccessKeyId;
    private readonly Func<string> _getAccessKeySecret;
    private readonly Func<string>? _getEndpoint;
    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;

    public AliyunTranslator(
        Func<string> getAccessKeyId,
        Func<string> getAccessKeySecret,
        Func<string>? getEndpoint = null,
        HttpClient? httpClient = null)
    {
        _getAccessKeyId = getAccessKeyId;
        _getAccessKeySecret = getAccessKeySecret;
        _getEndpoint = getEndpoint;

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
        string akId = _getAccessKeyId()?.Trim() ?? string.Empty;
        string akSecret = _getAccessKeySecret()?.Trim() ?? string.Empty;
        string endpoint = _getEndpoint?.Invoke()?.Trim() ?? DefaultEndpoint;
        if (string.IsNullOrWhiteSpace(endpoint)) endpoint = DefaultEndpoint;

        if (string.IsNullOrWhiteSpace(akId) || string.IsNullOrWhiteSpace(akSecret))
        {
            return TranslationResult.Failed(
                request.Text,
                request.SourceLanguage,
                request.TargetLanguage,
                ProviderName,
                "未配置阿里云机器翻译凭证，请前往【设置 ➔ 翻译设置】中配置 AccessKeyId 与 AccessKeySecret。");
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
            var uri = new Uri(endpoint);
            string scene = uri.AbsolutePath.Contains("ecommerce", StringComparison.OrdinalIgnoreCase) ? "title" : "general";

            string srcLang = MapLanguageCode(request.SourceLanguage);
            string tgtLang = MapLanguageCode(request.TargetLanguage);

            // 构造请求体 (零反射 Native AOT 安全)
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("FormatType", "text");
                writer.WriteString("SourceLanguage", srcLang);
                writer.WriteString("TargetLanguage", tgtLang);
                writer.WriteString("SourceText", request.Text);
                writer.WriteString("Scene", scene);
                writer.WriteEndObject();
            }
            byte[] bodyBytes = stream.ToArray();

            // Step 1: Content-MD5
            string bodyMd5 = Convert.ToBase64String(MD5.HashData(bodyBytes));

            // Step 2 & 3: GMT Date and StringToSign
            string date = DateTimeOffset.UtcNow.ToString("r", CultureInfo.InvariantCulture);
            string uuid = Guid.NewGuid().ToString();
            string path = uri.AbsolutePath;
            if (string.IsNullOrWhiteSpace(path)) path = "/";

            const string method = "POST";
            const string accept = "application/json";
            const string contentType = "application/json; charset=utf-8";

            string stringToSign = $"{method}\n{accept}\n{bodyMd5}\n{contentType}\n{date}\n" +
                                 $"x-acs-signature-method:HMAC-SHA1\n" +
                                 $"x-acs-signature-nonce:{uuid}\n" +
                                 $"x-acs-version:2019-01-02\n" +
                                 $"{path}";

            // Step 4: HMAC-SHA1
            byte[] keyBytes = Encoding.UTF8.GetBytes(akSecret);
            byte[] signDataBytes = Encoding.UTF8.GetBytes(stringToSign);
            string signature = Convert.ToBase64String(HMACSHA1.HashData(keyBytes, signDataBytes));
            string authHeader = $"acs {akId}:{signature}";

            // Send Request
            using var reqMsg = new HttpRequestMessage(HttpMethod.Post, uri);
            reqMsg.Headers.Host = uri.Host;
            reqMsg.Headers.Accept.Clear();
            reqMsg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            reqMsg.Headers.TryAddWithoutValidation("Date", date);
            reqMsg.Headers.TryAddWithoutValidation("Authorization", authHeader);
            reqMsg.Headers.TryAddWithoutValidation("x-acs-signature-nonce", uuid);
            reqMsg.Headers.TryAddWithoutValidation("x-acs-signature-method", "HMAC-SHA1");
            reqMsg.Headers.TryAddWithoutValidation("x-acs-version", "2019-01-02");

            reqMsg.Content = new ByteArrayContent(bodyBytes);
            reqMsg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            reqMsg.Content.Headers.TryAddWithoutValidation("Content-MD5", bodyMd5);

            var resp = await _httpClient.SendAsync(reqMsg, cancellationToken);
            string responseText = await resp.Content.ReadAsStringAsync(cancellationToken);
            sw.Stop();

            using var doc = JsonDocument.Parse(responseText);
            var root = doc.RootElement;

            // 兼容检查 errorCode / errorMsg 格式
            if (TryGetPropertyIgnoreCase(root, "errorCode", out var errCodeElem))
            {
                string errCode = errCodeElem.ValueKind == JsonValueKind.String ? (errCodeElem.GetString() ?? "") : errCodeElem.ToString();
                string errMsg = TryGetPropertyIgnoreCase(root, "errorMsg", out var errMsgElem) ? (errMsgElem.GetString() ?? errMsgElem.ToString()) : "";
                return TranslationResult.Failed(
                    request.Text,
                    request.SourceLanguage,
                    request.TargetLanguage,
                    ProviderName,
                    $"阿里云机器翻译错误 [{errCode}]: {errMsg}",
                    sw.Elapsed);
            }

            // 兼容检查 Code / Message 格式 (同时支持数字 200 与字符串 "200" / "OK")
            if (TryGetPropertyIgnoreCase(root, "Code", out var codeElem))
            {
                bool isSuccess = false;
                if (codeElem.ValueKind == JsonValueKind.Number)
                {
                    isSuccess = codeElem.GetInt32() == 200;
                }
                else if (codeElem.ValueKind == JsonValueKind.String)
                {
                    string? s = codeElem.GetString();
                    isSuccess = string.Equals(s, "200", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(s, "OK", StringComparison.OrdinalIgnoreCase);
                }

                if (!isSuccess)
                {
                    string codeStr = codeElem.ValueKind == JsonValueKind.String ? (codeElem.GetString() ?? "") : codeElem.ToString();
                    string msg = TryGetPropertyIgnoreCase(root, "Message", out var m) ? (m.GetString() ?? m.ToString()) : "";
                    return TranslationResult.Failed(
                        request.Text,
                        request.SourceLanguage,
                        request.TargetLanguage,
                        ProviderName,
                        $"阿里云接口返回错误码 {codeStr}: {msg}",
                        sw.Elapsed);
                }
            }

            // 提取 Data (兼容 Data / data)
            if (TryGetPropertyIgnoreCase(root, "Data", out var dataElem))
            {
                string translatedText = string.Empty;
                string detectedLang = request.SourceLanguage;

                if (TryGetPropertyIgnoreCase(dataElem, "Translated", out var transElem))
                {
                    translatedText = transElem.ValueKind == JsonValueKind.String ? (transElem.GetString() ?? string.Empty) : transElem.ToString();
                }

                if (TryGetPropertyIgnoreCase(dataElem, "DetectedLanguage", out var detElem))
                {
                    string? d = detElem.GetString();
                    if (!string.IsNullOrWhiteSpace(d)) detectedLang = d;
                }

                return TranslationResult.Success(
                    originalText: request.Text,
                    translatedText: translatedText,
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
                $"阿里云未返回预期的翻译结果: {responseText}",
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
                "阿里云翻译请求超时或已取消",
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
                $"阿里云翻译异常: {ex.Message}",
                sw.Elapsed);
        }
    }

    private static string MapLanguageCode(string lang)
    {
        return lang.ToLowerInvariant() switch
        {
            "zh" or "zh-cn" or "zh-hans" => "zh",
            "zh-tw" or "zh-hant" => "zh-tw",
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
            "auto" => "auto",
            _ => lang
        };
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        if (element.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    public void Dispose()
    {
        if (_disposeClient)
        {
            _httpClient.Dispose();
        }
    }
}
