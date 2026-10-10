using System;
using System.IO;
using ShawMuOCR.Core.OCR;
using SkiaSharp;
using Xunit;

namespace ShawMuOCR.Tests;

public class ImageOrientationHelperTests
{
    [Fact]
    public void Rotate_0Degrees_PreservesDimensionsAndNormalizesFormat()
    {
        using var src = new SKBitmap(100, 50, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var rotated = ImageOrientationHelper.Rotate(src, 0);

        Assert.NotNull(rotated);
        Assert.Equal(100, rotated.Width);
        Assert.Equal(50, rotated.Height);
        Assert.Equal(SKColorType.Bgra8888, rotated.ColorType);
        Assert.Equal(SKAlphaType.Premul, rotated.AlphaType);
    }

    [Fact]
    public void Rotate_90Degrees_SwapsDimensions()
    {
        using var src = new SKBitmap(120, 60, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var rotated = ImageOrientationHelper.Rotate(src, 90);

        Assert.NotNull(rotated);
        Assert.Equal(60, rotated.Width);
        Assert.Equal(120, rotated.Height);
        Assert.Equal(SKColorType.Bgra8888, rotated.ColorType);
    }

    [Fact]
    public void Rotate_180Degrees_PreservesDimensions()
    {
        using var src = new SKBitmap(120, 60, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var rotated = ImageOrientationHelper.Rotate(src, 180);

        Assert.NotNull(rotated);
        Assert.Equal(120, rotated.Width);
        Assert.Equal(60, rotated.Height);
    }

    [Fact]
    public void Rotate_270Degrees_SwapsDimensions()
    {
        using var src = new SKBitmap(120, 60, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var rotated = ImageOrientationHelper.Rotate(src, 270);

        Assert.NotNull(rotated);
        Assert.Equal(60, rotated.Width);
        Assert.Equal(120, rotated.Height);
    }

    [Fact]
    public void Rotate_NegativeDegrees_NormalizesProperly()
    {
        using var src = new SKBitmap(100, 50, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var rotated = ImageOrientationHelper.Rotate(src, -90); // 相当于 270 度

        Assert.NotNull(rotated);
        Assert.Equal(50, rotated.Width);
        Assert.Equal(100, rotated.Height);
    }

    [Theory]
    [InlineData(0, ImageRotationMode.Auto, 0)]
    [InlineData(90, ImageRotationMode.Auto, 90)]
    [InlineData(270, ImageRotationMode.Auto, 270)]
    [InlineData(90, ImageRotationMode.None, 0)]
    [InlineData(0, ImageRotationMode.Rotate90, 90)]
    [InlineData(90, ImageRotationMode.Rotate90, 180)]
    [InlineData(90, ImageRotationMode.Rotate180, 270)]
    [InlineData(90, ImageRotationMode.Rotate270, 0)]
    [InlineData(180, ImageRotationMode.Rotate180, 0)]
    public void ResolvePdfRotation_CalculatesCorrectAngle(int pageRotation, ImageRotationMode mode, int expected)
    {
        int actual = ImageOrientationHelper.ResolvePdfRotation(pageRotation, mode);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void DecodeAndOrient_RotatesEncodedImageStreamCorrectly()
    {
        using var bmp = new SKBitmap(80, 40, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var ms = new MemoryStream(data.ToArray());

        using var decoded90 = ImageOrientationHelper.DecodeAndOrient(ms, ImageRotationMode.Rotate90);
        Assert.NotNull(decoded90);
        Assert.Equal(40, decoded90.Width);
        Assert.Equal(80, decoded90.Height);
        Assert.Equal(SKColorType.Bgra8888, decoded90.ColorType);

        using var decodedNone = ImageOrientationHelper.DecodeAndOrient(data.ToArray(), ImageRotationMode.None);
        Assert.NotNull(decodedNone);
        Assert.Equal(80, decodedNone.Width);
        Assert.Equal(40, decodedNone.Height);
    }
}
