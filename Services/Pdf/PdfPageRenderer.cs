using System;
using System.IO;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Rendering.Skia;

namespace ShawMuOCR.Services.Pdf;

/// <summary>
/// PDF 页面栅格化：把整页渲染成位图后再交给 OCR。
///
/// 为什么需要它：PdfPig 的 GetImages() 只能取出「内嵌位图」（扫描件那一类）。
/// 由 GIS / CAD / 排版软件导出的 PDF 常常是矢量图，文字被转成路径，
/// 这类页面既没有文字层（page.Text 为空）也没有内嵌图片（GetImages() 返回 0），
/// 旧逻辑就会「一页什么都没认出来」。渲染成位图后，任何 PDF 都能走 OCR。
/// </summary>
public static class PdfPageRenderer
{
    /// <summary>渲染目标宽度（像素）。太大拖慢 OCR，太小影响小字识别，1600 是折中值。</summary>
    private const int DefaultTargetWidthPx = 1600;

    /// <summary>
    /// 把某一页渲染成位图。失败返回 null 并通过 <paramref name="error"/> 给出原因。
    /// 渲染结果已按 PDF 的 /Rotate 呈现为视觉正确方向，调用方不要再额外旋转。
    /// </summary>
    public static SKBitmap? TryRenderPage(
        PdfDocument document,
        int pageNumber,
        out string? error,
        int targetWidthPx = DefaultTargetWidthPx)
    {
        error = null;

        try
        {
            var page = document.GetPage(pageNumber);
            double pageWidth = page.Width > 1 ? page.Width : 595.0;
            float scale = (float)Math.Clamp(targetWidthPx / pageWidth, 1.0, 3.0);

            EnsurePageFactory(document);

            using var stream = document.GetPageAsPng(pageNumber, scale, 300);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);

            if (buffer.Length == 0)
            {
                error = "渲染结果为空";
                return null;
            }

            var bitmap = SKBitmap.Decode(buffer.ToArray());
            if (bitmap is null)
            {
                error = "渲染结果无法解码为位图";
            }
            return bitmap;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    private static void EnsurePageFactory(PdfDocument document)
    {
        // AddSkiaPageFactory 只需注册一次；重复注册会上抛异常，这里吞掉即可（说明已经注册过）
        try
        {
            document.AddSkiaPageFactory();
        }
        catch (InvalidOperationException)
        {
            // 已注册
        }
    }
}
