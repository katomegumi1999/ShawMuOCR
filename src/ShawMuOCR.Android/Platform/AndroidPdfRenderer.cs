using System;
using System.Collections.Generic;
using AndroidContext = global::Android.Content.Context;
using AndroidUri = global::Android.Net.Uri;
using AndroidBitmap = global::Android.Graphics.Bitmap;
using AndroidPdfRendererCore = global::Android.Graphics.Pdf.PdfRenderer;
using AndroidPdfRenderMode = global::Android.Graphics.Pdf.PdfRenderMode;
using AndroidCanvas = global::Android.Graphics.Canvas;
using AndroidColor = global::Android.Graphics.Color;

namespace ShawMuOCR.Android.Platform;

/// <summary>
/// 采用 Android 官方原生 PdfRenderer 的内存光栅化引擎
/// 直接将 PDF 各页渲染至内存位图并导出像素，不引入冗余第三方依赖
/// </summary>
public static class AndroidPdfRenderer
{
    /// <summary>
    /// 获取 PDF 总页数
    /// </summary>
    public static int GetPageCount(AndroidContext context, AndroidUri uri)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(uri);

        using var pfd = context.ContentResolver!.OpenFileDescriptor(uri, "r")
            ?? throw new InvalidOperationException($"无法打开 PDF 描述符: {uri}");
        using var renderer = new AndroidPdfRendererCore(pfd);
        return renderer.PageCount;
    }

    /// <summary>
    /// 将指定页面渲染至内存 ImageBuffer（零文件落盘）
    /// </summary>
    public static ImageBuffer RenderPage(AndroidContext context, AndroidUri uri, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(uri);

        using var pfd = context.ContentResolver!.OpenFileDescriptor(uri, "r")
            ?? throw new InvalidOperationException($"无法打开 PDF 描述符: {uri}");
        using var renderer = new AndroidPdfRendererCore(pfd);

        if (pageIndex < 0 || pageIndex >= renderer.PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), $"页码超出范围: 0 ~ {renderer.PageCount - 1}");
        }

        using var page = renderer.OpenPage(pageIndex);
        int width = page.Width;
        int height = page.Height;

        float scale = (width < 1000 && height < 1000) ? 2.0f : 1.0f;
        int targetWidth = (int)(width * scale);
        int targetHeight = (int)(height * scale);

        using var bitmap = AndroidBitmap.CreateBitmap(targetWidth, targetHeight, AndroidBitmap.Config.Argb8888!)
            ?? throw new InvalidOperationException("创建 PDF 渲染位图失败");

        using (var canvas = new AndroidCanvas(bitmap))
        {
            canvas.DrawColor(AndroidColor.White);
        }

        page.Render(bitmap, null, null, AndroidPdfRenderMode.ForDisplay);

        return AndroidImageDecoder.ConvertBitmapToBuffer(bitmap);
    }

    /// <summary>
    /// 流式逐页渲染全部 PDF 页面并直传内存
    /// </summary>
    public static async IAsyncEnumerable<(int PageNumber, ImageBuffer Buffer)> RenderAllPagesAsync(
        AndroidContext context, AndroidUri uri)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(uri);

        using var pfd = context.ContentResolver!.OpenFileDescriptor(uri, "r")
            ?? throw new InvalidOperationException($"无法打开 PDF 描述符: {uri}");
        using var renderer = new AndroidPdfRendererCore(pfd);

        int totalPages = renderer.PageCount;
        for (int i = 0; i < totalPages; i++)
        {
            using var page = renderer.OpenPage(i);
            int width = page.Width;
            int height = page.Height;

            float scale = (width < 1000 && height < 1000) ? 2.0f : 1.0f;
            int targetWidth = (int)(width * scale);
            int targetHeight = (int)(height * scale);

            using var bitmap = AndroidBitmap.CreateBitmap(targetWidth, targetHeight, AndroidBitmap.Config.Argb8888!)
                ?? throw new InvalidOperationException("创建 PDF 渲染位图失败");

            using (var canvas = new AndroidCanvas(bitmap))
            {
                canvas.DrawColor(AndroidColor.White);
            }

            page.Render(bitmap, null, null, AndroidPdfRenderMode.ForDisplay);

            var buffer = AndroidImageDecoder.ConvertBitmapToBuffer(bitmap);
            yield return (i + 1, buffer);
            await System.Threading.Tasks.Task.Yield();
        }
    }
}
