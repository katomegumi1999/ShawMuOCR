using SkiaSharp;

namespace ShawMuOCR.Windows.ScreenCapture;

/// <summary>
/// DIB Section 像素内存的填充工具：把整屏截图拷进目标 DIB，并在 DIB 内就地生成暗化底图。
/// <para>
/// 之所以抽成独立类型：截图遮罩层的 GDI 初始化跑在自建消息循环线程里，无法在单元测试中驱动，
/// 但「逐行拷贝」和「暗化」这两段裸指针逻辑最容易写错（行跨度、通道顺序、越界），必须能单独验证。
/// </para>
/// </summary>
internal static class DibBufferPacker
{
    /// <summary>暗化系数：通道值乘以该系数后右移 8 位，约等于压到 47% 亮度。</summary>
    internal const int DarkenNumerator = 120;

    /// <summary>
    /// 把 <paramref name="source"/> 拷进目标 DIB 的像素内存。
    /// <para>
    /// 目标行跨度固定为 <c>destinationWidth * 4</c>（DIB Section 32bpp 无行对齐填充）；
    /// 源行跨度取 <see cref="SKBitmap.RowBytes"/>，两者不一定相等，所以必须逐行拷而不是整块拷。
    /// 源大于目标时只拷左上角，源小于目标时目标余下部分保持 DIB 建好时的全零。
    /// </para>
    /// <para>
    /// 源若不是 <see cref="SKColorType.Bgra8888"/>，先经 Skia 转一次再拷，避免通道顺序错位。
    /// </para>
    /// </summary>
    internal static unsafe void CopyInto(SKBitmap source, IntPtr destinationBits, int destinationWidth, int destinationHeight)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (destinationBits == IntPtr.Zero)
        {
            throw new ArgumentException("Destination DIB bits pointer is null.", nameof(destinationBits));
        }

        IntPtr sourcePixels = source.GetPixels();
        if (sourcePixels == IntPtr.Zero)
        {
            throw new ArgumentException("Source bitmap has no pixel buffer.", nameof(source));
        }

        SKBitmap normalized = source;
        SKBitmap? converted = null;
        try
        {
            if (source.ColorType != SKColorType.Bgra8888)
            {
                converted = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
                using (var canvas = new SKCanvas(converted))
                {
                    canvas.DrawBitmap(source, 0, 0, SKSamplingOptions.Default);
                }

                normalized = converted;
                sourcePixels = normalized.GetPixels();
            }

            int copyWidth = Math.Min(destinationWidth, normalized.Width);
            int copyHeight = Math.Min(destinationHeight, normalized.Height);
            if (copyWidth <= 0 || copyHeight <= 0)
            {
                return;
            }

            byte* src = (byte*)sourcePixels;
            byte* dst = (byte*)destinationBits;
            long sourceRowBytes = normalized.RowBytes;
            long destinationRowBytes = (long)destinationWidth * 4;
            long rowBytes = (long)copyWidth * 4;

            for (int y = 0; y < copyHeight; y++)
            {
                Buffer.MemoryCopy(src + y * sourceRowBytes, dst + y * destinationRowBytes, rowBytes, rowBytes);
            }
        }
        finally
        {
            converted?.Dispose();
        }
    }

    /// <summary>
    /// 就地生成暗化底图：把 <paramref name="sourceBits"/> 的每个像素压暗后写进 <paramref name="destinationBits"/>。
    /// 源与目标可以是同一块内存，但常规用法是两块独立的 DIB。
    /// </summary>
    internal static unsafe void BuildDarkened(IntPtr sourceBits, IntPtr destinationBits, int pixelCount)
    {
        if (sourceBits == IntPtr.Zero || destinationBits == IntPtr.Zero)
        {
            throw new ArgumentException("DIB bits pointer is null.");
        }

        if (pixelCount <= 0)
        {
            return;
        }

        byte* src = (byte*)sourceBits;
        byte* dst = (byte*)destinationBits;
        long totalBytes = (long)pixelCount * 4;

        for (long i = 0; i < totalBytes; i += 4)
        {
            dst[i] = (byte)((src[i] * DarkenNumerator) >> 8);         // B
            dst[i + 1] = (byte)((src[i + 1] * DarkenNumerator) >> 8); // G
            dst[i + 2] = (byte)((src[i + 2] * DarkenNumerator) >> 8); // R
            dst[i + 3] = 255;                                          // A
        }
    }
}
