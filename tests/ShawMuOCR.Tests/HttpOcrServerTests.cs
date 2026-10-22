using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.Services.Http;
using SkiaSharp;
using Xunit;

namespace ShawMuOCR.Tests;

public class HttpOcrServerTests
{
    private sealed class FakeEngine : IOcrEngine
    {
        public string ModelName => "FakeModel";
        private readonly string _returnedText;
        private readonly IReadOnlyList<OcrTextBlock> _blocks;

        public FakeEngine(string returnedText = "HTTP识别测试文本\r\n第二行内容", IReadOnlyList<OcrTextBlock>? blocks = null)
        {
            _returnedText = returnedText;
            _blocks = blocks ?? [
                new OcrTextBlock
                {
                    Text = "HTTP识别测试文本",
                    Confidence = 0.99f,
                    BoxPoints = [new SKPointI(10, 10), new SKPointI(100, 10), new SKPointI(100, 30), new SKPointI(10, 30)]
                }
            ];
        }

        public Task<OcrResult> RecognizeAsync(OcrImage image, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new OcrResult
            {
                Text = _returnedText,
                Blocks = _blocks,
                Elapsed = TimeSpan.FromMilliseconds(15),
                IsSuccess = true
            });
        }

        public Task WarmupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class FakeEngineFactory : IOcrEngineFactory
    {
        public IOcrEngine Create(string modelName) => new FakeEngine();
        public IOcrEngine Create(string modelName, bool enableAngle, int maxSideLen) => new FakeEngine();
        public IOcrEngine Create(AppSettings settings) => new FakeEngine();
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Current { get; set; } = new();
        public string SettingsFilePath => "test_settings.json";
        public event Action<AppSettings>? SettingsChanged;
        public AppSettings Load() => Current;
        public void Save(AppSettings settings)
        {
            Current = settings;
            SettingsChanged?.Invoke(Current);
        }
    }

    private static byte[] CreateTestPng()
    {
        using var bitmap = new SKBitmap(40, 40, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Red);
        using var img = SKImage.FromBitmap(bitmap);
        using var data = img.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    [Fact]
    public void HttpMultipartParser_ParsesFileAndFields_Correctly()
    {
        string boundary = "----TestBoundary123";
        string contentType = $"multipart/form-data; boundary={boundary}";

        string payload =
$"--{boundary}\r\n" +
"Content-Disposition: form-data; name=\"splitColumns\"\r\n\r\n" +
"true\r\n" +
$"--{boundary}\r\n" +
"Content-Disposition: form-data; name=\"file\"; filename=\"test.png\"\r\n" +
"Content-Type: image/png\r\n\r\n" +
"HELLO_IMAGE_BYTES\r\n" +
$"--{boundary}--\r\n";

        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
        using var ms = new MemoryStream(payloadBytes);

        var result = HttpMultipartParser.Parse(ms, contentType);
        Assert.NotNull(result);
        Assert.True(result.FormFields.ContainsKey("splitColumns"));
        Assert.Equal("true", result.FormFields["splitColumns"]);
        Assert.NotNull(result.FileBytes);
        Assert.Equal("HELLO_IMAGE_BYTES", Encoding.UTF8.GetString(result.FileBytes));
        Assert.Equal("test.png", result.FileName);
    }

    [Fact]
    public async Task Server_HealthAndOcrEndpoints_WorkProperly()
    {
        int port = 18090;
        var factory = new FakeEngineFactory();
        var settingsService = new FakeSettingsService();
        using var server = new HttpOcrServer(factory, settingsService);

        await server.StartAsync(port, allowRemote: false, maxCores: 2, cpuLimitPercent: 100);
        Assert.True(server.IsRunning);

        using var client = new HttpClient();
        string baseUri = $"http://127.0.0.1:{port}";

        try
        {
            // 1. GET /health
            var healthResp = await client.GetAsync($"{baseUri}/health");
            Assert.Equal(HttpStatusCode.OK, healthResp.StatusCode);
            string healthJson = await healthResp.Content.ReadAsStringAsync();
            Assert.Contains("\"status\":\"ok\"", healthJson);

            // 2. OPTIONS /ocr (CORS Preflight)
            using var optReq = new HttpRequestMessage(HttpMethod.Options, $"{baseUri}/ocr");
            var optResp = await client.SendAsync(optReq);
            Assert.Equal(HttpStatusCode.NoContent, optResp.StatusCode);
            Assert.True(optResp.Headers.Contains("Access-Control-Allow-Origin"));

            // 3. POST /ocr (Multipart Form-Data)
            byte[] pngBytes = CreateTestPng();
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(pngBytes), "file", "sample.png");
            var ocrResp = await client.PostAsync($"{baseUri}/ocr", form);
            Assert.Equal(HttpStatusCode.OK, ocrResp.StatusCode);
            string ocrJson = await ocrResp.Content.ReadAsStringAsync();
            Assert.Contains("\"success\":true", ocrJson);
            Assert.Contains("HTTP识别测试文本", ocrJson);

            // 4. POST /ocr (JSON Base64)
            string b64 = Convert.ToBase64String(pngBytes);
            string reqJson = $"{{\"image\":\"data:image/png;base64,{b64}\"}}";
            var jsonResp = await client.PostAsync($"{baseUri}/ocr", new StringContent(reqJson, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, jsonResp.StatusCode);
            string jsonBody = await jsonResp.Content.ReadAsStringAsync();
            Assert.Contains("\"success\":true", jsonBody);

            // 5. POST /ocr (Raw binary body)
            using var binContent = new ByteArrayContent(pngBytes);
            binContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            var binResp = await client.PostAsync($"{baseUri}/ocr", binContent);
            Assert.Equal(HttpStatusCode.OK, binResp.StatusCode);
            string binJson = await binResp.Content.ReadAsStringAsync();
            Assert.Contains("\"success\":true", binJson);

            // 6. POST /ocr?format=text (Plain text output)
            using var plainForm = new MultipartFormDataContent();
            plainForm.Add(new ByteArrayContent(pngBytes), "file", "sample.png");
            var textResp = await client.PostAsync($"{baseUri}/ocr?format=text", plainForm);
            Assert.Equal(HttpStatusCode.OK, textResp.StatusCode);
            Assert.Equal("text/plain; charset=utf-8", textResp.Content.Headers.ContentType?.ToString());
            string plainText = await textResp.Content.ReadAsStringAsync();
            Assert.StartsWith("HTTP识别测试文本", plainText);

            // 7. POST /ocr with invalid image (Bad Request 400)
            using var invalidContent = new ByteArrayContent([1, 2, 3, 4, 5]);
            invalidContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            var badResp = await client.PostAsync($"{baseUri}/ocr", invalidContent);
            Assert.Equal(HttpStatusCode.BadRequest, badResp.StatusCode);
            string badText = await badResp.Content.ReadAsStringAsync();
            Assert.Contains("\"success\":false", badText);
        }
        finally
        {
            await server.StopAsync();
            Assert.False(server.IsRunning);
        }
    }

    // ==========================================================
    // 安全加固回归测试：任意文件读取 / 跨域 / 鉴权 / 体积上限 / 并发准入
    // ==========================================================

    private static ByteArrayContent PngContent(byte[] png)
    {
        var content = new ByteArrayContent(png);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        return content;
    }

    [Fact]
    public async Task Ocr_PathQueryParameter_IsRejected()
    {
        int port = 18091;
        using var server = new HttpOcrServer(new FakeEngineFactory(), new FakeSettingsService());
        await server.StartAsync(port, allowRemote: false, maxCores: 1, cpuLimitPercent: 100);

        using var client = new HttpClient();
        try
        {
            // 原实现会直接读取该路径并返回识别结果（任意文件读取漏洞）
            string target = Path.Combine(Path.GetTempPath(), "shawmu_not_a_real_secret.png");
            var resp = await client.PostAsync(
                $"http://127.0.0.1:{port}/ocr?path={Uri.EscapeDataString(target)}",
                new ByteArrayContent([]));

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            Assert.Contains("path", await resp.Content.ReadAsStringAsync());
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task Ocr_JsonImagePath_IsRejected()
    {
        int port = 18092;
        using var server = new HttpOcrServer(new FakeEngineFactory(), new FakeSettingsService());
        await server.StartAsync(port, allowRemote: false, maxCores: 1, cpuLimitPercent: 100);

        using var client = new HttpClient();
        try
        {
            string json = "{\"imagePath\":\"C:\\\\Windows\\\\win.ini\"}";
            var resp = await client.PostAsync(
                $"http://127.0.0.1:{port}/ocr",
                new StringContent(json, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            Assert.Contains("imagePath", await resp.Content.ReadAsStringAsync());
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task Cors_ForeignOrigin_IsRejected()
    {
        int port = 18093;
        using var server = new HttpOcrServer(new FakeEngineFactory(), new FakeSettingsService());
        await server.StartAsync(port, allowRemote: false, maxCores: 1, cpuLimitPercent: 100);

        using var client = new HttpClient();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, $"http://127.0.0.1:{port}/ocr");
            request.Headers.Add("Origin", "https://evil.example");
            request.Headers.Add("Access-Control-Request-Method", "POST");

            var resp = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
            Assert.False(resp.Headers.Contains("Access-Control-Allow-Origin"));
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task Cors_LocalhostOrigin_IsAllowed()
    {
        int port = 18094;
        using var server = new HttpOcrServer(new FakeEngineFactory(), new FakeSettingsService());
        await server.StartAsync(port, allowRemote: false, maxCores: 1, cpuLimitPercent: 100);

        using var client = new HttpClient();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, $"http://127.0.0.1:{port}/ocr");
            request.Headers.Add("Origin", "http://localhost:5173");
            request.Headers.Add("Access-Control-Request-Method", "POST");

            var resp = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
            Assert.Equal("http://localhost:5173", resp.Headers.GetValues("Access-Control-Allow-Origin").Single());
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task Auth_RequiresBearerToken_WhenConfigured()
    {
        int port = 18095;
        const string token = "shawmu-test-token-0123456789";
        using var server = new HttpOcrServer(new FakeEngineFactory(), new FakeSettingsService());
        await server.StartAsync(port, allowRemote: false, maxCores: 1, cpuLimitPercent: 100, accessToken: token);

        Assert.True(server.RequiresAuth);

        using var client = new HttpClient();
        byte[] png = CreateTestPng();
        string url = $"http://127.0.0.1:{port}/ocr";

        try
        {
            // 1. 无令牌 → 401
            var noToken = await client.PostAsync(url, PngContent(png));
            Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
            Assert.True(noToken.Headers.Contains("WWW-Authenticate"));

            // 2. 错误令牌 → 401
            using var wrongReq = new HttpRequestMessage(HttpMethod.Post, url) { Content = PngContent(png) };
            wrongReq.Headers.Add("Authorization", "Bearer wrong-token");
            var wrongToken = await client.SendAsync(wrongReq);
            Assert.Equal(HttpStatusCode.Unauthorized, wrongToken.StatusCode);

            // 3. 正确令牌 → 200
            using var okReq = new HttpRequestMessage(HttpMethod.Post, url) { Content = PngContent(png) };
            okReq.Headers.Add("Authorization", $"Bearer {token}");
            var okResp = await client.SendAsync(okReq);
            Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);
            Assert.Contains("\"success\":true", await okResp.Content.ReadAsStringAsync());
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task StartAsync_RemoteWithoutToken_Throws()
    {
        using var server = new HttpOcrServer(new FakeEngineFactory(), new FakeSettingsService());

        // 失败即关闭：禁止无令牌的局域网暴露
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.StartAsync(18096, allowRemote: true, maxCores: 1, cpuLimitPercent: 100));

        Assert.Contains("令牌", ex.Message);
        Assert.False(server.IsRunning);
    }

    [Fact]
    public async Task Ocr_OversizedBody_Returns413()
    {
        int port = 18097;
        using var server = new HttpOcrServer(new FakeEngineFactory(), new FakeSettingsService());
        await server.StartAsync(port, allowRemote: false, maxCores: 1, cpuLimitPercent: 100);

        using var client = new HttpClient();
        try
        {
            // 21MB > 20MB 硬上限
            byte[] oversized = new byte[21 * 1024 * 1024];
            var resp = await client.PostAsync($"http://127.0.0.1:{port}/ocr", PngContent(oversized));

            Assert.Equal(413, (int)resp.StatusCode);
            Assert.Contains("请求体过大", await resp.Content.ReadAsStringAsync());
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task Ocr_ConcurrentRequestsBeyondGate_Returns429()
    {
        int port = 18098;
        var factory = new BlockingEngineFactory();
        using var server = new HttpOcrServer(factory, new FakeSettingsService());
        await server.StartAsync(port, allowRemote: false, maxCores: 1, cpuLimitPercent: 100);

        // 闸门配额 = max(2, maxCores × 2) = 2
        Assert.Equal(2, server.MaxConcurrentRequests);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        byte[] png = CreateTestPng();
        string url = $"http://127.0.0.1:{port}/ocr";

        try
        {
            var first = client.PostAsync(url, PngContent(png));
            var second = client.PostAsync(url, PngContent(png));

            // 等到至少一个请求真正进入识别阶段（说明它已占用闸门配额）
            Assert.True(await factory.Started.WaitAsync(TimeSpan.FromSeconds(10)),
                "前两个请求未能进入识别阶段，无法验证并发闸门");

            // 第三个请求：配额已满，应立即 429 而不是排队等待
            var third = await client.PostAsync(url, PngContent(png));
            Assert.Equal(429, (int)third.StatusCode);

            factory.Release.Set();
            await Task.WhenAll(first, second);
        }
        finally
        {
            factory.Release.Set();
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task StartAsync_RemoteAnonymous_WorksWhenExplicitlyAllowed()
    {
        int port = 18099;
        using var server = new HttpOcrServer(new FakeEngineFactory(), new FakeSettingsService());

        // 显式允许无令牌开放局域网（分享给同网段同事的场景）
        await server.StartAsync(
            port,
            allowRemote: true,
            maxCores: 1,
            cpuLimitPercent: 100,
            accessToken: null,
            allowAnonymousRemote: true);

        Assert.True(server.IsRunning);
        Assert.False(server.RequiresAuth);
        Assert.True(server.IsAnonymousRemote);

        using var client = new HttpClient();
        byte[] png = CreateTestPng();

        try
        {
            // 不带任何 Authorization 头也必须能正常识别
            var resp = await client.PostAsync($"http://127.0.0.1:{port}/ocr", PngContent(png));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Contains("\"success\":true", await resp.Content.ReadAsStringAsync());

            // 但"按路径读文件"依然被禁止：放开的是鉴权，不是安全边界
            var pathResp = await client.PostAsync(
                $"http://127.0.0.1:{port}/ocr?path={Uri.EscapeDataString("C:\\Windows\\win.ini")}",
                new ByteArrayContent([]));
            Assert.Equal(HttpStatusCode.BadRequest, pathResp.StatusCode);

            // 跨域限制同样不因无令牌模式而放宽
            using var corsReq = new HttpRequestMessage(HttpMethod.Options, $"http://127.0.0.1:{port}/ocr");
            corsReq.Headers.Add("Origin", "https://evil.example");
            var corsResp = await client.SendAsync(corsReq);
            Assert.Equal(HttpStatusCode.Forbidden, corsResp.StatusCode);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    /// <summary>
    /// 识别阶段阻塞的引擎工厂，用于稳定复现"并发配额耗尽"
    /// </summary>
    private sealed class BlockingEngineFactory : IOcrEngineFactory
    {
        private readonly BlockingEngine _engine = new();

        public SemaphoreSlim Started => _engine.Started;
        public ManualResetEventSlim Release => _engine.Release;

        public IOcrEngine Create(string modelName) => _engine;
        public IOcrEngine Create(string modelName, bool enableAngle, int maxSideLen) => _engine;
        public IOcrEngine Create(AppSettings settings) => _engine;

        private sealed class BlockingEngine : IOcrEngine
        {
            public SemaphoreSlim Started { get; } = new(0);
            public ManualResetEventSlim Release { get; } = new(false);

            public string ModelName => "BlockingModel";

            public async Task<OcrResult> RecognizeAsync(OcrImage image, CancellationToken cancellationToken = default)
            {
                Started.Release();
                await Task.Run(
                    () => Release.Wait(TimeSpan.FromSeconds(20), cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                return new OcrResult
                {
                    Text = "阻塞测试",
                    Blocks = [],
                    Elapsed = TimeSpan.Zero,
                    IsSuccess = true
                };
            }

            public Task WarmupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public void Dispose() { }
        }
    }
}
