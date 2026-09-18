using System.Net;
using System.Text;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.Core.Translation;
using ShawMuOCR.Core.Translation.Providers;
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
}
