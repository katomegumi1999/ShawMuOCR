using System;
using Sdcb.SimdPaddleOCR;

namespace ShawMuOCR.Android.Platform;

/// <summary>
/// 纯内存图像数据载体（直接承载解码或光栅化后的原始像素，杜绝任何磁盘落盘缓存）
/// </summary>
public sealed class ImageBuffer : IDisposable
{
    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public ImagePixelFormat PixelFormat { get; }

    public ImageBuffer(byte[] pixels, int width, int height, ImagePixelFormat pixelFormat = ImagePixelFormat.Rgba32)
    {
        Pixels = pixels ?? throw new ArgumentNullException(nameof(pixels));
        Width = width;
        Height = height;
        PixelFormat = pixelFormat;
    }

    public void Dispose()
    {
        // 允许 GC 及时回收像素大对象
    }
}
