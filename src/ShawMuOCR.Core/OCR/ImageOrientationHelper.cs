using System;
using System.IO;
using ShawMuOCR.Core.Batch;
using SkiaSharp;

namespace ShawMuOCR.Core.OCR;

/// <summary>
/// 图像与 PDF 页面旋转变换及 EXIF 方向自动校正工具
/// </summary>
public static class ImageOrientationHelper
{
    /// <summary>
    /// 将 SKBitmap 按照指定角度进行顺时针旋转
    /// </summary>
    /// <param name="bitmap">源图像位图</param>
    /// <param name="degrees">顺时针旋转角度（0, 90, 180, 270）</param>
    /// <returns>旋转后的全新 SKBitmap 实例（调用方负责释放）</returns>
    public static SKBitmap Rotate(SKBitmap bitmap, int degrees)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        degrees = (degrees % 360 + 360) % 360;
        if (degrees == 0)
        {
            if (bitmap.ColorType == SKColorType.Bgra8888 && bitmap.AlphaType == SKAlphaType.Premul)
            {
                return bitmap.Copy();
            }
            var copy = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var c = new SKCanvas(copy);
            c.DrawBitmap(bitmap, 0, 0, SKSamplingOptions.Default);
            return copy;
        }

        bool swapDimensions = degrees is 90 or 270;
        int targetWidth = swapDimensions ? bitmap.Height : bitmap.Width;
        int targetHeight = swapDimensions ? bitmap.Width : bitmap.Height;

        var target = new SKBitmap(new SKImageInfo(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(target);
        canvas.Clear(SKColors.Transparent);

        canvas.Translate(targetWidth / 2f, targetHeight / 2f);
        canvas.RotateDegrees(degrees);
        canvas.Translate(-bitmap.Width / 2f, -bitmap.Height / 2f);
        canvas.DrawBitmap(bitmap, 0, 0, SKSamplingOptions.Default);

        return target;
    }

    /// <summary>
    /// 解码图片并根据 EXIF 拍摄朝向或指定旋转模式进行校正转正
    /// </summary>
    public static SKBitmap DecodeAndOrient(byte[] bytes, ImageRotationMode rotationMode = ImageRotationMode.Auto)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        int exifDegrees = 0;
        if (rotationMode == ImageRotationMode.Auto)
        {
            try
            {
                using var data = SKData.CreateCopy(bytes);
                using var codec = SKCodec.Create(data);
                if (codec != null)
                {
                    exifDegrees = codec.EncodedOrigin switch
                    {
                        SKEncodedOrigin.RightTop => 90,
                        SKEncodedOrigin.BottomRight => 180,
                        SKEncodedOrigin.LeftBottom => 270,
                        _ => 0
                    };
                }
            }
            catch { }
        }

        using var rawBitmap = SKBitmap.Decode(bytes);
        if (rawBitmap == null) return null!;

        int finalDegrees = rotationMode switch
        {
            ImageRotationMode.Auto => exifDegrees,
            ImageRotationMode.Rotate90 => 90,
            ImageRotationMode.Rotate180 => 180,
            ImageRotationMode.Rotate270 => 270,
            _ => 0
        };

        if (finalDegrees == 0)
        {
            return rawBitmap.Copy();
        }

        return Rotate(rawBitmap, finalDegrees);
    }

    /// <summary>
    /// 从文件流解码图片并根据 EXIF 拍摄朝向或指定旋转模式进行校正转正
    /// </summary>
    public static SKBitmap DecodeAndOrient(Stream stream, ImageRotationMode rotationMode = ImageRotationMode.Auto)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return DecodeAndOrient(ms.ToArray(), rotationMode);
    }

    /// <summary>
    /// 结合 PDF 页面旋转角（/Rotate）与指定的旋转模式计算最终需要旋转的顺时针角度
    /// </summary>
    /// <param name="pageRotationDegrees">PDF 页面原生标记的旋转角（如 0, 90, 180, 270）</param>
    /// <param name="mode">用户指定的旋转策略</param>
    public static int ResolvePdfRotation(int pageRotationDegrees, ImageRotationMode mode)
    {
        int normPage = (pageRotationDegrees % 360 + 360) % 360;
        return mode switch
        {
            ImageRotationMode.Auto => normPage,
            ImageRotationMode.None => 0,
            ImageRotationMode.Rotate90 => (normPage + 90) % 360,
            ImageRotationMode.Rotate180 => (normPage + 180) % 360,
            ImageRotationMode.Rotate270 => (normPage + 270) % 360,
            _ => normPage
        };
    }
}
