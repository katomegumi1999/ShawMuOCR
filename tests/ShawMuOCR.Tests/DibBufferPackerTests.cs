using System.Runtime.InteropServices;
using ShawMuOCR.Windows.ScreenCapture;
using SkiaSharp;
using Xunit;

namespace ShawMuOCR.Tests;

/// <summary>
/// DIB Section 像素填充逻辑的单元测试。
/// 遮罩层的 GDI 初始化跑在自建消息循环线程里无法直接驱动，但这两段裸指针代码最容易写错
/// （行跨度、通道顺序、越界、透明通道），必须能单独验证。
/// </summary>
public class DibBufferPackerTests
{
    [Fact]
    public void CopyInto_ExactSize_CopiesEveryByte()
    {
        const int w = 17, h = 9;
        using var source = MakeBitmap(w, h);
        IntPtr destination = AllocZeroed(w, h);
        try
        {
            DibBufferPacker.CopyInto(source, destination, w, h);

            Assert.Equal(ReadPixels(source), ReadNative(destination, w * h * 4));
        }
        finally
        {
            Marshal.FreeHGlobal(destination);
        }
    }

    [Fact]
    public void CopyInto_HonorsSourceRowBytes_NotAssumedWidthTimesFour()
    {
        // 造一块「行跨度大于 width*4」的源：模拟带行对齐填充的上游位图。
        // 若实现里用整块拷贝或误用 width*4 当源跨度，这里会读到错行的数据。
        const int w = 13, h = 6;
        const int paddedRowBytes = 64; // 13*4 = 52，故意留 12 字节填充
        using var source = new SKBitmap();
        source.InstallPixels(
            new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul),
            Marshal.AllocHGlobal(paddedRowBytes * h),
            paddedRowBytes);

        var pattern = new byte[paddedRowBytes * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < paddedRowBytes; x++)
            {
                pattern[y * paddedRowBytes + x] = (byte)(y * 31 + x);
            }
        }
        Marshal.Copy(pattern, 0, source.GetPixels(), pattern.Length);

        IntPtr destination = AllocZeroed(w, h);
        try
        {
            DibBufferPacker.CopyInto(source, destination, w, h);

            byte[] actual = ReadNative(destination, w * h * 4);
            // 逐行比对：第 y 行应等于源第 y 行的前 w*4 字节，填充字节不应漏进目标
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w * 4; x++)
                {
                    Assert.Equal(pattern[y * paddedRowBytes + x], actual[y * w * 4 + x]);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(destination);
            Marshal.FreeHGlobal(source.GetPixels());
        }
    }

    [Fact]
    public void CopyInto_SmallerSource_LeavesRestZero()
    {
        const int dstW = 10, dstH = 6;
        using var source = MakeBitmap(4, 3);
        IntPtr destination = AllocZeroed(dstW, dstH);
        try
        {
            DibBufferPacker.CopyInto(source, destination, dstW, dstH);

            byte[] actual = ReadNative(destination, dstW * dstH * 4);
            byte[] sourceBytes = ReadPixels(source);

            // 左上 4x3 区域等于源
            for (int y = 0; y < 3; y++)
            {
                for (int x = 0; x < 4 * 4; x++)
                {
                    Assert.Equal(sourceBytes[y * 16 + x], actual[y * dstW * 4 + x]);
                }
            }
            // 右侧与下方保持全零（DIB 建好时的初值）
            for (int y = 0; y < 3; y++)
            {
                for (int x = 16; x < dstW * 4; x++)
                {
                    Assert.Equal(0, actual[y * dstW * 4 + x]);
                }
            }
            for (int y = 3; y < dstH; y++)
            {
                for (int x = 0; x < dstW * 4; x++)
                {
                    Assert.Equal(0, actual[y * dstW * 4 + x]);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(destination);
        }
    }

    [Fact]
    public void CopyInto_RgbaSource_ConvertsChannelOrder()
    {
        // 源是 RGBA8888，目标 DIB 是 BGRA —— 必须经 Skia 转换，否则红蓝互换。
        const int w = 5, h = 4;
        using var source = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        var known = new SKColor(0x11, 0x22, 0x33, 0xFF);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                source.SetPixel(x, y, known);
            }
        }

        IntPtr destination = AllocZeroed(w, h);
        try
        {
            DibBufferPacker.CopyInto(source, destination, w, h);

            byte[] actual = ReadNative(destination, w * h * 4);
            // Bgra8888 内存布局是 [B,G,R,A]
            Assert.Equal(known.Blue, actual[0]);
            Assert.Equal(known.Green, actual[1]);
            Assert.Equal(known.Red, actual[2]);
        }
        finally
        {
            Marshal.FreeHGlobal(destination);
        }
    }

    [Fact]
    public void CopyInto_NullDestination_Throws()
    {
        using var source = MakeBitmap(2, 2);

        Assert.Throws<ArgumentException>(() => DibBufferPacker.CopyInto(source, IntPtr.Zero, 2, 2));
    }

    [Fact]
    public void BuildDarkened_ScalesChannelsAndForcesOpaqueAlpha()
    {
        const int pixels = 4;
        IntPtr source = Marshal.AllocHGlobal(pixels * 4);
        IntPtr destination = Marshal.AllocHGlobal(pixels * 4);
        try
        {
            var input = new byte[]
            {
                0, 0, 0, 0,          // 全黑
                255, 255, 255, 0,    // 全白
                100, 50, 200, 7,     // 任意通道
                128, 64, 32, 200,    // 任意通道
            };
            Marshal.Copy(input, 0, source, input.Length);

            DibBufferPacker.BuildDarkened(source, destination, pixels);

            byte[] actual = ReadNative(destination, pixels * 4);
            const int k = DibBufferPacker.DarkenNumerator;
            for (int i = 0; i < pixels; i++)
            {
                int b = i * 4;
                Assert.Equal((byte)((input[b] * k) >> 8), actual[b]);
                Assert.Equal((byte)((input[b + 1] * k) >> 8), actual[b + 1]);
                Assert.Equal((byte)((input[b + 2] * k) >> 8), actual[b + 2]);
                Assert.Equal(255, actual[b + 3]); // alpha 一律不透明，避免叠图时半透明漏底
            }

            // 抽一个具体值确认压暗幅度：255 * 120 / 256 = 119
            Assert.Equal(119, actual[1 * 4]);
        }
        finally
        {
            Marshal.FreeHGlobal(source);
            Marshal.FreeHGlobal(destination);
        }
    }

    [Fact]
    public void BuildDarkened_ZeroPixels_DoesNothing()
    {
        IntPtr source = Marshal.AllocHGlobal(4);
        IntPtr destination = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(source, 0x11223344);
            Marshal.WriteInt32(destination, 0);
            DibBufferPacker.BuildDarkened(source, destination, 0);
            Assert.Equal(0, Marshal.ReadInt32(destination));
        }
        finally
        {
            Marshal.FreeHGlobal(source);
            Marshal.FreeHGlobal(destination);
        }
    }

    [Fact]
    public void BuildDarkened_InPlaceSourceEqualsDestination_IsStable()
    {
        // 源与目标同一块内存时不能产生错位读写
        const int pixels = 3;
        IntPtr buffer = Marshal.AllocHGlobal(pixels * 4);
        try
        {
            var input = new byte[] { 200, 180, 160, 0, 60, 40, 20, 0, 255, 0, 0, 0 };
            Marshal.Copy(input, 0, buffer, input.Length);

            DibBufferPacker.BuildDarkened(buffer, buffer, pixels);

            byte[] actual = ReadNative(buffer, pixels * 4);
            const int k = DibBufferPacker.DarkenNumerator;
            for (int i = 0; i < pixels; i++)
            {
                int b = i * 4;
                Assert.Equal((byte)((input[b] * k) >> 8), actual[b]);
                Assert.Equal(255, actual[b + 3]);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static SKBitmap MakeBitmap(int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, new SKColor((byte)(x * 13 + 1), (byte)(y * 7 + 2), (byte)((x ^ y) + 3), 255));
            }
        }
        return bitmap;
    }

    private static IntPtr AllocZeroed(int width, int height)
    {
        int length = width * height * 4;
        IntPtr pointer = Marshal.AllocHGlobal(length);
        Marshal.Copy(new byte[length], 0, pointer, length);
        return pointer;
    }

    private static byte[] ReadPixels(SKBitmap bitmap)
    {
        int length = bitmap.RowBytes * bitmap.Height;
        return ReadNative(bitmap.GetPixels(), length);
    }

    private static byte[] ReadNative(IntPtr pointer, int length)
    {
        var buffer = new byte[length];
        Marshal.Copy(pointer, buffer, 0, length);
        return buffer;
    }
}
