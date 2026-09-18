using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShawMuOCR.Core.Translation.Providers;

public sealed class BaiduTranslator : ITranslator, IDisposable
{
    public string ProviderName => "Baidu";

    private const string AiEndpoint = "https://fanyi-api.baidu.com/ait/api/aiTextTranslate";
    private const string GeneralEndpoint = "https://fanyi-api.baidu.com/api/trans/vip/translate";

    private readonly Func<string> _getAppId;
    private readonly Func<string> _getSecretKey;
    private readonly Func<string>? _getApiKey;
    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;

    public BaiduTranslator(
        Func<string> getAppId,
        Func<string> getSecretKey,
        Func<string>? getApiKey = null,
        HttpClient? httpClient = null)
    {
        _getAppId = getAppId;
        _getSecretKey = getSecretKey;
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

    public BaiduTranslator(
        Func<string> getAppId,
        Func<string> getSecretKey,
        HttpClient? httpClient = null)
        : this(getAppId, getSecretKey, getSecretKey, httpClient)
    {
    }

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        string appId = _getAppId()?.Trim() ?? string.Empty;
        string secretKey = _getSecretKey()?.Trim() ?? string.Empty;
        string apiKey = _getApiKey?.Invoke()?.Trim() ?? string.Empty;

        bool hasApiKey = !string.IsNullOrWhiteSpace(apiKey);
        bool hasSecretKey = !string.IsNullOrWhiteSpace(secretKey);

        if (string.IsNullOrWhiteSpace(appId) || (!hasApiKey && !hasSecretKey))
        {
            return TranslationResult.Failed(
                request.Text,
                request.SourceLanguage,
                request.TargetLanguage,
                ProviderName,
                "未配置百度翻译凭证，请前往【设置】中配置 App ID 以及 密钥 (Secret Key) 或 API Key。");
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

        // 1. 如果配置了 API Key，优先尝试大模型文本翻译 (aiTextTranslate)
        if (hasApiKey)
        {
            var aiResult = await TryAiTextTranslateAsync(appId, apiKey, request, sw, cancellationToken);
            if (aiResult.IsSuccess)
            {
                return aiResult;
            }

            // 如果报错是 52003 (未授权用户)，且填了密钥，自动智能回退至通用文本翻译 (MD5 签名)
            if (hasSecretKey && (aiResult.ErrorMessage?.Contains("52003") == true || aiResult.ErrorMessage?.Contains("未授权") == true))
            {
                var fallbackResult = await TryGeneralTranslateAsync(appId, secretKey, request, sw, cancellationToken);
                if (fallbackResult.IsSuccess)
                {
                    return fallbackResult;
                }
            }

            return aiResult;
        }

        // 2. 如果只配置了 密钥 (Secret Key)，先尝试通用翻译；若失败且该密钥可作为 Token，再尝试大模型接口
        if (hasSecretKey)
        {
            var genResult = await TryGeneralTranslateAsync(appId, secretKey, request, sw, cancellationToken);
            if (genResult.IsSuccess)
            {
                return genResult;
            }

            // 如果通用翻译报签名错误，且用户可能把大模型 API Key 错填到了密钥框，尝试用 Bearer 鉴权调用大模型接口
            if (genResult.ErrorMessage?.Contains("54001") == true)
            {
                var fallbackAi = await TryAiTextTranslateAsync(appId, secretKey, request, sw, cancellationToken);
                if (fallbackAi.IsSuccess)
                {
                    return fallbackAi;
                }
            }

            return genResult;
        }

        return TranslationResult.Failed(
            request.Text,
            request.SourceLanguage,
            request.TargetLanguage,
            ProviderName,
            "未找到有效的百度翻译凭证",
            sw.Elapsed);
    }

    private async Task<TranslationResult> TryAiTextTranslateAsync(
        string appId,
        string apiKey,
        TranslationRequest request,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        try
        {
            string fromCode = MapLanguageCode(request.SourceLanguage);
            string toCode = MapLanguageCode(request.TargetLanguage);
            string cleanApiKey = apiKey.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? apiKey.Substring("Bearer ".Length).Trim()
                : apiKey;

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, AiEndpoint);
            httpRequest.Headers.TryAddWithoutValidation("X-Appid", appId);
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cleanApiKey);

            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                writer.WriteString("appid", appId);
                writer.WriteString("from", fromCode);
                writer.WriteString("to", toCode);
                writer.WriteString("q", request.Text);
                writer.WriteEndObject();
            }

            string jsonPayload = Encoding.UTF8.GetString(ms.ToArray());
            httpRequest.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            string json = await response.Content.ReadAsStringAsync(cancellationToken);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("error_code", out var errorCodeProp))
            {
                string code = errorCodeProp.ValueKind == JsonValueKind.Number
                    ? errorCodeProp.GetInt32().ToString()
                    : errorCodeProp.GetString() ?? "";

                if (!string.IsNullOrEmpty(code) && code != "0" && code != "52000")
                {
                    string msg = root.TryGetProperty("error_msg", out var msgProp) ? msgProp.GetString() ?? "" : "";
                    string friendlyMsg = GetFriendlyErrorMessage(code, msg);
                    return TranslationResult.Failed(
                        request.Text,
                        request.SourceLanguage,
                        request.TargetLanguage,
                        ProviderName,
                        friendlyMsg,
                        sw.Elapsed);
                }
            }

            if (root.TryGetProperty("trans_result", out var transResultProp) &&
                transResultProp.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var item in transResultProp.EnumerateArray())
                {
                    if (item.TryGetProperty("dst", out var dstProp))
                    {
                        if (sb.Length > 0) sb.AppendLine();
                        sb.Append(dstProp.GetString());
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

            if (root.TryGetProperty("result", out var resultProp))
            {
                if (resultProp.ValueKind == JsonValueKind.String)
                {
                    return TranslationResult.Success(
                        request.Text,
                        resultProp.GetString() ?? "",
                        request.SourceLanguage,
                        request.TargetLanguage,
                        ProviderName,
                        consumedChars: request.Text.Length,
                        consumedTokens: 0,
                        elapsed: sw.Elapsed);
                }
                else if (resultProp.TryGetProperty("trans_result", out var innerTrans) && innerTrans.ValueKind == JsonValueKind.Array)
                {
                    var sb = new StringBuilder();
                    foreach (var item in innerTrans.EnumerateArray())
                    {
                        if (item.TryGetProperty("dst", out var dstProp))
                        {
                            if (sb.Length > 0) sb.AppendLine();
                            sb.Append(dstProp.GetString());
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
            }

            return TranslationResult.Failed(
                request.Text,
                request.SourceLanguage,
                request.TargetLanguage,
                ProviderName,
                "百度大模型翻译返回了未知的响应格式",
                sw.Elapsed);
        }
        catch (Exception ex)
        {
            return TranslationResult.Failed(
                request.Text,
                request.SourceLanguage,
                request.TargetLanguage,
                ProviderName,
                $"百度大模型翻译请求失败: {ex.Message}",
                sw.Elapsed);
        }
    }

    private async Task<TranslationResult> TryGeneralTranslateAsync(
        string appId,
        string secretKey,
        TranslationRequest request,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        try
        {
            string fromCode = MapLanguageCode(request.SourceLanguage);
            string toCode = MapLanguageCode(request.TargetLanguage);
            string salt = Random.Shared.Next(100000, 999999).ToString();

            // 签名算法: MD5(appid + q + salt + key)
            string signRaw = $"{appId}{request.Text}{salt}{secretKey}";
            byte[] hashBytes = MD5.HashData(Encoding.UTF8.GetBytes(signRaw));
            string sign = Convert.ToHexStringLower(hashBytes);

            var formParams = new Dictionary<string, string>
            {
                ["q"] = request.Text,
                ["from"] = fromCode,
                ["to"] = toCode,
                ["appid"] = appId,
                ["salt"] = salt,
                ["sign"] = sign
            };

            using var formContent = new FormUrlEncodedContent(formParams);
            using var response = await _httpClient.PostAsync(
                GeneralEndpoint,
                formContent,
                cancellationToken);

            string json = await response.Content.ReadAsStringAsync(cancellationToken);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("error_code", out var errorCodeProp))
            {
                string code = errorCodeProp.ValueKind == JsonValueKind.Number
                    ? errorCodeProp.GetInt32().ToString()
                    : errorCodeProp.GetString() ?? "";

                if (!string.IsNullOrEmpty(code) && code != "0" && code != "52000")
                {
                    string msg = root.TryGetProperty("error_msg", out var msgProp) ? msgProp.GetString() ?? "" : "";
                    string friendlyMsg = GetFriendlyErrorMessage(code, msg);
                    return TranslationResult.Failed(
                        request.Text,
                        request.SourceLanguage,
                        request.TargetLanguage,
                        ProviderName,
                        friendlyMsg,
                        sw.Elapsed);
                }
            }

            if (root.TryGetProperty("trans_result", out var transResultProp) &&
                transResultProp.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var item in transResultProp.EnumerateArray())
                {
                    if (item.TryGetProperty("dst", out var dstProp))
                    {
                        if (sb.Length > 0) sb.AppendLine();
                        sb.Append(dstProp.GetString());
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
                "百度通用翻译返回了未知的响应格式",
                sw.Elapsed);
        }
        catch (Exception ex)
        {
            return TranslationResult.Failed(
                request.Text,
                request.SourceLanguage,
                request.TargetLanguage,
                ProviderName,
                $"百度通用翻译请求失败: {ex.Message}",
                sw.Elapsed);
        }
    }

    private static string MapLanguageCode(string code) => code.ToLowerInvariant() switch
    {
        "auto" => "auto",
        "zh" or "zh-cn" => "zh",
        "en" => "en",
        "ja" or "jp" => "jp",
        "ko" or "kor" => "kor",
        "fr" or "fra" => "fra",
        "de" => "de",
        "ru" => "ru",
        "es" or "spa" => "spa",
        _ => code
    };

    private static string GetFriendlyErrorMessage(string code, string rawMsg) => code switch
    {
        "52001" => "请求超时，请重试",
        "52002" => "系统错误，请重试",
        "52003" => "未授权用户：请检查 App ID 与密钥/API Key 是否填写正确，并在控制台确认是否已开通对应翻译服务",
        "54000" => "必填参数为空",
        "54001" => "签名/Key 鉴权错误，请核对密钥或 API Key",
        "54003" => "访问频率受限（QPS超限），请稍后重试",
        "54004" => "账户余额不足，请前往百度翻译开放平台充值",
        "54005" => "长 query 请求频繁，请降低调用频率",
        "58000" => "客户端 IP 非法，请检查控制台 IP 白名单设置",
        "58001" => "译文语言方向不支持",
        "58002" => "服务当前已关闭",
        _ => $"百度翻译接口错误 [{code}]: {rawMsg}"
    };

    public void Dispose()
    {
        if (_disposeClient)
        {
            _httpClient.Dispose();
        }
    }
}
