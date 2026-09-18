using ShawMuOCR.Core.Editor;
using ShawMuOCR.Core.OCR;
using SkiaSharp;
using Xunit;

namespace ShawMuOCR.Tests;

public class ImageDocumentTests
{
    private static OcrImage CreateTestImage(int width = 200, int height = 200)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        return new OcrImage(bitmap);
    }

    [Fact]
    public void ImageDocument_AddElements_RendersSuccessfully()
    {
        using var image = CreateTestImage(300, 200);
        using var doc = new ImageDocument(image);

        var rect = new RectangleElement { Left = 10, Top = 10, Right = 100, Bottom = 80 };
        var line = new LineElement { StartX = 10, StartY = 10, EndX = 150, EndY = 150 };
        var arrow = new ArrowElement { StartX = 20, StartY = 20, EndX = 180, EndY = 80 };
        var text = new TextElement { X = 50, Y = 50, Text = "Hello OCR" };

        doc.AddElement(rect);
        doc.AddElement(line);
        doc.AddElement(arrow);
        doc.AddElement(text);

        Assert.Equal(4, doc.Elements.Count);

        using var rendered = doc.RenderToBitmap();
        Assert.NotNull(rendered);
        Assert.Equal(300, rendered.Width);
        Assert.Equal(200, rendered.Height);
    }

    [Fact]
    public void ImageDocument_UndoRedo_WorksCorrectly()
    {
        using var image = CreateTestImage();
        using var doc = new ImageDocument(image);

        var rect = new RectangleElement { Left = 10, Top = 10, Right = 50, Bottom = 50 };

        Assert.False(doc.UndoRedo.CanUndo);
        Assert.False(doc.UndoRedo.CanRedo);

        doc.AddElement(rect);
        Assert.Single(doc.Elements);
        Assert.True(doc.UndoRedo.CanUndo);
        Assert.False(doc.UndoRedo.CanRedo);

        doc.UndoRedo.Undo();
        Assert.Empty(doc.Elements);
        Assert.False(doc.UndoRedo.CanUndo);
        Assert.True(doc.UndoRedo.CanRedo);

        doc.UndoRedo.Redo();
        Assert.Single(doc.Elements);
        Assert.True(doc.UndoRedo.CanUndo);
        Assert.False(doc.UndoRedo.CanRedo);
    }

    [Fact]
    public void ImageDocument_HitTest_And_Move_Works()
    {
        using var image = CreateTestImage();
        using var doc = new ImageDocument(image);

        var rect = new RectangleElement { Left = 20, Top = 20, Right = 100, Bottom = 100 };
        doc.AddElement(rect);

        // 点在矩形边框上
        var hit = doc.HitTest(new SKPoint(20, 50));
        Assert.NotNull(hit);
        Assert.Equal(rect.Id, hit.Id);

        // 移动图元
        doc.MoveElement(rect, 10, 10);
        Assert.Equal(30, rect.Left);
        Assert.Equal(30, rect.Top);

        // 撤销移动
        doc.UndoRedo.Undo();
        Assert.Equal(20, rect.Left);
        Assert.Equal(20, rect.Top);
    }

    [Fact]
    public void LineElement_HitTest_And_Clone_Works()
    {
        var line = new LineElement { StartX = 10, StartY = 10, EndX = 100, EndY = 10 };
        Assert.True(line.HitTest(new SKPoint(50, 12)));
        Assert.False(line.HitTest(new SKPoint(50, 30)));

        var clone = (LineElement)line.Clone();
        clone.MoveBy(5, 5);
        Assert.Equal(15, clone.StartX);
        Assert.Equal(15, clone.StartY);
        Assert.Equal(10, line.StartX); // 原对象保持不变
    }

    [Fact]
    public void ArrowElement_HitTest_And_BoundingBox_Works()
    {
        var arrow = new ArrowElement { StartX = 0, StartY = 0, EndX = 100, EndY = 0 };
        Assert.True(arrow.HitTest(new SKPoint(50, 1)));
        Assert.False(arrow.HitTest(new SKPoint(50, 50)));

        var bounds = arrow.BoundingBox;
        Assert.True(bounds.Right >= 100);
    }

    [Fact]
    public void TextElement_HitTest_And_Move_Works()
    {
        var text = new TextElement { X = 50, Y = 50, Text = "Hello", FontSize = 16 };
        var bounds = text.BoundingBox;
        Assert.True(bounds.Width > 0);
        Assert.True(bounds.Height > 0);

        Assert.True(text.HitTest(new SKPoint(bounds.MidX, bounds.MidY)));
        Assert.False(text.HitTest(new SKPoint(bounds.Right + 50, bounds.Bottom + 50)));

        text.MoveBy(10, 20);
        Assert.Equal(60, text.X);
        Assert.Equal(70, text.Y);
    }

    [Fact]
    public void ImageDocument_RemoveElement_UndoRedo()
    {
        using var image = CreateTestImage();
        using var doc = new ImageDocument(image);

        var rect = new RectangleElement { Left = 10, Top = 10, Right = 50, Bottom = 50 };
        doc.AddElement(rect);
        Assert.Single(doc.Elements);

        doc.RemoveElement(rect);
        Assert.Empty(doc.Elements);

        doc.UndoRedo.Undo();
        Assert.Single(doc.Elements);

        doc.UndoRedo.Redo();
        Assert.Empty(doc.Elements);
    }

    [Fact]
    public void AppSettings_DefaultHotkeys_AreCorrect()
    {
        var settings = new ShawMuOCR.Core.Settings.AppSettings();
        Assert.Equal("Alt+Z", settings.Hotkey);
        Assert.Equal("Alt+X", settings.CaptureImageHotkey);
        Assert.Equal("Alt+C", settings.AnnotateHotkey);

        string json = System.Text.Json.JsonSerializer.Serialize(settings);
        Assert.Contains("AnnotateHotkey", json);

        var deserialized = System.Text.Json.JsonSerializer.Deserialize<ShawMuOCR.Core.Settings.AppSettings>(json);
        Assert.NotNull(deserialized);
        Assert.Equal("Alt+Z", deserialized.Hotkey);
        Assert.Equal("Alt+X", deserialized.CaptureImageHotkey);
        Assert.Equal("Alt+C", deserialized.AnnotateHotkey);
    }

    [Fact]
    public void ImageDocument_RenderToBitmap_Scale_Works()
    {
        using var image = CreateTestImage(200, 100);
        using var doc = new ImageDocument(image);

        var rect = new RectangleElement { Left = 10, Top = 10, Right = 100, Bottom = 50 };
        doc.AddElement(rect);

        // 缩放 50%
        using var half = doc.RenderToBitmap(0.5f);
        Assert.Equal(100, half.Width);
        Assert.Equal(50, half.Height);

        // 缩放 200%
        using var doubleSize = doc.RenderToBitmap(2.0f);
        Assert.Equal(400, doubleSize.Width);
        Assert.Equal(200, doubleSize.Height);

        // 自定义分辨率
        using var custom = doc.RenderToBitmap(300, 150);
        Assert.Equal(300, custom.Width);
        Assert.Equal(150, custom.Height);
    }

    [Theory]
    [InlineData(ImageSaveFormat.Png, 100)]
    [InlineData(ImageSaveFormat.Jpeg, 90)]
    [InlineData(ImageSaveFormat.Jpeg, 50)]
    [InlineData(ImageSaveFormat.Webp, 90)]
    [InlineData(ImageSaveFormat.Webp, 60)]
    [InlineData(ImageSaveFormat.Bmp, 100)]
    public void ImageDocument_Encode_SupportsAllFormatsAndQualities(ImageSaveFormat format, int quality)
    {
        using var image = CreateTestImage(160, 120);
        using var doc = new ImageDocument(image);

        doc.AddElement(new RectangleElement { Left = 5, Top = 5, Right = 80, Bottom = 60 });
        doc.AddElement(new TextElement { X = 10, Y = 30, Text = "Format Test" });

        var options = new ImageExportOptions
        {
            Format = format,
            Quality = quality,
            Scale = 1.0f
        };

        byte[] bytes = doc.Encode(options);
        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);

        // 验证编码输出能否被 Skia 正确重新解码
        using var decoded = SKBitmap.Decode(bytes);
        Assert.NotNull(decoded);
        Assert.Equal(160, decoded.Width);
        Assert.Equal(120, decoded.Height);
    }

    [Fact]
    public void AppSettings_SaveImageSettings_DefaultsAndClone()
    {
        var settings = new ShawMuOCR.Core.Settings.AppSettings();
        Assert.Equal("PNG", settings.DefaultSaveFormat);
        Assert.Equal(90, settings.DefaultSaveQuality);
        Assert.Equal(100, settings.DefaultSaveScale);
        Assert.Null(settings.LastSaveDirectory);

        settings.DefaultSaveFormat = "WEBP";
        settings.DefaultSaveQuality = 85;
        settings.DefaultSaveScale = 150;
        settings.LastSaveDirectory = @"C:\Users\Public\Pictures";

        var cloned = settings.Clone();
        Assert.Equal("WEBP", cloned.DefaultSaveFormat);
        Assert.Equal(85, cloned.DefaultSaveQuality);
        Assert.Equal(150, cloned.DefaultSaveScale);
        Assert.Equal(@"C:\Users\Public\Pictures", cloned.LastSaveDirectory);
    }
}
