using System;
using System.IO;
using AndroidContext = global::Android.Content.Context;
using AndroidUri = global::Android.Net.Uri;
using AndroidBitmap = global::Android.Graphics.Bitmap;
using AndroidBitmapFactory = global::Android.Graphics.BitmapFactory;
using Sdcb.SimdPaddleOCR;

namespace ShawMuOCR.Android.Platform;

/// <summary>
/// 稳定高效的 Android 图像解码器
/// 支持硬件级 InSampleSize 下采样与高质量等比例缩放，将超大图片自动平滑收敛至 MaxSideLen（默认 1280），
/// 实现毫秒级极速 OCR 推理，彻底规避内存溢出与大小核性能倒退
/// </summary>
public static class AndroidImageDecoder
{
    /// <summary>
    /// 从 SAF Uri 纯流式解码到托管内存像素数组，带智能下采样与长边约束
    /// </summary>
    public static ImageBuffer DecodeFromUri(AndroidContext context, AndroidUri uri, int maxSideLen = 1280)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(uri);

        AndroidCrashLogger.Log($"[IMAGE DECODER] 正在通过 ContentResolver 打开 SAF 流: {uri} (目标长边限制: {maxSideLen})");

        AndroidBitmap? bitmap = null;
        try
        {
            int sampleSize = 1;
            if (maxSideLen > 0)
            {
                try
                {
                    using var probeStream = context.ContentResolver!.OpenInputStream(uri);
                    if (probeStream != null)
                    {
                        var probeOptions = new AndroidBitmapFactory.Options { InJustDecodeBounds = true };
                        AndroidBitmapFactory.DecodeStream(probeStream, null, probeOptions);
                        int origLongSide = Math.Max(probeOptions.OutWidth, probeOptions.OutHeight);
                        while (origLongSide / (sampleSize * 2) >= maxSideLen)
                        {
                            sampleSize *= 2;
                        }

                        if (sampleSize > 1)
                        {
                            AndroidCrashLogger.Log($"[IMAGE DECODER] 探测到大图原始尺寸: {probeOptions.OutWidth}x{probeOptions.OutHeight}，启用硬件级 InSampleSize={sampleSize} 极速下采样");
                        }
                    }
                }
                catch (Exception probeEx)
                {
                    AndroidCrashLogger.Log($"[IMAGE DECODER] 探测原图尺寸失败，将采用标准单倍解码: {probeEx.Message}");
                }
            }

            using (var stream = context.ContentResolver!.OpenInputStream(uri)
                ?? throw new InvalidOperationException($"无法打开 SAF 资源: {uri}"))
            {
                var options = new AndroidBitmapFactory.Options
                {
                    InPreferredConfig = AndroidBitmap.Config.Argb8888,
                    InPremultiplied = false,
                    InMutable = true,
                    InSampleSize = sampleSize
                };

                bitmap = AndroidBitmapFactory.DecodeStream(stream, null, options)
                    ?? throw new InvalidOperationException("BitmapFactory 解码流返回 null，图片格式可能不被支持");
            }

            AndroidCrashLogger.Log($"[IMAGE DECODER] 解码成功: {bitmap.Width}x{bitmap.Height}, Config={bitmap.GetConfig()}, ByteCount={bitmap.ByteCount}");
            return ConvertBitmapToBuffer(bitmap, maxSideLen);
        }
        catch (Exception ex)
        {
            AndroidCrashLogger.LogException(ex, $"[IMAGE DECODER] 解码 URI 失败: {uri}");
            throw;
        }
        finally
        {
            bitmap?.Recycle();
            bitmap?.Dispose();
        }
    }

    /// <summary>
    /// 从输入流直接解码
    /// </summary>
    public static ImageBuffer DecodeFromStream(Stream stream, int maxSideLen = 1280)
    {
        ArgumentNullException.ThrowIfNull(stream);

        AndroidBitmap? bitmap = null;
        try
        {
            var options = new AndroidBitmapFactory.Options
            {
                InPreferredConfig = AndroidBitmap.Config.Argb8888,
                InPremultiplied = false,
                InMutable = true
            };

            bitmap = AndroidBitmapFactory.DecodeStream(stream, null, options)
                ?? throw new InvalidOperationException("BitmapFactory 解码流失败");

            return ConvertBitmapToBuffer(bitmap, maxSideLen);
        }
        finally
        {
            bitmap?.Recycle();
            bitmap?.Dispose();
        }
    }

    /// <summary>
    /// 将 Android 原生 Bitmap 提取到紧凑 RGBA32 托管字节内存
    /// 支持按长边约束等比例高质量缩放，使用 GetPixels 彻底杜绝纯黑缺陷
    /// </summary>
    public static ImageBuffer ConvertBitmapToBuffer(AndroidBitmap bitmap, int maxSideLen = 1280)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        AndroidBitmap effectiveBitmap = bitmap;
        bool needRecycle = false;

        try
        {
            int longSide = Math.Max(bitmap.Width, bitmap.Height);
            if (maxSideLen > 0 && longSide > maxSideLen)
            {
                float scale = (float)maxSideLen / longSide;
                int targetW = (int)Math.Round(bitmap.Width * scale);
                int targetH = (int)Math.Round(bitmap.Height * scale);
                targetW = Math.Max(2, (targetW / 2) * 2);
                targetH = Math.Max(2, (targetH / 2) * 2);

                AndroidCrashLogger.Log($"[IMAGE CONVERTER] 图片长边超出限制 ({maxSideLen})，执行等比例平滑缩放: {bitmap.Width}x{bitmap.Height} -> {targetW}x{targetH}");
                effectiveBitmap = AndroidBitmap.CreateScaledBitmap(bitmap, targetW, targetH, true)
                    ?? bitmap;
                if (!ReferenceEquals(effectiveBitmap, bitmap))
                {
                    needRecycle = true;
                }
            }

            int width = effectiveBitmap.Width;
            int height = effectiveBitmap.Height;
            int totalPixels = width * height;

            AndroidCrashLogger.Log($"[IMAGE CONVERTER] 开始提取位图像素: {width}x{height} (总像素: {totalPixels})");

            int[] intPixels = new int[totalPixels];
            effectiveBitmap.GetPixels(intPixels, 0, width, 0, 0, width, height);

            byte[] rgbaBytes = new byte[totalPixels * 4];
            long nonZeroCount = 0;

            for (int i = 0; i < totalPixels; i++)
            {
                int p = intPixels[i];
                int idx = i * 4;

                byte a = (byte)((p >> 24) & 0xFF);
                byte r = (byte)((p >> 16) & 0xFF);
                byte g = (byte)((p >> 8) & 0xFF);
                byte b = (byte)(p & 0xFF);

                if (a == 0)
                {
                    a = 255;
                }

                rgbaBytes[idx] = r;
                rgbaBytes[idx + 1] = g;
                rgbaBytes[idx + 2] = b;
                rgbaBytes[idx + 3] = a;

                if (r > 0 || g > 0 || b > 0)
                {
                    nonZeroCount++;
                }
            }

            AndroidCrashLogger.Log($"[IMAGE CONVERTER] 像素提取完毕: 有效彩色/非黑像素占比: {nonZeroCount}/{totalPixels} ({(double)nonZeroCount / totalPixels:P1})");

            return new ImageBuffer(rgbaBytes, width, height, ImagePixelFormat.Rgba32);
        }
        finally
        {
            if (needRecycle)
            {
                effectiveBitmap.Recycle();
                effectiveBitmap.Dispose();
            }
        }
    }
}
