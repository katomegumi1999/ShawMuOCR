using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using ShawMuOCR.Core.Capture;
using ShawMuOCR.Core.OCR;
using SkiaSharp;

namespace ShawMuOCR.OCR;

/// <summary>
/// 外部 Web API OCR 推理引擎，将截取的图像上传至用户配置的 HTTP 服务并解析文本结果
/// </summary>
public sealed class ExternalApiOcrEngine : IOcrEngine
{
    private readonly string _apiUrl;
    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;
    private bool _disposed;

    public string ModelName => $"ExternalApi ({_apiUrl})";

    public ExternalApiOcrEngine(string apiUrl, HttpClient? httpClient = null)
    {
        _apiUrl = string.IsNullOrWhiteSpace(apiUrl) ? "http://127.0.0.1:8080/ocr" : apiUrl.Trim();
        if (httpClient != null)
        {
            _httpClient = httpClient;
            _disposeClient = false;
        }
        else
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            _disposeClient = true;
        }
    }

    public async Task<OcrResult> RecognizeAsync(OcrImage image, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(image);

        var sw = Stopwatch.StartNew();
        try
        {
            var bitmap = image.Bitmap;
            if (bitmap == null || bitmap.IsNull || bitmap.Handle == IntPtr.Zero || bitmap.Width <= 0 || bitmap.Height <= 0)
            {
                return OcrResult.Empty(TimeSpan.Zero);
            }

            // 1. 将位图编码为 PNG 内存字节流
            using var ms = new MemoryStream();
            bitmap.Encode(ms, SKEncodedImageFormat.Png, 100);
            byte[] pngBytes = ms.ToArray();

            // 2. 构造标准 multipart/form-data 请求
            using var form = new MultipartFormDataContent();
            using var fileContent = new ByteArrayContent(pngBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(fileContent, "file", "image.png");

            // 3. 发送 POST 请求至外部接口
            using var response = await _httpClient.PostAsync(_apiUrl, form, cancellationToken);
            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return OcrResult.Failed($"HTTP 接口返回错误 ({(int)response.StatusCode}): {responseBody}", sw.Elapsed);
            }

            // 4. 解析响应内容（AOT 友好的 JsonDocument DOM 解析）
            var blocks = ParseOcrResponse(responseBody);
            string fullText = string.Join(Environment.NewLine, blocks.Select(b => b.Text));

            return new OcrResult
            {
                Text = fullText,
                Blocks = blocks,
                Elapsed = sw.Elapsed,
                IsSuccess = true
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return OcrResult.Failed("OCR 识别已取消", sw.Elapsed);
        }
        catch (Exception ex)
        {
            return OcrResult.Failed($"外部接口请求异常: {ex.Message}", sw.Elapsed);
        }
    }

    /// <summary>
    /// 宽容解析多种格式的 OCR 服务响应（PaddleOCR-json 规范、通用 JSON 及纯文本）
    /// </summary>
    public static List<OcrTextBlock> ParseOcrResponse(string responseText)
    {
        var blocks = new List<OcrTextBlock>();
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return blocks;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseText);
            var root = doc.RootElement;

            // 格式 1: {"code": 100/200, "data": [{"text": "...", "score": 0.98, "box": [...]}]}
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
            {
                ParseJsonArray(dataElement, blocks);
            }
            // 格式 2: 根节点直接是数组 [{"text": "...", "score": 0.98}]
            else if (root.ValueKind == JsonValueKind.Array)
            {
                ParseJsonArray(root, blocks);
            }
            // 格式 3: {"result": [...]} 或 {"lines": [...]}
            else if (root.ValueKind == JsonValueKind.Object && (root.TryGetProperty("result", out var resElement) || root.TryGetProperty("lines", out resElement)) && resElement.ValueKind == JsonValueKind.Array)
            {
                ParseJsonArray(resElement, blocks);
            }
            // 格式 4: {"text": "完整识别结果文本"}
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("text", out var singleTextElement))
            {
                string? t = singleTextElement.GetString();
                if (!string.IsNullOrWhiteSpace(t))
                {
                    blocks.Add(new OcrTextBlock
                    {
                        Text = t,
                        Confidence = 1.0f,
                        BoundingBox = new SKRectI(0, 0, 100, 20),
                        BoxPoints = new[] { new SKPointI(0, 0), new SKPointI(100, 0), new SKPointI(100, 20), new SKPointI(0, 20) }
                    });
                }
            }
        }
        catch
        {
            // 不是有效 JSON，降级为按行解析纯文本
            var lines = responseText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            int y = 0;
            foreach (var line in lines)
            {
                string trimmed = line.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    blocks.Add(new OcrTextBlock
                    {
                        Text = trimmed,
                        Confidence = 1.0f,
                        BoundingBox = new SKRectI(0, y, 100, y + 20),
                        BoxPoints = new[] { new SKPointI(0, y), new SKPointI(100, y), new SKPointI(100, y + 20), new SKPointI(0, y + 20) }
                    });
                    y += 25;
                }
            }
        }

        return blocks;
    }

    private static void ParseJsonArray(JsonElement arrayElement, List<OcrTextBlock> blocks)
    {
        int fallbackY = 0;
        foreach (var item in arrayElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                string? text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    blocks.Add(new OcrTextBlock
                    {
                        Text = text,
                        Confidence = 1.0f,
                        BoundingBox = new SKRectI(0, fallbackY, 100, fallbackY + 20),
                        BoxPoints = new[] { new SKPointI(0, fallbackY), new SKPointI(100, fallbackY), new SKPointI(100, fallbackY + 20), new SKPointI(0, fallbackY + 20) }
                    });
                    fallbackY += 25;
                }
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object) continue;

            string textVal = string.Empty;
            if (item.TryGetProperty("text", out var propText)) textVal = propText.GetString() ?? string.Empty;
            else if (item.TryGetProperty("words", out propText)) textVal = propText.GetString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(textVal)) continue;

            float scoreVal = 1.0f;
            if (item.TryGetProperty("score", out var propScore) && propScore.TryGetSingle(out float s)) scoreVal = s;
            else if (item.TryGetProperty("confidence", out propScore) && propScore.TryGetSingle(out s)) scoreVal = s;

            SKRectI rect = new SKRectI(0, fallbackY, 100, fallbackY + 20);
            var boxPoints = new List<SKPointI>();

            if (item.TryGetProperty("box", out var propBox) && propBox.ValueKind == JsonValueKind.Array)
            {
                // 解析坐标多边形与外接矩形 [[x1, y1], [x2, y2], [x3, y3], [x4, y4]]
                foreach (var pt in propBox.EnumerateArray())
                {
                    if (pt.ValueKind == JsonValueKind.Array)
                    {
                        var coords = pt.EnumerateArray().ToList();
                        if (coords.Count >= 2 && coords[0].TryGetInt32(out int x) && coords[1].TryGetInt32(out int y))
                        {
                            boxPoints.Add(new SKPointI(x, y));
                        }
                    }
                }

                if (boxPoints.Count > 0)
                {
                    int minX = boxPoints.Min(p => p.X);
                    int minY = boxPoints.Min(p => p.Y);
                    int maxX = boxPoints.Max(p => p.X);
                    int maxY = boxPoints.Max(p => p.Y);
                    rect = new SKRectI(minX, minY, Math.Max(minX + 1, maxX), Math.Max(minY + 1, maxY));
                }
            }

            if (boxPoints.Count == 0)
            {
                boxPoints.Add(new SKPointI(rect.Left, rect.Top));
                boxPoints.Add(new SKPointI(rect.Right, rect.Top));
                boxPoints.Add(new SKPointI(rect.Right, rect.Bottom));
                boxPoints.Add(new SKPointI(rect.Left, rect.Bottom));
            }

            blocks.Add(new OcrTextBlock
            {
                Text = textVal,
                Confidence = scoreVal,
                BoundingBox = rect,
                BoxPoints = boxPoints
            });

            fallbackY += 25;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_disposeClient)
        {
            _httpClient.Dispose();
        }
    }
}
