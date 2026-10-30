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

        string json = System.Text.Json.JsonSerializer.Serialize(settings, ShawMuOCR.Core.Settings.AppSettingsJsonContext.Default.AppSettings);
        Assert.Contains("Annotate", json);

        var deserialized = System.Text.Json.JsonSerializer.Deserialize(json, ShawMuOCR.Core.Settings.AppSettingsJsonContext.Default.AppSettings);
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

    [Fact]
    public void PenElement_HitTest_MoveBy_And_Resize_Work()
    {
        var pen = new PenElement { Color = SKColors.Red, StrokeWidth = 4f };
        pen.Points.Add(new SKPoint(10, 10));
        pen.Points.Add(new SKPoint(30, 30));
        pen.Points.Add(new SKPoint(50, 10));

        Assert.True(pen.HitTest(new SKPoint(20, 20)));
        Assert.False(pen.HitTest(new SKPoint(100, 100)));

        var bounds = pen.BoundingBox;
        Assert.True(bounds.Width > 0);

        pen.MoveBy(10, 5);
        Assert.Equal(20, pen.Points[0].X);
        Assert.Equal(15, pen.Points[0].Y);

        pen.Resize(new SKRect(0, 0, 100, 50));
        Assert.True(pen.BoundingBox.Width > 0);

        var cloned = (PenElement)pen.Clone();
        Assert.Equal(pen.Points.Count, cloned.Points.Count);
        Assert.Equal(pen.Color, cloned.Color);
    }

    [Fact]
    public void MosaicElement_CreatePatch_And_Render_Work()
    {
        using var image = CreateTestImage(200, 200);
        using var doc = new ImageDocument(image);

        var bounds = new SKRect(20, 20, 80, 80);
        var patch = MosaicElement.CreatePixelatedPatch(image.Bitmap, bounds, 8);
        Assert.NotNull(patch);

        var mosaic = new MosaicElement
        {
            Bounds = bounds,
            StrokeWidth = 10f,
            PixelatedPatch = patch
        };
        mosaic.Points.Add(new SKPoint(30, 30));
        mosaic.Points.Add(new SKPoint(70, 70));

        doc.AddElement(mosaic);
        Assert.Single(doc.Elements);

        using var rendered = doc.RenderToBitmap();
        Assert.NotNull(rendered);

        var hit = doc.HitTest(new SKPoint(50, 50));
        Assert.NotNull(hit);
        Assert.Equal(mosaic.Id, hit.Id);
    }

    [Fact]
    public void ImageDocument_ClearElements_And_UndoRedo_Work()
    {
        using var image = CreateTestImage();
        using var doc = new ImageDocument(image);

        doc.AddElement(new RectangleElement { Left = 10, Top = 10, Right = 50, Bottom = 50 });
        doc.AddElement(new LineElement { StartX = 0, StartY = 0, EndX = 100, EndY = 100 });
        doc.AddElement(new TextElement { X = 20, Y = 20, Text = "Test" });
        Assert.Equal(3, doc.Elements.Count);

        // 清空所有元素 (还原)
        doc.ClearElements();
        Assert.Empty(doc.Elements);
        Assert.True(doc.UndoRedo.CanUndo);

        // 撤销清空
        doc.UndoRedo.Undo();
        Assert.Equal(3, doc.Elements.Count);

        // 重做清空
        doc.UndoRedo.Redo();
        Assert.Empty(doc.Elements);
    }

    [Fact]
    public void Element_Rotation_And_HitTest_Work()
    {
        var rect = new RectangleElement
        {
            Left = 10,
            Top = 10,
            Right = 90,
            Bottom = 90,
            Rotation = 45f // 旋转 45 度
        };

        Assert.Equal(45f, rect.Rotation);

        // 点在未旋转的右上角附近 (原 (90,10)，在45度旋转后不再命中)
        // 验证 HitTest 支持旋转逆向变换
        Assert.False(rect.HitTest(new SKPoint(90, 10)));

        var clone = (RectangleElement)rect.Clone();
        Assert.Equal(45f, clone.Rotation);
    }

    [Fact]
    public void TextElement_SystemFont_NoShadow_RendersSuccessfully()
    {
        using var image = CreateTestImage(200, 100);
        using var doc = new ImageDocument(image);

        var text = new TextElement
        {
            X = 20,
            Y = 20,
            Text = "系统字体无阴影",
            Color = SKColors.Blue,
            FontSize = 18f
        };

        doc.AddElement(text);
        using var rendered = doc.RenderToBitmap();
        Assert.NotNull(rendered);
    }

    [Fact]
    public void ImageDocument_Crop_CropsImageAndElementsCorrectly()
    {
        using var image = CreateTestImage(200, 200);
        using var doc = new ImageDocument(image);

        var rect = new RectangleElement { Left = 50, Top = 50, Right = 100, Bottom = 100 };
        doc.AddElement(rect);

        // 裁切区域：从 (20, 20) 裁切 100x100 像素
        doc.Crop(new SKRectI(20, 20, 120, 120));

        Assert.Equal(100, doc.OriginalImage.Width);
        Assert.Equal(100, doc.OriginalImage.Height);
        Assert.Single(doc.Elements);

        // 原矩形坐标从 (50, 50, 100, 100) 偏移变为 (30, 30, 80, 80)
        var shiftedRect = (RectangleElement)doc.Elements[0];
        Assert.Equal(30, shiftedRect.Left);
        Assert.Equal(30, shiftedRect.Top);
        Assert.Equal(80, shiftedRect.Right);
        Assert.Equal(80, shiftedRect.Bottom);
    }

    [Fact]
    public void ImageDocument_Crop_UndoRedo_RestoresOriginalDimensionsAndElements()
    {
        using var image = CreateTestImage(300, 200);
        using var doc = new ImageDocument(image);

        var rect = new RectangleElement { Left = 60, Top = 60, Right = 120, Bottom = 120 };
        doc.AddElement(rect);

        // 裁切
        doc.Crop(new SKRectI(30, 30, 180, 150));
        Assert.Equal(150, doc.OriginalImage.Width);
        Assert.Equal(120, doc.OriginalImage.Height);
        Assert.True(doc.UndoRedo.CanUndo);

        // 撤销裁切：恢复原始 300x200 与原始坐标
        doc.UndoRedo.Undo();
        Assert.Equal(300, doc.OriginalImage.Width);
        Assert.Equal(200, doc.OriginalImage.Height);
        var restoredRect = (RectangleElement)doc.Elements[0];
        Assert.Equal(60, restoredRect.Left);
        Assert.Equal(60, restoredRect.Top);

        // 重做裁切
        doc.UndoRedo.Redo();
        Assert.Equal(150, doc.OriginalImage.Width);
        Assert.Equal(120, doc.OriginalImage.Height);
    }

    [Fact]
    public void WatermarkElement_Render_Clone_And_HitTest_Works()
    {
        var wm = new WatermarkElement
        {
            Text = "绝密资料",
            FontSize = 28f,
            Opacity = 0.3f,
            IsTiled = true,
            ImageWidth = 400,
            ImageHeight = 300,
            Rotation = -30f
        };

        Assert.Equal(400, wm.BoundingBox.Width);
        Assert.Equal(300, wm.BoundingBox.Height);

        // 克隆测试
        var clone = (WatermarkElement)wm.Clone();
        Assert.Equal(wm.Text, clone.Text);
        Assert.Equal(wm.IsTiled, clone.IsTiled);
        Assert.Equal(wm.Opacity, clone.Opacity);

        // 渲染测试
        using var bitmap = new SKBitmap(400, 300);
        using var canvas = new SKCanvas(bitmap);
        wm.Render(canvas);

        // 单条居中模式
        wm.IsTiled = false;
        wm.Render(canvas);
        Assert.True(wm.HitTest(new SKPoint(200, 150)));
    }

    [Fact]
    public void WatermarkElement_ProportionalScalingAndDensity_AdaptsToImageDimensions()
    {
        // 1. 小图 (400x300)
        var smallWm = new WatermarkElement
        {
            Text = "内部资料",
            FontSize = 0f,
            IsTiled = true,
            ImageWidth = 400,
            ImageHeight = 300
        };
        float smallSize = smallWm.GetEffectiveFontSize();
        Assert.True(smallSize >= 14f && smallSize <= 20f);

        // 2. 标清图 (1920x1080)
        var fhdWm = new WatermarkElement
        {
            Text = "内部资料",
            FontSize = 0f,
            IsTiled = true,
            ImageWidth = 1920,
            ImageHeight = 1080
        };
        float fhdSize = fhdWm.GetEffectiveFontSize();
        Assert.True(fhdSize > smallSize);
        Assert.True(fhdSize >= 40f && fhdSize <= 55f);

        // 3. 4K 超清图 (3840x2160)
        var uhdWm = new WatermarkElement
        {
            Text = "内部资料",
            FontSize = 0f,
            IsTiled = true,
            ImageWidth = 3840,
            ImageHeight = 2160
        };
        float uhdSize = uhdWm.GetEffectiveFontSize();
        Assert.True(uhdSize > fhdSize);
        Assert.True(uhdSize >= 85f && uhdSize <= 110f);

        // 4. 渲染验证：不同分辨率全图均可正常完成渲染且不抛异常
        using var smallBmp = new SKBitmap(400, 300);
        using var smallCanvas = new SKCanvas(smallBmp);
        smallWm.Render(smallCanvas);

        using var fhdBmp = new SKBitmap(1920, 1080);
        using var fhdCanvas = new SKCanvas(fhdBmp);
        fhdWm.Render(fhdCanvas);

        // 5. 单条居中模式按比例缩放验证
        fhdWm.IsTiled = false;
        float singleSize = fhdWm.GetEffectiveFontSize();
        Assert.True(singleSize > fhdSize); // 单条醒目水印字号大于平铺字号
        fhdWm.Render(fhdCanvas);
        Assert.True(fhdWm.HitTest(new SKPoint(960, 540)));
    }

    [Fact]
    public void ImageDocument_HitTest_With_Radius_Works()
    {
        using var image = CreateTestImage(200, 200);
        using var doc = new ImageDocument(image);

        var line = new LineElement { StartX = 10, StartY = 10, EndX = 100, EndY = 10, StrokeWidth = 2f };
        doc.AddElement(line);

        // 距离直线 10px 处，常规 0 半径 HitTest 应未命中
        Assert.Null(doc.HitTest(new SKPoint(50, 20), radius: 0f));

        // 带有 15px 橡皮擦半径的 HitTest 能够探测到该图元
        var hit = doc.HitTest(new SKPoint(50, 20), radius: 15f);
        Assert.NotNull(hit);
        Assert.Same(line, hit);
    }

    [Fact]
    public void EllipseElement_Render_Clone_And_HitTest_Works()
    {
        var ellipse = new EllipseElement
        {
            Left = 50,
            Top = 50,
            Right = 150,
            Bottom = 150,
            StrokeWidth = 4f
        };

        Assert.Equal(100, ellipse.BoundingBox.Width);
        Assert.Equal(100, ellipse.BoundingBox.Height);

        // 圆周边缘命中
        Assert.True(ellipse.HitTest(new SKPoint(150, 100)));
        // 圆心空心处不命中
        Assert.False(ellipse.HitTest(new SKPoint(100, 100)));

        // 克隆测试
        var clone = (EllipseElement)ellipse.Clone();
        Assert.Equal(ellipse.BoundingBox.Width, clone.BoundingBox.Width);

        // 渲染测试
        using var bitmap = new SKBitmap(200, 200);
        using var canvas = new SKCanvas(bitmap);
        ellipse.Render(canvas);
    }

    [Fact]
    public void EraserElement_Render_In_ImageDocument_Works()
    {
        using var image = CreateTestImage(200, 200);
        using var doc = new ImageDocument(image);

        var rect = new RectangleElement { Left = 20, Top = 20, Right = 180, Bottom = 180, StrokeWidth = 10f };
        doc.AddElement(rect);

        var eraser = new EraserElement { StrokeWidth = 20f };
        eraser.Points.Add(new SKPoint(100, 20));
        eraser.Points.Add(new SKPoint(100, 180));
        doc.AddElement(eraser);

        Assert.Equal(2, doc.Elements.Count);

        // 渲染图层测试 (验证 SaveLayer 与 Clear 混合模式执行正常)
        using var rendered = doc.RenderToBitmap();
        Assert.NotNull(rendered);
        Assert.Equal(200, rendered.Width);
        Assert.Equal(200, rendered.Height);
    }

    [Fact]
    public void MosaicElement_ContinuousStroke_MultiPoints_Render_Works()
    {
        using var image = CreateTestImage(300, 300);
        using var doc = new ImageDocument(image);

        var mosaic = new MosaicElement { StrokeWidth = 12f };
        // 模拟连续涂抹轨迹的多个点
        mosaic.Points.Add(new SKPoint(50, 50));
        mosaic.Points.Add(new SKPoint(80, 80));
        mosaic.Points.Add(new SKPoint(120, 100));
        mosaic.Points.Add(new SKPoint(160, 150));

        var box = mosaic.BoundingBox;
        Assert.True(box.Width > 110f);
        Assert.True(box.Height > 100f);

        mosaic.Bounds = box;
        mosaic.PixelatedPatch = MosaicElement.CreatePixelatedPatch(image.Bitmap, box, 8);
        Assert.NotNull(mosaic.PixelatedPatch);

        doc.AddElement(mosaic);
        Assert.Single(doc.Elements);

        using var rendered = doc.RenderToBitmap();
        Assert.NotNull(rendered);
        Assert.Equal(300, rendered.Width);
        Assert.Equal(300, rendered.Height);

        // 测试撤销重做
        doc.UndoRedo.Undo();
        Assert.Empty(doc.Elements);
        doc.UndoRedo.Redo();
        Assert.Single(doc.Elements);
    }

    [Fact]
    public void ImageDocument_RestoreFullSnapshot_ElementsOffsetAndUndoWorks()
    {
        // 模拟 100x100 的局部截图，选区在全屏中的物理偏移量为 (200, 150)
        using var cropImage = CreateTestImage(100, 100);
        using var fullImage = CreateTestImage(800, 600);
        using var doc = new ImageDocument(cropImage);

        var rect = new RectangleElement { Left = 10, Top = 20, Right = 50, Bottom = 60 };
        var line = new LineElement { StartX = 5, StartY = 5, EndX = 40, EndY = 40 };
        doc.AddElement(rect);
        doc.AddElement(line);

        float dx = 200f;
        float dy = 150f;

        var restoredElements = new List<IImageElement>();
        foreach (var elem in doc.Elements)
        {
            var cloned = elem.Clone();
            cloned.MoveBy(dx, dy);
            restoredElements.Add(cloned);
        }

        using var fullImageClone = fullImage.Clone();
        doc.UndoRedo.Execute(new CropDocumentCommand(doc, fullImageClone, restoredElements));

        // 验证底图尺寸已扩展为全屏快照
        Assert.Equal(800, doc.OriginalImage.Width);
        Assert.Equal(600, doc.OriginalImage.Height);

        // 验证要素坐标已精确平移
        var newRect = (RectangleElement)doc.Elements[0];
        Assert.Equal(210f, newRect.Left);
        Assert.Equal(170f, newRect.Top);
        Assert.Equal(250f, newRect.Right);
        Assert.Equal(210f, newRect.Bottom);

        var newLine = (LineElement)doc.Elements[1];
        Assert.Equal(205f, newLine.StartX);
        Assert.Equal(155f, newLine.StartY);
        Assert.Equal(240f, newLine.EndX);
        Assert.Equal(190f, newLine.EndY);

        // 验证按 Ctrl+Z 撤销后无缝回退至局部小图与原始坐标
        doc.UndoRedo.Undo();
        Assert.Equal(100, doc.OriginalImage.Width);
        Assert.Equal(100, doc.OriginalImage.Height);

        var oldRect = (RectangleElement)doc.Elements[0];
        Assert.Equal(10f, oldRect.Left);
        Assert.Equal(20f, oldRect.Top);
    }

    [Fact]
    public void TextElement_Resize_CanScaleBeyond160_WithoutUpperLimit()
    {
        var textElem = new TextElement
        {
            Text = "ShawMuOCR Test Scale",
            FontSize = 100f,
            X = 0,
            Y = 0
        };

        var initialBounds = textElem.BoundingBox;
        Assert.True(initialBounds.Width > 0);

        // 放大 3 倍，使得 fontSize 期望达到 ~300f（此前硬编码限制为 160f）
        var expandedBounds = new SkiaSharp.SKRect(
            initialBounds.Left,
            initialBounds.Top,
            initialBounds.Left + initialBounds.Width * 3f,
            initialBounds.Top + initialBounds.Height * 3f);

        textElem.Resize(expandedBounds);

        // 验证字号已突破 160f 限制，且正常按比例缩放
        Assert.True(textElem.FontSize >= 250f, $"Actual font size {textElem.FontSize} should exceed 250f without being clamped to 160f.");
    }
}
