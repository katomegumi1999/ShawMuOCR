using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ShawMuOCR.Core.Ai;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.Core.Translation;
using ShawMuOCR.OCR;
using SkiaSharp;
using Xunit;

namespace ShawMuOCR.Tests;

/// <summary>
/// 统一 AI 接口池：数据模型、迁移、加密索引、额度记账、客户端协议与 OCR 引擎适配。
/// 不发起真实网络请求：用本地 TcpListener 假服务器验证请求形状。
/// </summary>
public class AiApiOcrTests
{
    // ==========================================================
    // 节点池数据模型
    // ==========================================================

    [Fact]
    public void Settings_FindReturnsNodeById()
    {
        var ai = new AiSettings();
        ai.Profiles.Add(new AiApiProfile { Id = "a", Name = "A", Model = "m1" });
        ai.Profiles.Add(new AiApiProfile { Id = "b", Name = "B", Model = "m2" });

        Assert.Equal("A", ai.Find("a")!.Name);
        Assert.Equal("m2", ai.Find("b")!.Model);
        Assert.Null(ai.Find("missing"));
        Assert.Null(ai.Find(null));
        Assert.Null(ai.Find("  "));
    }

    [Fact]
    public void Settings_Clone_DeepCopiesProfiles()
    {
        var original = new AppSettings();
        original.Ai.Profiles.Add(new AiApiProfile { Id = "a", ApiKey = "k1", Name = "A" });
        original.Ocr.AiProfileId = "a";
        original.Ocr.AiVisionPrompt = "自定义提示词";

        var clone = original.Clone();

        Assert.Single(clone.Ai.Profiles);
        Assert.Equal("k1", clone.Ai.Profiles[0].ApiKey);
        Assert.Equal("a", clone.Ocr.AiProfileId);
        Assert.Equal("自定义提示词", clone.Ocr.AiVisionPrompt);

        // 深拷贝：改副本不能污染原对象（否则设置界面的"取消修改"会失效）
        clone.Ai.Profiles[0].ApiKey = "changed";
        clone.Ai.Profiles[0].Name = "changed";
        Assert.Equal("k1", original.Ai.Profiles[0].ApiKey);
        Assert.Equal("A", original.Ai.Profiles[0].Name);
    }

    [Fact]
    public void Settings_NewProfile_GetsStableGuidId()
    {
        var p = new AiApiProfile();
        Assert.False(string.IsNullOrWhiteSpace(p.Id));
        Assert.True(Guid.TryParseExact(p.Id, "N", out _));
        Assert.NotEqual(new AiApiProfile().Id, p.Id);
    }

    // ==========================================================
    // 加密索引：必须按 GUID，不能按数组下标
    // ==========================================================

    [Fact]
    public void ProfileCiphertextField_UsesGuidNotIndex()
    {
        string field = JsonSettingsService.ProfileKeyField("abc123");

        Assert.Equal("AiProfile:abc123:ApiKey", field);
        Assert.Contains("abc123", field);
        // 若退化成下标形式（如 AiProfiles[0].ApiKey），删除首个节点会让密文整体错位
        Assert.DoesNotContain("[0]", field);
    }

    // ==========================================================
    // 旧配置迁移
    // ==========================================================

    [Fact]
    public void Load_MigratesLegacySingleConfigsIntoPool()
    {
        string dir = Path.Combine(Path.GetTempPath(), "shawmu_ai_migrate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "settings.json");

        try
        {
            const string legacyJson = """
            {
              "Ocr": {
                "ExternalModelType": "AiApi",
                "AiApi": {
                  "BaseUrl": "https://vision.test/v1",
                  "ApiKey": "sk-legacy-ocr",
                  "Model": "qwen-vl-max",
                  "Prompt": "旧提示词"
                }
              },
              "Translation": {
                "Provider": "OpenAI",
                "OpenAiApiEndpoint": "https://api.openai.com/v1/chat/completions",
                "OpenAiApiKey": "sk-legacy-tr",
                "OpenAiModel": "gpt-4o-mini",
                "OpenAiMonthlyLimit": 123456,
                "OpenAiUsedCount": 789
              }
            }
            """;
            File.WriteAllText(path, legacyJson);

            var service = new JsonSettingsService(path);
            var settings = service.Current;

            Assert.Equal(2, settings.Ai.Profiles.Count);
            Assert.True(settings.Ai.Migrated);

            var ocrNode = settings.Ai.Find("legacy-aiocr");
            Assert.NotNull(ocrNode);
            Assert.Equal("sk-legacy-ocr", ocrNode!.ApiKey);
            Assert.Equal("https://vision.test/v1", ocrNode.BaseUrl);
            Assert.True(ocrNode.SupportsVision);
            Assert.Equal("legacy-aiocr", settings.Ocr.AiProfileId);
            Assert.Equal("旧提示词", settings.Ocr.AiVisionPrompt);
            Assert.Null(settings.Ocr.AiApi);

            var trNode = settings.Ai.Find("legacy-openai");
            Assert.NotNull(trNode);
            Assert.Equal("sk-legacy-tr", trNode!.ApiKey);
            // 历史地址带 /chat/completions，迁移后统一只存到 /v1
            Assert.Equal("https://api.openai.com/v1", trNode.BaseUrl);
            Assert.Equal(123456, trNode.MonthlyTokenLimit);
            Assert.Equal(789, trNode.UsedTokens);

            Assert.Equal("ai:legacy-openai", settings.TranslateProvider);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Load_MigrationIsIdempotent_AndDoesNotResurrectDeletedNodes()
    {
        string dir = Path.Combine(Path.GetTempPath(), "shawmu_ai_idem_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "settings.json");

        try
        {
            var service = new JsonSettingsService(path);
            var settings = service.Current;
            settings.Ai.Migrated = true;
            settings.Ai.Profiles.Add(new AiApiProfile { Id = "only", Name = "唯一节点", ApiKey = "k" });
            // 旧字段仍然保留着明文，模拟用户手动改过配置
            settings.Translation.OpenAiApiKey = "sk-stale";
            service.Save(settings);

            // 用户删光所有节点
            var second = new JsonSettingsService(path);
            var current = second.Current;
            current.Ai.Profiles.Clear();
            second.Save(current);

            // 再次加载：迁移标记已置位，不得凭空造出节点
            var third = new JsonSettingsService(path);
            Assert.Empty(third.Current.Ai.Profiles);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ==========================================================
    // 额度：公共接口统一记账
    // ==========================================================

    [Fact]
    public void Quota_CommitAccumulatesPerNode()
    {
        MakeSettings(out var service, limit: 1000);
        var quota = new AiQuotaService(service);

        Assert.True(quota.TryCheck("node-a", out _));
        quota.Commit("node-a", 400);
        quota.Commit("node-a", 100);

        var snapshot = quota.GetSnapshot("node-a");
        Assert.Equal(500, snapshot.UsedTokens);
        Assert.Equal(1000, snapshot.MonthlyLimit);
        Assert.Equal(50, snapshot.Percent, 3);
    }

    [Fact]
    public void Quota_BlocksWhenLimitReached()
    {
        var settings = MakeSettings(out var service, limit: 100);
        settings.Ai.Find("node-a")!.UsedTokens = 100;
        var quota = new AiQuotaService(service);

        Assert.False(quota.TryCheck("node-a", out string? deny));
        Assert.Contains("额度", deny);
    }

    [Fact]
    public void Quota_ZeroLimitMeansUnlimited()
    {
        MakeSettings(out var service, limit: 0);
        var quota = new AiQuotaService(service);

        quota.Commit("node-a", 999999);

        Assert.True(quota.TryCheck("node-a", out _));
        Assert.Equal(0, quota.GetSnapshot("node-a").Percent);
    }

    [Fact]
    public void Quota_ResetsOnMonthRollover()
    {
        var settings = MakeSettings(out var service, limit: 1000);
        settings.Ai.Find("node-a")!.UsedTokens = 900;
        settings.Ai.UsageMonth = "2000-01";

        var quota = new AiQuotaService(service);
        var snapshot = quota.GetSnapshot("node-a");

        Assert.Equal(0, snapshot.UsedTokens);
        Assert.Equal(DateTime.Now.ToString("yyyy-MM"), service.Current.Ai.UsageMonth);
    }

    [Fact]
    public void Quota_MissingNodeIsRejectedWithActionableMessage()
    {
        MakeSettings(out var service, limit: 1000);
        var quota = new AiQuotaService(service);

        Assert.False(quota.TryCheck("ghost", out string? deny));
        Assert.Contains("已被删除", deny);
    }

    // ==========================================================
    // 工厂与引擎
    // ==========================================================

    [Fact]
    public void Factory_BuildsAiEngineFromSelectedNode()
    {
        var settings = MakeSettings(out _, limit: 1000);
        settings.Ocr.ExternalModelType = "AiApi";
        settings.Ocr.AiProfileId = "node-a";

        var factory = new OcrEngineFactory();
        using var engine = factory.Create(settings);

        var ai = Assert.IsType<AiApiOcrEngine>(engine);
        Assert.False(ai.UsesLocalCpu);
        Assert.Contains("节点 A", ai.ModelName);
    }

    [Fact]
    public void Factory_UnknownNodeStillReturnsEngine_SoUiDoesNotCrash()
    {
        var settings = MakeSettings(out _, limit: 1000);
        settings.Ocr.ExternalModelType = "AiApi";
        settings.Ocr.AiProfileId = "deleted-node";

        var factory = new OcrEngineFactory();
        using var engine = factory.Create(settings);

        // 不抛异常：让识别阶段给出可操作提示，而不是打断启动
        Assert.IsType<AiApiOcrEngine>(engine);
    }

    [Fact]
    public async Task Engine_WithoutApiKey_FailsWithoutTouchingNetwork()
    {
        var settings = MakeSettings(out _, limit: 1000);
        settings.Ai.Find("node-a")!.ApiKey = string.Empty;
        settings.Ocr.ExternalModelType = "AiApi";
        settings.Ocr.AiProfileId = "node-a";

        var factory = new OcrEngineFactory();
        using var engine = factory.Create(settings);

        using var bitmap = new SKBitmap(8, 8);
        using var image = new OcrImage(bitmap);

        var result = await engine.RecognizeAsync(image);

        Assert.False(result.IsSuccess);
        Assert.Contains("API Key", result.ErrorMessage);
    }

    [Fact]
    public async Task Engine_StopsWhenQuotaExceeded()
    {
        var settings = MakeSettings(out var service, limit: 100);
        settings.Ai.Find("node-a")!.UsedTokens = 100;
        settings.Ocr.ExternalModelType = "AiApi";
        settings.Ocr.AiProfileId = "node-a";

        var factory = new OcrEngineFactory(aiQuota: new AiQuotaService(service));
        using var engine = factory.Create(settings);

        using var bitmap = new SKBitmap(8, 8);
        using var image = new OcrImage(bitmap);

        var result = await engine.RecognizeAsync(image);

        Assert.False(result.IsSuccess);
        Assert.Contains("额度", result.ErrorMessage);
    }

    [Fact]
    public async Task Engine_SendsVisionRequestAndCountsTokens()
    {
        const string replyJson = """
        {"choices":[{"finish_reason":"stop","message":{"content":"识别结果：小沐 OCR"}}],"usage":{"total_tokens":321}}
        """;

        using var fake = new FakeHttpServer(replyJson);

        var settings = MakeSettings(out var service, limit: 100000);
        var node = settings.Ai.Find("node-a")!;
        node.BaseUrl = fake.BaseUrl;
        node.ApiKey = "sk-fake-key";
        node.Model = "qwen-vl-max";
        settings.Ocr.ExternalModelType = "AiApi";
        settings.Ocr.AiProfileId = "node-a";
        settings.Ocr.AiVisionPrompt = "只输出文字";

        var quota = new AiQuotaService(service);
        var factory = new OcrEngineFactory(aiQuota: quota);

        using var engine = factory.Create(settings);
        using var bitmap = new SKBitmap(64, 32);
        using var image = new OcrImage(bitmap);

        var result = await engine.RecognizeAsync(image);
        string request = await fake.RequestTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("识别结果：小沐 OCR", result.Text);
        // AI 引擎不产生文字框，伪造坐标会污染对外 HTTP 接口输出
        Assert.Empty(result.Blocks);

        Assert.StartsWith("POST /v1/chat/completions HTTP/1.1", request, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Bearer sk-fake-key", request);
        Assert.Contains("\"type\":\"image_url\"", request);
        Assert.Contains("data:image/jpeg;base64,", request);
        Assert.Contains("只输出文字", request);

        // 用量必须记到该节点上，否则统一额度形同虚设
        Assert.Equal(321, service.Current.Ai.Find("node-a")!.UsedTokens);
    }

    [Fact]
    public async Task Client_UnreachableEndpoint_ReportsFriendlyHttpError()
    {
        // 占一个端口再释放，保证这个端口上没有监听者（连接会被立即拒绝）
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var client = new OpenAiCompatibleClient(
            "node-dead",
            $"http://127.0.0.1:{port}/v1",
            "sk-whatever",
            "gpt-4o-mini",
            timeoutSeconds: 10,
            maxConcurrency: 1);

        var response = await client.CompleteAsync(new LlmRequest { Prompt = "hi" });

        Assert.False(response.IsSuccess);
        // 断网/地址写错不能被归为"异常"或"超时"，否则用户不知道该改地址还是该等
        Assert.Equal(LlmErrorKind.Http, response.ErrorKind);
        Assert.Contains("无法连接", response.ErrorMessage);
    }

    [Fact]
    public async Task Client_Unauthorized_IsNotRetried()
    {
        const string replyJson = """{"error":{"message":"Invalid API key provided"}}""";
        using var fake = new FakeHttpServer(replyJson, statusCode: 401);

        var client = new OpenAiCompatibleClient("node-401", fake.BaseUrl, "sk-bad", "gpt-4o-mini");
        var response = await client.CompleteAsync(new LlmRequest { Prompt = "hi" });

        Assert.False(response.IsSuccess);
        Assert.Equal(LlmErrorKind.Unauthorized, response.ErrorKind);
        // 401 重试只会把密钥错误拖成十几秒等待，必须是立即失败
        Assert.Contains("Invalid API key", response.ErrorMessage);
    }

    // ==========================================================
    // 翻译走节点池
    // ==========================================================

    [Fact]
    public async Task Translate_WithAiNodeProvider_UsesThatNodeAndRecordsUsage()
    {
        const string replyJson = """
        {"choices":[{"message":{"content":"你好，世界"}}],"usage":{"total_tokens":88}}
        """;
        using var fake = new FakeHttpServer(replyJson);

        var settings = MakeSettings(out var service, limit: 100000);
        var node = settings.Ai.Find("node-a")!;
        node.BaseUrl = fake.BaseUrl;
        node.ApiKey = "sk-fake-key";
        node.Model = "gpt-4o-mini";
        settings.TranslateProvider = AiSettings.TranslateProviderPrefix + "node-a";
        service.Save(settings);

        var quota = new AiQuotaService(service);
        using var translator = new TranslationService(service, aiQuota: quota);

        var result = await translator.TranslateAsync("Hello, world");
        string request = await fake.RequestTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("你好，世界", result.TranslatedText);
        Assert.Contains("Bearer sk-fake-key", request);
        // 翻译请求不能带图片，否则纯文本任务也会按视觉模型的价钱计费
        Assert.DoesNotContain("image_url", request);
        // 额度记到节点上，与 OCR 共用同一本账
        Assert.Equal(88, service.Current.Ai.Find("node-a")!.UsedTokens);
    }

    [Fact]
    public void GetQuotaInfo_AiNode_ReportsNodeUsageInsteadOfBaidu()
    {
        var settings = MakeSettings(out var service, limit: 2000);
        settings.TranslateProvider = AiSettings.TranslateProviderPrefix + "node-a";
        settings.Ai.Find("node-a")!.UsedTokens = 500;
        // 百度那边有独立的旧计数，不该被错认成 AI 节点的用量
        settings.BaiduUsedCount = 7777;
        settings.BaiduMonthlyLimit = 9999;
        service.Save(settings);

        using var translator = new TranslationService(service, aiQuota: new AiQuotaService(service));
        var info = translator.GetQuotaInfo(AiSettings.TranslateProviderPrefix + "node-a");

        Assert.Equal(500, info.UsedThisMonth);
        Assert.Equal(2000, info.MonthlyLimit);
        Assert.Equal("Token", info.UnitName);
        Assert.Equal(25, info.Percentage, 3);
    }

    // ==========================================================
    // 响应解析与地址拼接
    // ==========================================================

    [Fact]
    public void ParseContent_ExtractsPlainAndStripsCodeFence()
    {
        Assert.Equal("第一行\n第二行", OpenAiCompatibleClient.ParseContent(
            """{"choices":[{"message":{"content":"第一行\n第二行"}}]}"""));

        Assert.Equal("小沐 OCR 测试", OpenAiCompatibleClient.ParseContent(
            """{"choices":[{"message":{"content":"```text\n小沐 OCR 测试\n```"}}]}"""));
    }

    [Fact]
    public void ParseContent_JoinsSegmentedContentArray()
    {
        Assert.Equal("表头内容", OpenAiCompatibleClient.ParseContent(
            """{"choices":[{"message":{"content":[{"type":"text","text":"表头"},{"type":"text","text":"内容"}]}}]}"""));
    }

    [Fact]
    public void ParseContent_ReturnsEmptyForErrorsAndBlanks()
    {
        Assert.Equal(string.Empty, OpenAiCompatibleClient.ParseContent("""{"error":{"message":"bad key"}}"""));
        Assert.Equal(string.Empty, OpenAiCompatibleClient.ParseContent("{}"));
        Assert.Equal(string.Empty, OpenAiCompatibleClient.ParseContent(string.Empty));
    }

    [Fact]
    public void BuildEndpoint_AppendsChatCompletionsOnce()
    {
        Assert.Equal("https://a.test/v1/chat/completions", OpenAiCompatibleClient.BuildEndpoint("https://a.test/v1"));
        Assert.Equal("https://a.test/v1/chat/completions", OpenAiCompatibleClient.BuildEndpoint("https://a.test/v1/"));
        Assert.Equal("https://a.test/v1/chat/completions", OpenAiCompatibleClient.BuildEndpoint("https://a.test/v1/chat/completions"));
    }

    // ==========================================================
    // 翻译接入节点池
    // ==========================================================

    [Fact]
    public async Task Translation_UnknownAiNodeFailsWithClearMessage()
    {
        MakeSettings(out var service, limit: 1000);
        using var translation = new TranslationService(service);

        var result = await translation.TranslateWithProviderAsync("ai:ghost", "hello", "en", "zh");

        Assert.False(result.IsSuccess);
        Assert.Contains("已被删除", result.ErrorMessage);
    }

    [Fact]
    public void DefaultPrompt_IsNotEmptyAndWithinLimit()
    {
        Assert.False(string.IsNullOrWhiteSpace(AiOcrPrompt.Default));
        Assert.True(AiOcrPrompt.Default.Length <= AiOcrPrompt.MaxLength);

        Assert.False(string.IsNullOrWhiteSpace(AiTranslatePrompt.Default));
        Assert.True(AiTranslatePrompt.Default.Length <= AiTranslatePrompt.MaxLength);
        Assert.Contains("{TargetLanguage}", AiTranslatePrompt.Default);
    }

    [Fact]
    public async Task Translate_WithCustomPromptTemplate_ReplacesMacros()
    {
        const string replyJson = """
        {"choices":[{"message":{"content":"Custom translated output"}}],"usage":{"total_tokens":50}}
        """;
        using var fake = new FakeHttpServer(replyJson);

        var settings = MakeSettings(out var service, limit: 100000);
        var node = settings.Ai.Find("node-a")!;
        node.BaseUrl = fake.BaseUrl;
        node.ApiKey = "sk-custom-prompt";
        node.Model = "gpt-4o-mini";
        settings.TranslateProvider = AiSettings.TranslateProviderPrefix + "node-a";
        settings.Translation.AiTranslatePromptText = "Please translate accurately to {TargetLanguage} from {SourceLanguage}.";
        service.Save(settings);

        var quota = new AiQuotaService(service);
        using var translator = new TranslationService(service, aiQuota: quota);

        var result = await translator.TranslateAsync("Hello", sourceLanguage: "en", targetLanguage: "zh");
        string request = await fake.RequestTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.IsSuccess);
        Assert.Equal("Custom translated output", result.TranslatedText);
        Assert.Contains("Please translate accurately to", request);
    }

    [Fact]
    public void Json_RoundTripsPoolAndSelection()
    {
        var settings = new AppSettings();
        settings.Ai.Migrated = true;
        settings.Ai.Profiles.Add(new AiApiProfile
        {
            Id = "n1",
            Name = "通义",
            BaseUrl = "https://dash.test/v1",
            ApiKey = "sk-rt",
            Model = "qwen-vl-max",
            SupportsVision = true,
            MonthlyTokenLimit = 5000,
            UsedTokens = 12
        });
        settings.Ocr.ExternalModelType = "AiApi";
        settings.Ocr.AiProfileId = "n1";
        settings.Ocr.AiVisionPrompt = "只输出表格";

        string json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
        var restored = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);

        Assert.NotNull(restored);
        var node = restored!.Ai.Find("n1");
        Assert.NotNull(node);
        Assert.Equal("sk-rt", node!.ApiKey);
        Assert.Equal(5000, node.MonthlyTokenLimit);
        Assert.True(node.SupportsVision);
        Assert.Equal("n1", restored.Ocr.AiProfileId);
        Assert.Equal("只输出表格", restored.Ocr.AiVisionPrompt);
    }

    // ==========================================================
    // 辅助
    // ==========================================================

    private static AppSettings MakeSettings(out ISettingsService service, int limit)
    {
        var settings = new AppSettings();
        settings.Ai.Migrated = true;
        settings.Ai.UsageMonth = DateTime.Now.ToString("yyyy-MM");
        settings.Ai.Profiles.Add(new AiApiProfile
        {
            Id = "node-a",
            Name = "节点 A",
            BaseUrl = "https://api.test/v1",
            ApiKey = "sk-a",
            Model = "qwen-vl-max",
            SupportsVision = true,
            MonthlyTokenLimit = limit
        });

        service = new InMemorySettingsService(settings);
        return ((InMemorySettingsService)service).Current;
    }

    private sealed class InMemorySettingsService : ISettingsService
    {
        public InMemorySettingsService(AppSettings settings) => Current = settings;

        public AppSettings Current { get; private set; }
        public string SettingsFilePath => "ai_memory_settings.json";
        public event Action<AppSettings>? SettingsChanged;

        public AppSettings Load() => Current;

        public void Save(AppSettings settings)
        {
            Current = settings;
            SettingsChanged?.Invoke(Current);
        }
    }

    /// <summary>
    /// 最小化本地 HTTP 服务：接受一次请求、回传原始请求文本，用固定 JSON 响应。
    /// 用 TcpListener 而非 HttpListener，避免 Windows 上非管理员进程注册 URL 前缀的访问控制问题。
    /// </summary>
    private sealed class FakeHttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly string _replyJson;
        private readonly int _statusCode;

        public string BaseUrl { get; }
        public Task<string> RequestTask { get; }

        public FakeHttpServer(string replyJson, int statusCode = 200)
        {
            _replyJson = replyJson;
            _statusCode = statusCode;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}/v1";
            RequestTask = Task.Run(AcceptOnceAsync);
        }

        private async Task<string> AcceptOnceAsync()
        {
            using var client = await _listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();

            var buffer = new byte[8192];
            var received = new List<byte>();
            int headerEnd = -1;

            while (headerEnd < 0)
            {
                int read = await stream.ReadAsync(buffer);
                if (read <= 0) break;
                received.AddRange(buffer.Take(read));
                headerEnd = IndexOfHeaderTerminator(received);
            }

            if (headerEnd < 0) return string.Empty;

            string head = System.Text.Encoding.UTF8.GetString(received.Take(headerEnd).ToArray());
            int contentLength = ParseContentLength(head);
            int bodyStart = headerEnd + 4;

            while (received.Count - bodyStart < contentLength)
            {
                int read = await stream.ReadAsync(buffer);
                if (read <= 0) break;
                received.AddRange(buffer.Take(read));
            }

            string requestText = System.Text.Encoding.UTF8.GetString(received.ToArray());

            byte[] payload = System.Text.Encoding.UTF8.GetBytes(_replyJson);
            string header = $"HTTP/1.1 {_statusCode} {(_statusCode == 200 ? "OK" : "Error")}\r\n" +
                            "Content-Type: application/json\r\n" +
                            $"Content-Length: {payload.Length}\r\n" +
                            "Connection: close\r\n\r\n";
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(header));
            await stream.WriteAsync(payload);
            await stream.FlushAsync();

            return requestText;
        }

        private static int IndexOfHeaderTerminator(List<byte> data)
        {
            for (int i = 0; i + 3 < data.Count; i++)
            {
                if (data[i] == (byte)'\r' && data[i + 1] == (byte)'\n' &&
                    data[i + 2] == (byte)'\r' && data[i + 3] == (byte)'\n')
                {
                    return i;
                }
            }
            return -1;
        }

        private static int ParseContentLength(string headers)
        {
            foreach (var line in headers.Split("\r\n"))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    return int.TryParse(line["Content-Length:".Length..].Trim(), out int value) ? value : 0;
                }
            }
            return 0;
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
        }
    }
}
