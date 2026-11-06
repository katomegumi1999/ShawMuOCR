using System.Diagnostics;
using ShawMuOCR.Core.Ai;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Settings;
using SkiaSharp;

namespace ShawMuOCR.OCR;

/// <summary>
/// AI 视觉接口 OCR 引擎：把图片编码后通过统一客户端投递给节点池中的某个节点，
/// 由大模型按用户配置的提示词返回文字内容。
///
/// 该引擎只是 <see cref="ILlmClient"/> 的适配层：协议、鉴权、重试、限流都在客户端里，
/// 额度记账统一走 <see cref="IAiQuotaService"/>。
///
/// 不产生文字框坐标 —— 大模型给不出可靠位置，伪造坐标会让对外 HTTP 接口输出错位框，
/// 因此 Blocks 恒为空，下游批量导出自动退化为纯文本路径。
/// </summary>
public sealed class AiApiOcrEngine : IOcrEngine
{
    private readonly ILlmClient _client;
    private readonly IAiQuotaService? _quota;
    private readonly string _prompt;
    private readonly string _displayName;
    private readonly int _maxImageSide;
    private readonly int _jpegQuality;

    public string ModelName => $"AI ({_displayName})";

    /// <summary>
    /// 不占用本机 CPU 推理资源，批量调度器据此跳过 CPU 占空比补偿休眠。
    /// </summary>
    public bool UsesLocalCpu => false;

    public AiApiOcrEngine(
        ILlmClient client,
        string displayName,
        string? prompt = null,
        IAiQuotaService? quota = null,
        int maxImageSide = 1536,
        int jpegQuality = 85)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _quota = quota;
        _displayName = string.IsNullOrWhiteSpace(displayName) ? client.ProfileId : displayName;
        _prompt = string.IsNullOrWhiteSpace(prompt) ? AiOcrPrompt.Default : prompt;
        _maxImageSide = Math.Clamp(maxImageSide, 256, 4096);
        _jpegQuality = Math.Clamp(jpegQuality, 40, 100);
    }

    public async Task<OcrResult> RecognizeAsync(OcrImage image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        var sw = Stopwatch.StartNew();
        try
        {
            var bitmap = image.Bitmap;
            if (bitmap == null || bitmap.IsNull || bitmap.Handle == IntPtr.Zero || bitmap.Width <= 0 || bitmap.Height <= 0)
            {
                return OcrResult.Empty(TimeSpan.Zero);
            }

            if (_quota != null && !_quota.TryCheck(_client.ProfileId, out string? deny))
            {
                return OcrResult.Failed(deny ?? "该 AI 节点的本月额度已用尽", sw.Elapsed);
            }

            byte[] jpegBytes = EncodeForUpload(bitmap);
            if (jpegBytes.Length == 0)
            {
                return OcrResult.Failed("图片编码失败，无法提交给 AI 接口。", sw.Elapsed);
            }

            string prompt = _prompt.Length > AiOcrPrompt.MaxLength
                ? _prompt[..AiOcrPrompt.MaxLength]
                : _prompt;

            var response = await _client.CompleteAsync(
                new LlmRequest
                {
                    Prompt = prompt,
                    ImageJpeg = jpegBytes,
                    // 识别任务要稳定复现，不要随机性
                    Temperature = 0
                },
                cancellationToken).ConfigureAwait(false);

            sw.Stop();

            if (!response.IsSuccess)
            {
                return OcrResult.Failed(response.ErrorMessage ?? "AI 接口调用失败", sw.Elapsed);
            }

            _quota?.Commit(_client.ProfileId, response.TotalTokens);

            return new OcrResult
            {
                Text = response.Content,
                Blocks = Array.Empty<OcrTextBlock>(),
                Elapsed = sw.Elapsed,
                IsSuccess = true
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return OcrResult.Failed("AI 识别已取消", sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return OcrResult.Failed($"AI 接口调用异常: {ex.Message}", sw.Elapsed);
        }
    }

    /// <summary>
    /// 将位图等比缩放到上限边长以内，并编码为 JPEG。
    /// 用 JPEG 而非 PNG：同等视觉质量下体积小 5–8 倍，base64 后差距同步放大，
    /// 直接决定上传耗时与图片 token 数。
    /// </summary>
    private byte[] EncodeForUpload(SKBitmap source)
    {
        SKBitmap? scaled = null;
        try
        {
            int longest = Math.Max(source.Width, source.Height);
            SKBitmap target = source;

            if (longest > _maxImageSide)
            {
                double scale = (double)_maxImageSide / longest;
                int w = Math.Max(1, (int)Math.Round(source.Width * scale));
                int h = Math.Max(1, (int)Math.Round(source.Height * scale));

                scaled = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
                using (var canvas = new SKCanvas(scaled))
                {
                    // JPEG 不支持透明通道，先铺白底，避免带透明区域的截图变成黑块
                    canvas.Clear(SKColors.White);
                    canvas.DrawBitmap(
                        source,
                        new SKRect(0, 0, source.Width, source.Height),
                        new SKRect(0, 0, w, h),
                        SKSamplingOptions.Default);
                }
                target = scaled;
            }

            using var image = SKImage.FromBitmap(target);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, _jpegQuality);
            return data?.ToArray() ?? Array.Empty<byte>();
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    public void Dispose()
    {
        // 客户端与信号量为静态/共享资源，由进程生命周期管理，此处无需释放
    }
}
