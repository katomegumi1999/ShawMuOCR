using System;
using System.IO;
using ShawMuOCR.Core.Logging;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Settings;
using SkiaSharp;
using Xunit;

namespace ShawMuOCR.Tests;

public class SettingsAndLoggingPathTests
{
    [Fact]
    public void JsonSettingsService_DefaultPath_PointsToExeDirectory()
    {
        var settingsService = new JsonSettingsService();
        string expectedPath = Path.Combine(AppContext.BaseDirectory, "settings.json");

        Assert.Equal(expectedPath, settingsService.SettingsFilePath);
    }

    [Fact]
    public void FileLoggerService_DefaultLogDirectory_PointsToExeLogsDirectory()
    {
        var settingsService = new JsonSettingsService();
        var logger = new FileLoggerService(settingsService);
        string expectedDir = Path.Combine(AppContext.BaseDirectory, "logs");

        Assert.Equal(expectedDir, logger.LogDirectory);
    }

    [Fact]
    public void JsonSettingsService_CustomPath_SavesAndLoadsSuccessfully()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"test_settings_{Guid.NewGuid():N}.json");
        try
        {
            var service = new JsonSettingsService(tempFile);
            var settings = service.Current;
            settings.AutoCopy = !settings.AutoCopy;
            settings.Hotkey = "Ctrl+Alt+Q";
            service.Save(settings);

            var reloaded = new JsonSettingsService(tempFile);
            Assert.Equal("Ctrl+Alt+Q", reloaded.Current.Hotkey);
            Assert.Equal(settings.AutoCopy, reloaded.Current.AutoCopy);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void OcrSession_ImageLifecycle_KeptAliveUntilSessionDisposed()
    {
        var bitmap = new SKBitmap(20, 20);
        var image = new OcrImage(bitmap);
        var result = new OcrResult
        {
            Text = "Test",
            IsSuccess = true,
            Elapsed = TimeSpan.FromMilliseconds(10)
        };

        var session = new OcrSession(image, result);
        Assert.NotNull(session.Image);
        Assert.False(session.Image.Bitmap.IsNull);
        Assert.NotEqual(IntPtr.Zero, session.Image.Bitmap.Handle);

        session.Dispose();
        // After session disposal, Image should be disposed
        Assert.Equal(IntPtr.Zero, bitmap.Handle);
    }
}
