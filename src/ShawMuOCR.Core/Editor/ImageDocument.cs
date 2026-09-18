using System.Collections.ObjectModel;
using ShawMuOCR.Core.OCR;
using SkiaSharp;

namespace ShawMuOCR.Core.Editor;

public sealed class ImageDocument : IDisposable
{
    private bool _disposed;

    public OcrImage OriginalImage { get; }
    public List<IImageElement> Elements { get; } = new();
    public UndoRedoManager UndoRedo { get; } = new();

    public ImageDocument(OcrImage originalImage)
    {
        OriginalImage = originalImage ?? throw new ArgumentNullException(nameof(originalImage));
    }

    public void AddElement(IImageElement element)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        UndoRedo.Execute(new AddElementCommand(Elements, element));
    }

    public void RemoveElement(IImageElement element)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        UndoRedo.Execute(new RemoveElementCommand(Elements, element));
    }

    public void MoveElement(IImageElement element, float dx, float dy)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        UndoRedo.Execute(new MoveElementCommand(element, dx, dy));
    }

    public IImageElement? HitTest(SKPoint point)
    {
        // 从最上层（最后绘制的图元）向下倒序探测
        for (int i = Elements.Count - 1; i >= 0; i--)
        {
            if (Elements[i].HitTest(point))
            {
                return Elements[i];
            }
        }
        return null;
    }

    /// <summary>
    /// 将原始图片与所有编辑图元合成渲染输出为全新的 OcrImage（100% 原始尺寸）
    /// </summary>
    public OcrImage RenderToBitmap() => RenderToBitmap(OriginalImage.Width, OriginalImage.Height);

    /// <summary>
    /// 按指定缩放比例渲染输出图片
    /// </summary>
    public OcrImage RenderToBitmap(float scale)
    {
        int targetW = Math.Max(1, (int)Math.Round(OriginalImage.Width * scale));
        int targetH = Math.Max(1, (int)Math.Round(OriginalImage.Height * scale));
        return RenderToBitmap(targetW, targetH);
    }

    /// <summary>
    /// 按指定目标宽高渲染输出图片（矢量图元按比例无损重绘）
    /// </summary>
    public OcrImage RenderToBitmap(int targetWidth, int targetHeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        targetWidth = Math.Max(1, targetWidth);
        targetHeight = Math.Max(1, targetHeight);

        var info = new SKImageInfo(targetWidth, targetHeight, OriginalImage.Bitmap.ColorType, OriginalImage.Bitmap.AlphaType);
        var targetBitmap = new SKBitmap(info);

        using (var canvas = new SKCanvas(targetBitmap))
        {
            float scaleX = (float)targetWidth / OriginalImage.Width;
            float scaleY = (float)targetHeight / OriginalImage.Height;

            if (Math.Abs(scaleX - 1f) > 0.0001f || Math.Abs(scaleY - 1f) > 0.0001f)
            {
                canvas.Scale(scaleX, scaleY);
            }

            // 1. 绘制底层原图，启用线性过滤保持缩放平滑
            canvas.DrawBitmap(OriginalImage.Bitmap, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));

            // 2. 依次叠加渲染矢量图元
            foreach (var element in Elements)
            {
                element.Render(canvas);
            }
        }

        return new OcrImage(targetBitmap);
    }

    /// <summary>
    /// 将编辑图元及底图按指定格式、画质与尺寸编码为二进制字节数组
    /// </summary>
    public byte[] Encode(ImageExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);

        int targetW = options.TargetWidth > 0 ? options.TargetWidth : Math.Max(1, (int)Math.Round(OriginalImage.Width * (options.Scale > 0 ? options.Scale : 1.0f)));
        int targetH = options.TargetHeight > 0 ? options.TargetHeight : Math.Max(1, (int)Math.Round(OriginalImage.Height * (options.Scale > 0 ? options.Scale : 1.0f)));

        using var rendered = RenderToBitmap(targetW, targetH);

        if (options.Format == ImageSaveFormat.Bmp)
        {
            return EncodeBmp(rendered.Bitmap);
        }

        var skFormat = options.Format switch
        {
            ImageSaveFormat.Png => SKEncodedImageFormat.Png,
            ImageSaveFormat.Jpeg => SKEncodedImageFormat.Jpeg,
            ImageSaveFormat.Webp => SKEncodedImageFormat.Webp,
            _ => SKEncodedImageFormat.Png
        };

        int quality = Math.Clamp(options.Quality, 1, 100);

        using var skImage = SKImage.FromBitmap(rendered.Bitmap);
        using var encodedData = skImage.Encode(skFormat, quality);
        if (encodedData == null)
        {
            throw new InvalidOperationException($"无法将图片编码为 {options.Format} 格式");
        }

        return encodedData.ToArray();
    }

    private static byte[] EncodeBmp(SKBitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        int stride = width * 4;
        int imageSize = stride * height;
        int fileSize = 14 + 40 + imageSize;

        byte[] bmp = new byte[fileSize];
        using var ms = new MemoryStream(bmp);
        using var bw = new BinaryWriter(ms);

        // BITMAPFILEHEADER (14 bytes)
        bw.Write((byte)'B');
        bw.Write((byte)'M');
        bw.Write(fileSize);
        bw.Write((short)0); // reserved1
        bw.Write((short)0); // reserved2
        bw.Write(14 + 40);  // offset to pixel data (54)

        // BITMAPINFOHEADER (40 bytes)
        bw.Write(40);        // biSize
        bw.Write(width);     // biWidth
        bw.Write(height);    // biHeight (positive = bottom-up)
        bw.Write((short)1);  // biPlanes
        bw.Write((short)32); // biBitCount
        bw.Write(0);         // biCompression (BI_RGB)
        bw.Write(imageSize); // biSizeImage
        bw.Write(0);         // biXPelsPerMeter
        bw.Write(0);         // biYPelsPerMeter
        bw.Write(0);         // biClrUsed
        bw.Write(0);         // biClrImportant

        // Pixel Data (32-bit BGRA, bottom-up)
        var span = bitmap.GetPixelSpan();
        int srcRowBytes = bitmap.RowBytes;

        for (int y = height - 1; y >= 0; y--)
        {
            int rowOffset = y * srcRowBytes;
            bw.Write(span.Slice(rowOffset, stride));
        }

        return bmp;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            OriginalImage.Dispose();
            Elements.Clear();
            UndoRedo.Clear();
        }
    }
}
