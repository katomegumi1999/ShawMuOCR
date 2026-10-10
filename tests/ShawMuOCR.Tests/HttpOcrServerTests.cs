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
}
