using System.Net;
using ShawMuOCR.Core.Capture;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.OCR;
using SkiaSharp;
using Xunit;

namespace ShawMuOCR.Tests;

public class ExternalApiOcrEngineTests
{
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request);
        }
    }

    [Fact]
    public void ParseOcrResponse_PaddleOcrJsonFormat_ParsesAccurately()
    {
        string json = """
        {
            "code": 100,
            "data": [
                {
                    "text": "小沐 OCR 2026",
                    "score": 0.992,
                    "box": [[10, 20], [200, 20], [200, 50], [10, 50]]
                },
                {
                    "text": "测试文本行",
                    "score": 0.950,
                    "box": [[10, 60], [150, 60], [150, 85], [10, 85]]
                }
            ]
        }
        """;

        var blocks = ExternalApiOcrEngine.ParseOcrResponse(json);

        Assert.Equal(2, blocks.Count);
        Assert.Equal("小沐 OCR 2026", blocks[0].Text);
        Assert.Equal(0.992f, blocks[0].Confidence, 3);
        Assert.Equal(10, blocks[0].BoundingBox.Left);
        Assert.Equal(20, blocks[0].BoundingBox.Top);
        Assert.Equal(200, blocks[0].BoundingBox.Right);
        Assert.Equal(50, blocks[0].BoundingBox.Bottom);

        Assert.Equal("测试文本行", blocks[1].Text);
    }

    [Fact]
    public void ParseOcrResponse_PlainText_SplitsLines()
    {
        string text = "第一行测试内容\r\n第二行测试内容";

        var blocks = ExternalApiOcrEngine.ParseOcrResponse(text);

        Assert.Equal(2, blocks.Count);
        Assert.Equal("第一行测试内容", blocks[0].Text);
        Assert.Equal("第二行测试内容", blocks[1].Text);
    }

    [Fact]
    public async Task RecognizeAsync_SuccessfulApiResponse_ReturnsRecognizedText()
    {
        var handler = new MockHttpMessageHandler(req =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                    "code": 100,
                    "data": [
                        { "text": "远程识别成功", "score": 0.99 }
                    ]
                }
                """)
            };
            return Task.FromResult(res);
        });

        using var client = new HttpClient(handler);
        using var engine = new ExternalApiOcrEngine("http://mock-api.local/ocr", client);

        using var bmp = new SKBitmap(100, 40);
        using var img = new OcrImage(bmp);

        var result = await engine.RecognizeAsync(img);

        Assert.True(result.IsSuccess);
        Assert.Contains("远程识别成功", result.Text);
        Assert.Single(result.Blocks);
    }

    [Fact]
    public async Task RecognizeAsync_HttpServerError_ReturnsFailedResult()
    {
        var handler = new MockHttpMessageHandler(req =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("Server Error")
            };
            return Task.FromResult(res);
        });

        using var client = new HttpClient(handler);
        using var engine = new ExternalApiOcrEngine("http://mock-api.local/ocr", client);

        using var bmp = new SKBitmap(50, 50);
        using var img = new OcrImage(bmp);

        var result = await engine.RecognizeAsync(img);

        Assert.False(result.IsSuccess);
        Assert.Contains("500", result.ErrorMessage);
    }
}
