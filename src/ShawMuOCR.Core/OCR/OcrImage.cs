using SkiaSharp;

namespace ShawMuOCR.Core.OCR;

public sealed class OcrImage : IDisposable
{
    private bool _disposed;

    public SKBitmap Bitmap { get; }
    public int Width => Bitmap.Width;
    public int Height => Bitmap.Height;

    public OcrImage(SKBitmap bitmap)
    {
        Bitmap = bitmap ?? throw new ArgumentNullException(nameof(bitmap));
    }

    /// <summary>
    /// 从当前大图像中无损裁剪子区域
    /// </summary>
    public OcrImage Crop(int x, int y, int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 边界裁剪钳制
        int clampedX = Math.Clamp(x, 0, Bitmap.Width);
        int clampedY = Math.Clamp(y, 0, Bitmap.Height);
        int clampedWidth = Math.Clamp(width, 1, Bitmap.Width - clampedX);
        int clampedHeight = Math.Clamp(height, 1, Bitmap.Height - clampedY);

        // 核心修复：必须独立分配新位图内存并拷贝像素，绝不能使用 ExtractSubset！
        // 因为 ExtractSubset 会与母图共享物理显存且继承母图的巨大行跨度 (RowBytes=全屏宽*4)。
        // 一旦母图被 Dispose()，共享内存即刻成为野指针悬垂引用（Use-After-Free），导致截图呈现严重的斜纹噪点乱码。
        var rect = new SKRectI(clampedX, clampedY, clampedX + clampedWidth, clampedY + clampedHeight);
        var info = new SKImageInfo(clampedWidth, clampedHeight, Bitmap.ColorType, Bitmap.AlphaType);
        var cropped = new SKBitmap(info);

        using (var canvas = new SKCanvas(cropped))
        {
            canvas.DrawBitmap(Bitmap, rect, new SKRect(0, 0, clampedWidth, clampedHeight), SKSamplingOptions.Default);
        }

        return new OcrImage(cropped);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Bitmap.Dispose();
            _disposed = true;
        }
    }
}
