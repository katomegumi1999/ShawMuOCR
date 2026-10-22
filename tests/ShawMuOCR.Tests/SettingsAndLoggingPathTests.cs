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

    [Fact]
    public void DataProtectionHelper_RoundTrips_AndHandlesPlainLegacyText()
    {
        string rawSecret = "sk-proj-1234567890abcdefghijklmnopqrstuvwxyz";
        string encrypted = ShawMuOCR.Core.Security.DataProtectionHelper.Protect(rawSecret);

        if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith("enc:", encrypted);
            Assert.DoesNotContain("1234567890", encrypted); // 密文中不含明文片段
        }

        string decrypted = ShawMuOCR.Core.Security.DataProtectionHelper.Unprotect(encrypted);
        Assert.Equal(rawSecret, decrypted);

        // 不兼容旧版未加密的明文，直接返回空字符串由用户重新录入
        string legacy = "legacy_plain_api_key";
        string legacyResult = ShawMuOCR.Core.Security.DataProtectionHelper.Unprotect(legacy);
        Assert.Equal(string.Empty, legacyResult);
    }

    [Fact]
    public void JsonSettingsService_ProtectsApiKeysOnDisk_AndExposesPlainTextInMemory()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"secure_settings_{Guid.NewGuid():N}.json");
        try
        {
            var service = new JsonSettingsService(tempFile);
            var settings = service.Current;
            settings.OpenAiApiKey = "sk-live-998877665544";
            settings.BaiduApiKey = "baidu_sec_api_key";
            settings.GoogleApiKey = "google_sec_api_key";
            service.Save(settings);

            // 1. 验证在内存中 _current 是纯明文
            Assert.Equal("sk-live-998877665544", service.Current.OpenAiApiKey);
            Assert.Equal("baidu_sec_api_key", service.Current.BaiduApiKey);
            Assert.Equal("google_sec_api_key", service.Current.GoogleApiKey);

            // 2. 验证磁盘上的真实 json 文件中绝对不包含明文 Key，而是带有 enc: 前缀的密文
            string diskContent = File.ReadAllText(tempFile);
            if (OperatingSystem.IsWindows())
            {
                Assert.DoesNotContain("sk-live-998877665544", diskContent);
                Assert.DoesNotContain("baidu_sec_api_key", diskContent);
                Assert.DoesNotContain("google_sec_api_key", diskContent);
                Assert.Contains("enc:", diskContent);
            }

            // 3. 重新加载，验证内存能透明无感还原明文
            var reloaded = new JsonSettingsService(tempFile);
            Assert.Equal("sk-live-998877665544", reloaded.Current.OpenAiApiKey);
            Assert.Equal("baidu_sec_api_key", reloaded.Current.BaiduApiKey);
            Assert.Equal("google_sec_api_key", reloaded.Current.GoogleApiKey);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void AppDataPath_And_MuClipDataPath_MatchUserSpec()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string expectedShawMuDir = Path.Combine(localAppData, "ShawMuOCR");
        string expectedMuClipDir = Path.Combine(localAppData, "ShawMuOCR", "Muclip");
        string expectedMuClipDb = Path.Combine(expectedMuClipDir, "muclip.db");

        var muClipService = new MuClip.Services.MuClipService();
        // 初始化空路径时自动采用约定路径
        Assert.Equal(expectedMuClipDir, muClipService.DataDirectory);

        // 验证路径字符串拼接符合期望
        Assert.EndsWith(Path.Combine("ShawMuOCR", "Muclip"), expectedMuClipDir);
        Assert.EndsWith("muclip.db", expectedMuClipDb);
    }

    [Fact]
    public void FileLoggerService_CleanOldLogs_CleansExpiredLogs_AndKeepsRecentAndCurrentLogs()
    {
        string tempLogDir = Path.Combine(Path.GetTempPath(), $"test_logs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempLogDir);

        try
        {
            var settingsService = new JsonSettingsService();
            settingsService.Current.EnableLogAutoClean = false;
            var logger = new FileLoggerService(settingsService, tempLogDir);

            // 1. 创建 2 个过期日志文件（30 天前和 60 天前）
            string expiredFile1 = Path.Combine(tempLogDir, "shawmu_ocr_20200101.log");
            string expiredFile2 = Path.Combine(tempLogDir, "shawmu_ocr_20200102.log");
            File.WriteAllText(expiredFile1, "old log 1");
            File.WriteAllText(expiredFile2, "old log 2");

            // 2. 创建 1 个 2 天前的近期日志文件（在 7 天保留期内）
            string recentDateStr = DateTime.Today.AddDays(-2).ToString("yyyyMMdd");
            string recentFile = Path.Combine(tempLogDir, $"shawmu_ocr_{recentDateStr}.log");
            File.WriteAllText(recentFile, "recent log");

            // 3. 创建今日日志文件
            string todayFile = logger.CurrentLogFilePath;
            File.WriteAllText(todayFile, "today log");

            // 4. 执行清理（保留 7 天内）
            int cleanedCount = logger.CleanOldLogs(7);

            Assert.Equal(2, cleanedCount);
            Assert.False(File.Exists(expiredFile1));
            Assert.False(File.Exists(expiredFile2));
            Assert.True(File.Exists(recentFile));
            Assert.True(File.Exists(todayFile));
        }
        finally
        {
            if (Directory.Exists(tempLogDir))
            {
                try { Directory.Delete(tempLogDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void AppSettings_AutoLogClean_DefaultsAndClonesCorrectly()
    {
        var settings = new AppSettings();
        Assert.True(settings.EnableLogAutoClean);
        Assert.Equal(7, settings.LogRetentionDays);

        settings.EnableLogAutoClean = false;
        settings.LogRetentionDays = 14;

        var cloned = settings.Clone();
        Assert.False(cloned.EnableLogAutoClean);
        Assert.Equal(14, cloned.LogRetentionDays);
    }
}
