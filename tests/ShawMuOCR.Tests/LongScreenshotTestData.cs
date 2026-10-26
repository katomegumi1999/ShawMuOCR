using System.Runtime.InteropServices;
using SkiaSharp;

namespace ShawMuOCR.Tests;

/// <summary>
/// 长截图相关测试共用的合成素材：一张 480×3000 的「类文档」长图，
/// 以及按偏移量切视口、逐行读取的辅助方法。
/// 因为每一帧的真实位移与像素内容都完全确定，测试结论可以做到逐字节断言。
/// </summary>
internal static class LongScreenshotTestData
{
    /// <summary>文档宽度，同时用作视口宽度。</summary>
    public const int DocWidth = 480;

    /// <summary>文档高度，远高于单个视口，保证可以连续滚动很多次。</summary>
    public const int DocHeight = 3000;

    /// <summary>视口（单帧）高度。</summary>
    public const int ViewportHeight = 600;

    /// <summary>
    /// 造一张「类文档」长图：每个像素由坐标哈希决定，再叠加强周期性很弱的行结构。
    /// 不能做成等距条纹——行间距均匀的画面会让行投影产生周期性混叠，掩盖估计器的真实能力。
    /// </summary>
    public static SKBitmap CreateDocument(uint seed = 12345, int width = DocWidth, int height = DocHeight)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        unsafe
        {
            byte* pixels = (byte*)bitmap.GetPixels();
            int rowBytes = bitmap.RowBytes;
            uint state = seed;

            for (int y = 0; y < height; y++)
            {
                // 每行的基调随 y 做非周期变化（用哈希而不是取模）
                uint rowHash = Hash((uint)y * 2654435761u + seed);
                byte rowTint = (byte)(rowHash >> 24);
                int lineBand = (int)((rowHash >> 8) & 0x1F);

                byte* line = pixels + (long)y * rowBytes;
                for (int x = 0; x < width; x++)
                {
                    state = state * 1664525 + 1013904223;
                    byte noise = (byte)(state >> 24);
                    // 局部暗条模拟文字行，位置由行哈希决定，不是固定周期
                    byte text = (byte)(lineBand < 6 && (x + (int)(rowHash & 0x7F)) % 97 < 48 ? 20 : 200);

                    int offset = x * 4;
                    line[offset] = (byte)((text + noise / 8) & 0xFF);                 // B
                    line[offset + 1] = (byte)((text + rowTint / 8) & 0xFF);           // G
                    line[offset + 2] = (byte)((text ^ (rowTint >> 1)) & 0xFF);        // R
                    line[offset + 3] = 255;                                            // A
                }
            }
        }

        return bitmap;
    }

    /// <summary>造一张与文档结构完全不同的横条纹画面，用来模拟「窗口被切走」。</summary>
    public static SKBitmap CreateStripedFrame(int width = DocWidth, int height = ViewportHeight)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.Black };
        for (int y = 0; y < height; y += 40)
        {
            canvas.DrawRect(0, y, width, 20, paint);
        }

        return bitmap;
    }

    /// <summary>从长图裁出视口高度的一帧。</summary>
    public static SKBitmap CutFrame(SKBitmap document, int offset, int viewportHeight = ViewportHeight)
    {
        var frame = new SKBitmap(new SKImageInfo(document.Width, viewportHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(frame);
        canvas.DrawBitmap(
            document,
            new SKRect(0, offset, document.Width, offset + viewportHeight),
            new SKRect(0, 0, document.Width, viewportHeight),
            SKSamplingOptions.Default);
        return frame;
    }

    /// <summary>读取指定起始行开始的连续若干行原始像素，用于逐字节比对。</summary>
    public static byte[] ReadRows(SKBitmap bitmap, int top, int rows)
    {
        int rowBytes = bitmap.RowBytes;
        var buffer = new byte[rowBytes * rows];
        Marshal.Copy(new IntPtr(bitmap.GetPixels().ToInt64() + (long)top * rowBytes), buffer, 0, buffer.Length);
        return buffer;
    }

    private static uint Hash(uint value)
    {
        value ^= value >> 16;
        value *= 0x7FEB352D;
        value ^= value >> 15;
        value *= 0x846CA68B;
        value ^= value >> 16;
        return value;
    }
}
