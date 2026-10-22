using System.Net;
using System.Text;
using ShawMuOCR.Core.Clipboard;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.Core.Translation;
using ShawMuOCR.Core.Translation.Providers;
using ShawMuOCR.ViewModels;
using Xunit;

namespace ShawMuOCR.Tests;

public class TranslationServiceTests
{
    private sealed class MockSettingsService : ISettingsService
    {
        public AppSettings Current { get; private set; } = new();
        public string SettingsFilePath => "memory://settings.json";
        public event Action<AppSettings>? SettingsChanged;

        public AppSettings Load() => Current.Clone();

        public void Save(AppSettings settings)
        {
            Current = settings.Clone();
            SettingsChanged?.Invoke(Current);
        }
    }

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    [Fact]
    public void TranslationLanguages_ReturnsCorrectNames()
    {
        Assert.Equal("自动检测", TranslationLanguages.GetName("auto"));
        Assert.Equal("中文 (简体)", TranslationLanguages.GetName("zh"));
        Assert.Equal("英语", TranslationLanguages.GetName("en"));
        Assert.Equal("日语", TranslationLanguages.GetName("ja"));

        Assert.NotEmpty(TranslationLanguages.All);
        Assert.NotEmpty(TranslationLanguages.AllTargets);
        Assert.DoesNotContain(TranslationLanguages.AllTargets, l => l.Code == "auto");
    }

    [Fact]
    public async Task TranslationService_Interception_WhenBaiduLimitExceeded()
    {
        var settingsService = new MockSettingsService();
        var s = settingsService.Current;
        s.TranslateProvider = "Baidu";
        s.BaiduAppId = "test_app_id";
        s.BaiduSecretKey = "test_secret_key";
        s.BaiduMonthlyLimit = 1000;
        s.BaiduUsedCount = 1000;
        s.TranslationUsageMonth = DateTime.Now.ToString("yyyy-MM");
        settingsService.Save(s);

        using var service = new TranslationService(settingsService);
        var result = await service.TranslateAsync("Hello World");

        Assert.False(result.IsSuccess);
        Assert.Contains("百度翻译字符额度上限", result.ErrorMessage);
    }

    [Fact]
    public async Task TranslationService_Interception_WhenGoogleLimitExceeded()
    {
        var settingsService = new MockSettingsService();
        var s = settingsService.Current;
        s.TranslateProvider = "Google";
        s.GoogleApiKey = "test_key";
        s.GoogleMonthlyLimit = 500;
        s.GoogleUsedCount = 500;
        s.TranslationUsageMonth = DateTime.Now.ToString("yyyy-MM");
        settingsService.Save(s);

        using var service = new TranslationService(settingsService);
        var result = await service.TranslateAsync("Hello World");

        Assert.False(result.IsSuccess);
        Assert.Contains("谷歌翻译字符额度上限", result.ErrorMessage);
    }

    [Fact]
    public async Task TranslationService_Interception_WhenOpenAiLimitExceeded()
    {
        var settingsService = new MockSettingsService();
        var s = settingsService.Current;
        s.TranslateProvider = "OpenAI";
        s.OpenAiApiKey = "test_key";
        s.OpenAiMonthlyLimit = 200;
        s.OpenAiUsedCount = 200;
        s.TranslationUsageMonth = DateTime.Now.ToString("yyyy-MM");
        settingsService.Save(s);

        using var service = new TranslationService(settingsService);
        var result = await service.TranslateAsync("Hello World");

        Assert.False(result.IsSuccess);
        Assert.Contains("OpenAI Token 额度上限", result.ErrorMessage);
    }

    [Fact]
    public void TranslationService_MonthRollover_ResetsCountsAutomatically()
    {
        var settingsService = new MockSettingsService();
        var s = settingsService.Current;
        s.TranslationUsageMonth = "2020-01"; // Old month
        s.BaiduUsedCount = 9999;
        s.GoogleUsedCount = 8888;
        s.OpenAiUsedCount = 7777;
        settingsService.Save(s);

        using var service = new TranslationService(settingsService);
        var info = service.GetQuotaInfo("Baidu");

        Assert.Equal(0, info.UsedThisMonth);
        Assert.Equal(DateTime.Now.ToString("yyyy-MM"), settingsService.Current.TranslationUsageMonth);
        Assert.Equal(0, settingsService.Current.BaiduUsedCount);
        Assert.Equal(0, settingsService.Current.GoogleUsedCount);
        Assert.Equal(0, settingsService.Current.OpenAiUsedCount);
    }

    [Fact]
    public async Task TranslationService_MissingKeys_ReturnsFriendlyErrorMessage()
    {
        var settingsService = new MockSettingsService();
        var s = settingsService.Current;
        s.TranslateProvider = "Baidu";
        s.BaiduAppId = "";
        s.BaiduSecretKey = "";
        s.TranslationUsageMonth = DateTime.Now.ToString("yyyy-MM");
        settingsService.Save(s);

        using var service = new TranslationService(settingsService);
        var result = await service.TranslateAsync("Hello");

        Assert.False(result.IsSuccess);
        Assert.Contains("未配置百度翻译", result.ErrorMessage);
    }

    [Fact]
    public async Task BaiduTranslator_MockSuccess_ParsesResponseCorrectly()
    {
        string capturedUrl = string.Empty;
        string capturedAuthScheme = string.Empty;
        string capturedAuthParam = string.Empty;
        string capturedBody = string.Empty;

        string capturedXAppId = string.Empty;

        var mockHandler = new MockHttpMessageHandler(req =>
        {
            capturedUrl = req.RequestUri?.ToString() ?? string.Empty;
            capturedAuthScheme = req.Headers.Authorization?.Scheme ?? string.Empty;
            capturedAuthParam = req.Headers.Authorization?.Parameter ?? string.Empty;
            capturedXAppId = req.Headers.TryGetValues("X-Appid", out var values) ? values.FirstOrDefault() ?? "" : "";
            capturedBody = req.Content?.ReadAsStringAsync().Result ?? string.Empty;

            string json = """
            {
                "from": "en",
                "to": "zh",
                "trans_result": [
                    {
                        "src": "Hello",
                        "dst": "你好"
                    }
                ]
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(mockHandler);
        // 测试传入自带 'Bearer ' 前缀的 API Key，确保自动规范化
        using var translator = new BaiduTranslator(() => "test_appid", () => "Bearer test_apikey", httpClient);

        var result = await translator.TranslateAsync(new TranslationRequest("Hello", "en", "zh"));

        Assert.True(result.IsSuccess);
        Assert.Equal("你好", result.TranslatedText);
        Assert.Equal("Baidu", result.ProviderName);
        Assert.Equal("https://fanyi-api.baidu.com/ait/api/aiTextTranslate", capturedUrl);
        Assert.Equal("Bearer", capturedAuthScheme);
        Assert.Equal("test_apikey", capturedAuthParam);
        Assert.Equal("test_appid", capturedXAppId);
        Assert.Contains("\"appid\":\"test_appid\"", capturedBody);
        Assert.Contains("\"q\":\"Hello\"", capturedBody);
    }

    [Fact]
    public async Task BaiduTranslator_WhenAiEndpointReturns52003_AutoFallsBackToGeneralTranslate()
    {
        int callCount = 0;
        string lastUrl = string.Empty;

        var mockHandler = new MockHttpMessageHandler(req =>
        {
            callCount++;
            lastUrl = req.RequestUri?.ToString() ?? string.Empty;

            if (callCount == 1)
            {
                // 模拟大模型接口返回 52003 未授权
                string errorJson = """
                {
                    "error_code": "52003",
                    "error_msg": "UNAUTHORIZED USER"
                }
                """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(errorJson, Encoding.UTF8, "application/json")
                };
            }

            // 第二次自动回退到通用文本翻译并成功
            string successJson = """
            {
                "from": "en",
                "to": "zh",
                "trans_result": [
                    {
                        "src": "Hello",
                        "dst": "你好(通用)"
                    }
                ]
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(successJson, Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(mockHandler);
        using var translator = new BaiduTranslator(
            () => "test_appid",
            () => "test_secret_key",
            () => "test_api_key",
            httpClient);

        var result = await translator.TranslateAsync(new TranslationRequest("Hello", "en", "zh"));

        Assert.True(result.IsSuccess);
        Assert.Equal("你好(通用)", result.TranslatedText);
        Assert.Equal(2, callCount);
        Assert.Contains("vip/translate", lastUrl);
    }

    [Fact]
    public async Task GoogleTranslator_MockSuccess_DecodesHtmlEntities()
    {
        var mockHandler = new MockHttpMessageHandler(req =>
        {
            string json = """
            {
                "data": {
                    "translations": [
                        {
                            "translatedText": "Tom &amp; Jerry",
                            "detectedSourceLanguage": "en"
                        }
                    ]
                }
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(mockHandler);
        using var translator = new GoogleTranslator(() => "api_key", httpClient);

        var result = await translator.TranslateAsync(new TranslationRequest("Tom and Jerry", "en", "zh"));

        Assert.True(result.IsSuccess);
        Assert.Equal("Tom & Jerry", result.TranslatedText); // HTML entity &amp; decoded to &
    }

    [Fact]
    public async Task OpenAiTranslator_MockSuccess_ParsesChoicesAndUsage()
    {
        var mockHandler = new MockHttpMessageHandler(req =>
        {
            string json = """
            {
                "choices": [
                    {
                        "message": {
                            "role": "assistant",
                            "content": "你好世界"
                        }
                    }
                ],
                "usage": {
                    "prompt_tokens": 15,
                    "completion_tokens": 5,
                    "total_tokens": 20
                }
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(mockHandler);
        using var translator = new OpenAiTranslator(
            () => "https://api.openai.com/v1/chat/completions",
            () => "sk-test",
            () => "gpt-4o-mini",
            httpClient);

        var result = await translator.TranslateAsync(new TranslationRequest("Hello World", "en", "zh"));

        Assert.True(result.IsSuccess);
        Assert.Equal("你好世界", result.TranslatedText);
        Assert.Equal(20, result.ConsumedTokens);
    }

    [Fact]
    public async Task VolcengineTranslator_MockSuccess_ParsesResponseAndHeadersCorrectly()
    {
        string capturedAuth = string.Empty;
        string capturedDate = string.Empty;
        string capturedSha = string.Empty;
        string capturedBody = string.Empty;

        var mockHandler = new MockHttpMessageHandler(req =>
        {
            capturedAuth = req.Headers.GetValues("Authorization").FirstOrDefault() ?? "";
            capturedDate = req.Headers.GetValues("X-Date").FirstOrDefault() ?? "";
            capturedSha = req.Headers.GetValues("X-Content-Sha256").FirstOrDefault() ?? "";
            capturedBody = req.Content?.ReadAsStringAsync().Result ?? "";

            string json = """
            {
                "TranslationList": [
                    {
                        "Translation": "你好世界",
                        "DetectedSourceLanguage": "en"
                    }
                ],
                "ResponseMetadata": {
                    "RequestId": "20261020test",
                    "Action": "TranslateText",
                    "Version": "2020-06-01",
                    "Service": "translate",
                    "Region": "cn-north-1",
                    "Error": null
                }
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(mockHandler);
        using var translator = new VolcengineTranslator(
            () => "test_ak",
            () => "test_sk",
            () => "cn-north-1",
            httpClient);

        var result = await translator.TranslateAsync(new TranslationRequest("Hello world", "auto", "zh"));

        Assert.True(result.IsSuccess);
        Assert.Equal("你好世界", result.TranslatedText);
        Assert.Equal("en", result.SourceLanguage);
        Assert.StartsWith("HMAC-SHA256 Credential=test_ak/", capturedAuth);
        Assert.NotEmpty(capturedDate);
        Assert.NotEmpty(capturedSha);
        Assert.Contains("\"TargetLanguage\":\"zh\"", capturedBody);
        Assert.Contains("\"TextList\":[\"Hello world\"]", capturedBody);
    }

    [Fact]
    public async Task VolcengineTranslator_MockError_ReturnsFailedResult()
    {
        var mockHandler = new MockHttpMessageHandler(req =>
        {
            string json = """
            {
                "ResponseMetadata": {
                    "RequestId": "20261020err",
                    "Action": "TranslateText",
                    "Version": "2020-06-01",
                    "Service": "translate",
                    "Region": "cn-north-1",
                    "Error": {
                        "Code": "-400",
                        "Message": "Invalid target language"
                    }
                }
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(mockHandler);
        using var translator = new VolcengineTranslator(
            () => "test_ak",
            () => "test_sk",
            () => "cn-north-1",
            httpClient);

        var result = await translator.TranslateAsync(new TranslationRequest("Hello", "auto", "bad_lang"));

        Assert.False(result.IsSuccess);
        Assert.Contains("-400", result.ErrorMessage);
        Assert.Contains("Invalid target language", result.ErrorMessage);
    }

    [Fact]
    public async Task AliyunTranslator_MockSuccess_ParsesResponseAndHeadersCorrectly()
    {
        string capturedAuth = string.Empty;
        string capturedMd5 = string.Empty;
        string capturedDate = string.Empty;
        string capturedBody = string.Empty;

        var mockHandler = new MockHttpMessageHandler(req =>
        {
            capturedAuth = req.Headers.GetValues("Authorization").FirstOrDefault() ?? "";
            capturedMd5 = req.Content?.Headers.GetValues("Content-MD5").FirstOrDefault() ?? "";
            capturedDate = req.Headers.GetValues("Date").FirstOrDefault() ?? "";
            capturedBody = req.Content?.ReadAsStringAsync().Result ?? "";

            string json = """
            {
                "Code": 200,
                "Message": "success",
                "RequestId": "aliyun-req-123",
                "Data": {
                    "Translated": "大疆无人机",
                    "WordCount": "10",
                    "DetectedLanguage": "en"
                }
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(mockHandler);
        using var translator = new AliyunTranslator(
            () => "test_ak_id",
            () => "test_ak_secret",
            () => "http://mt.cn-hangzhou.aliyuncs.com/api/translate/web/general",
            httpClient);

        var result = await translator.TranslateAsync(new TranslationRequest("DJI Drone", "auto", "zh"));

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("大疆无人机", result.TranslatedText);
        Assert.Equal("en", result.SourceLanguage);
        Assert.StartsWith("acs test_ak_id:", capturedAuth);
        Assert.NotEmpty(capturedMd5);
        Assert.NotEmpty(capturedDate);
        Assert.Contains("\"SourceText\":\"DJI Drone\"", capturedBody);
        Assert.Contains("\"Scene\":\"general\"", capturedBody);
    }

    [Fact]
    public async Task AliyunTranslator_StringCode_ParsesSuccess()
    {
        var mockHandler = new MockHttpMessageHandler(req =>
        {
            string json = """
            {
                "Code": "200",
                "Data": {
                    "Translated": "测试成功",
                    "WordCount": "5"
                }
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(mockHandler);
        using var translator = new AliyunTranslator(
            () => "test_ak_id",
            () => "test_ak_secret",
            () => "http://mt.cn-hangzhou.aliyuncs.com/api/translate/web/general",
            httpClient);

        var result = await translator.TranslateAsync(new TranslationRequest("Test success", "auto", "zh"));

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("测试成功", result.TranslatedText);
    }

    [Fact]
    public async Task AliyunTranslator_MockError_ReturnsFailedResult()
    {
        var mockHandler = new MockHttpMessageHandler(req =>
        {
            string json = """
            {
                "errorCode": "InvalidAccessKeyId.NotFound",
                "errorMsg": "Specified access key id not found."
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(mockHandler);
        using var translator = new AliyunTranslator(
            () => "invalid_id",
            () => "invalid_secret",
            () => "http://mt.cn-hangzhou.aliyuncs.com/api/translate/web/general",
            httpClient);

        var result = await translator.TranslateAsync(new TranslationRequest("Test", "auto", "zh"));

        Assert.False(result.IsSuccess);
        Assert.Contains("InvalidAccessKeyId.NotFound", result.ErrorMessage);
    }

    [Fact]
    public async Task TranslationService_Interception_WhenVolcengineOrAliyunLimitExceeded()
    {
        var settingsService = new MockSettingsService();
        var s = settingsService.Current;
        s.TranslationUsageMonth = DateTime.Now.ToString("yyyy-MM");

        // 1. 测试火山引擎限额拦截
        s.TranslateProvider = "Volcengine";
        s.VolcengineAccessKey = "ak";
        s.VolcengineSecretKey = "sk";
        s.VolcengineMonthlyLimit = 1000;
        s.VolcengineUsedCount = 1000;
        settingsService.Save(s);

        using var service = new TranslationService(settingsService);
        var volcResult = await service.TranslateAsync("Hello");
        Assert.False(volcResult.IsSuccess);
        Assert.Contains("火山引擎翻译字符额度上限", volcResult.ErrorMessage);

        // 2. 测试阿里云限额拦截
        s.TranslateProvider = "Aliyun";
        s.AliyunAccessKeyId = "ak_id";
        s.AliyunAccessKeySecret = "ak_sec";
        s.AliyunMonthlyLimit = 2000;
        s.AliyunUsedCount = 2000;
        settingsService.Save(s);

        var aliyunResult = await service.TranslateAsync("Hello");
        Assert.False(aliyunResult.IsSuccess);
        Assert.Contains("阿里云机器翻译字符额度上限", aliyunResult.ErrorMessage);

        // 3. 额度信息获取验证
        var volcQuota = service.GetQuotaInfo("Volcengine");
        Assert.Equal("Volcengine", volcQuota.ProviderName);
        Assert.Equal(100.0, volcQuota.Percentage);

        var aliyunQuota = service.GetQuotaInfo("Aliyun");
        Assert.Equal("Aliyun", aliyunQuota.ProviderName);
        Assert.Equal(100.0, aliyunQuota.Percentage);
    }

    private sealed class MockClipboardService : IClipboardService
    {
        public string? Text { get; set; }
        public Task<bool> SetTextAsync(string text)
        {
            Text = text;
            return Task.FromResult(true);
        }
        public Task<string?> GetTextAsync() => Task.FromResult(Text);
        public Task<bool> SetImageAsync(OcrImage image) => Task.FromResult(true);
    }

    [Fact]
    public void TranslationProviders_ReturnsCorrectChineseNames()
    {
        Assert.Equal(5, TranslationProviders.All.Count);
        Assert.Equal("百度翻译", TranslationProviders.GetDisplayName("Baidu"));
        Assert.Equal("谷歌翻译", TranslationProviders.GetDisplayName("Google"));
        Assert.Equal("OpenAI", TranslationProviders.GetDisplayName("OpenAI"));
        Assert.Equal("火山引擎", TranslationProviders.GetDisplayName("Volcengine"));
        Assert.Equal("阿里云翻译", TranslationProviders.GetDisplayName("Aliyun"));

        var baidu = TranslationProviders.All.First(p => p.Id == "Baidu");
        Assert.Equal("百度翻译", baidu.ToString());
    }

    [Fact]
    public void TranslateViewModel_SwapLanguages_AutoToZh_DirectlyBecomesZhToEn()
    {
        var settingsService = new MockSettingsService();
        var s = settingsService.Current;
        s.TranslateSourceLang = "auto";
        s.TranslateTargetLang = "zh";
        settingsService.Save(s);

        using var translationService = new TranslationService(settingsService);
        var clipboardService = new MockClipboardService();
        var vm = new TranslateViewModel(translationService, settingsService, clipboardService);

        vm.SourceText = "Hello";
        vm.TranslatedText = "你好";

        Assert.Equal("auto", vm.SelectedSourceLang.Code);
        Assert.Equal("zh", vm.SelectedTargetLang.Code);

        // 第一次切换：auto-zh 直接转换为 zh-en（无需先转为 en-zh 再转）
        vm.SwapLanguages();

        Assert.Equal("zh", vm.SelectedSourceLang.Code);
        Assert.Equal("en", vm.SelectedTargetLang.Code);
        Assert.Equal("你好", vm.SourceText);
        Assert.Equal("Hello", vm.TranslatedText);

        // 再次切换：zh-en 翻转为 en-zh
        vm.SwapLanguages();

        Assert.Equal("en", vm.SelectedSourceLang.Code);
        Assert.Equal("zh", vm.SelectedTargetLang.Code);
        Assert.Equal("Hello", vm.SourceText);
        Assert.Equal("你好", vm.TranslatedText);
    }

    [Fact]
    public void TranslateViewModel_SwapLanguages_AutoToEn_DirectlyBecomesEnToZh()
    {
        var settingsService = new MockSettingsService();
        var s = settingsService.Current;
        s.TranslateSourceLang = "auto";
        s.TranslateTargetLang = "en";
        settingsService.Save(s);

        using var translationService = new TranslationService(settingsService);
        var clipboardService = new MockClipboardService();
        var vm = new TranslateViewModel(translationService, settingsService, clipboardService);

        Assert.Equal("auto", vm.SelectedSourceLang.Code);
        Assert.Equal("en", vm.SelectedTargetLang.Code);

        vm.SwapLanguages();

        Assert.Equal("en", vm.SelectedSourceLang.Code);
        Assert.Equal("zh", vm.SelectedTargetLang.Code);
    }

    [Fact]
    public void TranslateViewModel_ProviderSelection_DisplaysChineseNameAndSyncsId()
    {
        var settingsService = new MockSettingsService();
        var s = settingsService.Current;
        s.TranslateProvider = "Baidu";
        settingsService.Save(s);

        using var translationService = new TranslationService(settingsService);
        var clipboardService = new MockClipboardService();
        var vm = new TranslateViewModel(translationService, settingsService, clipboardService);

        Assert.Equal("Baidu", vm.SelectedProvider);
        Assert.Equal("百度翻译", vm.SelectedProviderItem.DisplayName);

        // 切换为火山引擎
        var volc = vm.AvailableProviders.First(p => p.Id == "Volcengine");
        vm.SelectedProviderItem = volc;
        Assert.Equal("Volcengine", vm.SelectedProvider);
        Assert.Equal("火山引擎", vm.SelectedProviderItem.DisplayName);

        // 通过字符串 ID 切换
        vm.SelectedProvider = "Aliyun";
        Assert.Equal("Aliyun", vm.SelectedProvider);
        Assert.Equal("阿里云翻译", vm.SelectedProviderItem.DisplayName);
    }
}
