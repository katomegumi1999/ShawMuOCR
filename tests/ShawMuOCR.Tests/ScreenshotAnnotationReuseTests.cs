using System.IO;
using ShawMuOCR.Core.Editor;
using ShawMuOCR.Core.OCR;
using SkiaSharp;
using Xunit;

namespace ShawMuOCR.Tests;

public class ScreenshotAnnotationReuseTests
{
    [Fact]
    public void StandaloneScreenshotEditorWindow_XAML_ExplicitlyDisallowsFileDrop()
    {
        // 验证用户明确要求的红线：独立的截图标注窗口不要支持直接拖拽文件进来编辑
        string xamlPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Views", "ScreenshotEditorWindow.axaml");
        if (File.Exists(xamlPath))
        {
            string xaml = File.ReadAllText(xamlPath);
            Assert.Contains("DragDrop.AllowDrop=\"False\"", xaml);
        }
    }

    [Fact]
    public void ImageDocument_AddsAndRendersAnnotations()
    {
        using var srcBmp = new SKBitmap(200, 200);
        using var doc = new ImageDocument(new OcrImage(srcBmp));

        Assert.Empty(doc.Elements);

        doc.AddElement(new RectangleElement
        {
            Left = 10,
            Top = 10,
            Right = 80,
            Bottom = 80,
            Color = SKColors.Red,
            StrokeWidth = 4f
        });

        Assert.Single(doc.Elements);

        using var rendered = doc.RenderToBitmap();
        Assert.NotNull(rendered?.Bitmap);
        Assert.Equal(200, rendered.Bitmap.Width);
        Assert.Equal(200, rendered.Bitmap.Height);

        // 撤销测试
        Assert.True(doc.UndoRedo.CanUndo);
        doc.UndoRedo.Undo();
        Assert.Empty(doc.Elements);
    }

    [Fact]
    public void CapturePageView_XAML_UsesEmbeddedScreenshotEditorControl_AndRemovesRightToolbar()
    {
        string xamlPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Views", "Pages", "CapturePageView.axaml");
        if (File.Exists(xamlPath))
        {
            string xaml = File.ReadAllText(xamlPath);
            Assert.Contains("ScreenshotEditorControl", xaml);
            Assert.Contains("IsEmbedded=\"True\"", xaml);
            Assert.DoesNotContain("Width=\"136\"", xaml);
            Assert.DoesNotContain("图像操作", xaml);
        }

        string ctrlXamlPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Views", "Components", "ScreenshotEditorControl.axaml");
        if (File.Exists(ctrlXamlPath))
        {
            string ctrlXaml = File.ReadAllText(ctrlXamlPath);
            Assert.Contains("Name=\"OpenImageBtn\"", ctrlXaml);
            Assert.Contains("Name=\"RestoreFullSnapshotBtn\"", ctrlXaml);
            Assert.Contains("Name=\"PopoutEditorBtn\"", ctrlXaml);
            Assert.Contains("Name=\"ClearImageBtn\"", ctrlXaml);
        }

        string mainXamlPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Views", "MainWindow.axaml");
        if (File.Exists(mainXamlPath))
        {
            string mainXaml = File.ReadAllText(mainXamlPath);
            Assert.Contains("IsVisible=\"{Binding IsCapturePage}\"", mainXaml);
            Assert.Contains("StartCaptureCommand", mainXaml);
            Assert.Contains("StartCaptureImageCommand", mainXaml);
            Assert.Contains("StartAnnotateCommand", mainXaml);
        }
    }

    [Fact]
    public void OcrSession_StoresAndPreservesScreenBounds()
    {
        using var bmp = new SKBitmap(100, 100);
        using var img = new OcrImage(bmp);
        var bounds = new ShawMuOCR.Core.Capture.ScreenRect(120, 240, 400, 300);

        using var session = new OcrSession(img, OcrResult.Empty(), bounds);
        Assert.NotNull(session.ScreenBounds);
        Assert.Equal(120, session.ScreenBounds.Value.X);
        Assert.Equal(240, session.ScreenBounds.Value.Y);
        Assert.Equal(400, session.ScreenBounds.Value.Width);
        Assert.Equal(300, session.ScreenBounds.Value.Height);
    }

    private sealed class FakeSettingsService : ShawMuOCR.Core.Settings.ISettingsService
    {
        public FakeSettingsService(ShawMuOCR.Core.Settings.AppSettings settings) => Current = settings;
        public ShawMuOCR.Core.Settings.AppSettings Current { get; set; }
        public string SettingsFilePath => "test_settings.json";
        public event Action<ShawMuOCR.Core.Settings.AppSettings>? SettingsChanged;
        public ShawMuOCR.Core.Settings.AppSettings Load() => Current;
        public void Save(ShawMuOCR.Core.Settings.AppSettings settings)
        {
            Current = settings;
            SettingsChanged?.Invoke(settings);
        }
    }

    [Fact]
    public void QuickSaveHelper_ReadsSharedDirectoryAndFormatSettings()
    {
        var settings = new ShawMuOCR.Core.Settings.AppSettings
        {
            DefaultSaveFormat = "WEBP",
            DefaultSaveQuality = 75,
            DefaultSaveScale = 50,
            LastSaveDirectory = @"C:\TestQuickSave"
        };
        var settingsService = new FakeSettingsService(settings);

        var (options, ext) = ShawMuOCR.Services.QuickSaveHelper.GetExportOptions(settingsService, 200, 100);
        Assert.Equal(ImageSaveFormat.Webp, options.Format);
        Assert.Equal(".webp", ext);
        Assert.Equal(75, options.Quality);
        Assert.Equal(100, options.TargetWidth);
        Assert.Equal(50, options.TargetHeight);
        Assert.Equal(@"C:\TestQuickSave", settingsService.Current.LastSaveDirectory);

        // 模拟截图标注更改配置后，截图工具快存立即感知共享状态
        var updated = settingsService.Current.Clone();
        updated.DefaultSaveFormat = "JPEG";
        updated.DefaultSaveQuality = 95;
        updated.LastSaveDirectory = @"D:\NewSharedFolder";
        settingsService.Save(updated);

        var (newOptions, newExt) = ShawMuOCR.Services.QuickSaveHelper.GetExportOptions(settingsService, 200, 100);
        Assert.Equal(ImageSaveFormat.Jpeg, newOptions.Format);
        Assert.Equal(".jpg", newExt);
        Assert.Equal(95, newOptions.Quality);
        Assert.Equal(@"D:\NewSharedFolder", settingsService.Current.LastSaveDirectory);
    }

    [Fact]
    public void ScreenshotEditorControl_DoesNotUseFake100x100BitmapPlaceholder()
    {
        string csPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Views", "Components", "ScreenshotEditorControl.axaml.cs");
        if (File.Exists(csPath))
        {
            string cs = File.ReadAllText(csPath);
            // 确保不再通过 new SKBitmap(100, 100) 伪造空白位图占位
            Assert.DoesNotContain("new SKBitmap(100, 100)", cs);
        }
    }
}
