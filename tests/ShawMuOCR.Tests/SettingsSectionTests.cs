using System;
using System.IO;
using ShawMuOCR.Core.Settings;
using Xunit;

namespace ShawMuOCR.Tests;

public class SettingsSectionTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public SettingsSectionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "shawmu_sections_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void AppSettings_Sections_AreNotNullByDefault()
    {
        var settings = new AppSettings();
        Assert.NotNull(settings.General);
        Assert.NotNull(settings.Hotkeys);
        Assert.NotNull(settings.Ocr);
        Assert.NotNull(settings.Recording);
        Assert.NotNull(settings.Translation);
        Assert.NotNull(settings.Theme);
        Assert.NotNull(settings.Batch);
        Assert.NotNull(settings.HttpApi);
    }

    [Fact]
    public void AppSettings_BridgeProperties_SynchronizeWithSections()
    {
        var settings = new AppSettings();

        // Hotkeys
        settings.Hotkey = "Ctrl+Shift+Z";
        Assert.Equal("Ctrl+Shift+Z", settings.Hotkeys.Ocr);

        // OCR
        settings.MaxSideLen = 1600;
        Assert.Equal(1600, settings.Ocr.MaxSideLen);

        // Recording
        settings.RecordingFps = 60;
        Assert.Equal(60u, settings.Recording.Fps);

        // Translation
        settings.TranslateProvider = "Google";
        Assert.Equal("Google", settings.Translation.Provider);

        // Theme
        settings.ThemeEnableAcrylic = false;
        Assert.False(settings.Theme.EnableAcrylic);

        // Batch
        settings.BatchMaxCores = 8;
        Assert.Equal(8, settings.Batch.MaxCores);

        // HttpApi
        settings.HttpApiPort = 8090;
        Assert.Equal(8090, settings.HttpApi.Port);

        // General
        settings.AutoCopy = false;
        Assert.False(settings.General.AutoCopy);
    }

    [Fact]
    public void JsonSettingsService_SavesSectionalHierarchy()
    {
        var service = new JsonSettingsService(_path);
        service.Current.Hotkeys.Ocr = "Alt+K";
        service.Current.Recording.Fps = 60;
        service.Current.Translation.Provider = "OpenAI";
        service.Save(service.Current);

        string json = File.ReadAllText(_path);

        // 验证产物呈结构化分块，且未在根层平铺冗余字段
        Assert.Contains("\"Hotkeys\":", json);
        Assert.Contains("\"Recording\":", json);
        Assert.Contains("\"Translation\":", json);
        Assert.Contains("\"General\":", json);
        Assert.Contains("\"Ocr\":", json);
        Assert.Contains("\"Theme\":", json);
        Assert.Contains("\"Batch\":", json);
        Assert.Contains("\"HttpApi\":", json);

        // 重新加载后数据完好
        var reloaded = new JsonSettingsService(_path);
        Assert.Equal("Alt+K", reloaded.Current.Hotkey);
        Assert.Equal("Alt+K", reloaded.Current.Hotkeys.Ocr);
        Assert.Equal(60u, reloaded.Current.RecordingFps);
        Assert.Equal(60u, reloaded.Current.Recording.Fps);
        Assert.Equal("OpenAI", reloaded.Current.TranslateProvider);
        Assert.Equal("OpenAI", reloaded.Current.Translation.Provider);
    }

    [Fact]
    public void JsonSettingsService_LoadsLegacyFlatJsonAndUpgradesToSectional()
    {
        // 模拟旧版本单层 settings.json
        File.WriteAllText(_path, """
        {
          "Hotkey": "Ctrl+Shift+O",
          "RecordingFps": 45,
          "BatchMaxCores": 6,
          "HttpApiPort": 9999,
          "TranslateProvider": "Baidu",
          "SilentMode": false
        }
        """);

        var service = new JsonSettingsService(_path);

        // 1. 旧单层平铺字段无感映射至对应分块
        Assert.Equal("Ctrl+Shift+O", service.Current.Hotkey);
        Assert.Equal("Ctrl+Shift+O", service.Current.Hotkeys.Ocr);
        Assert.Equal(45u, service.Current.Recording.Fps);
        Assert.Equal(6, service.Current.Batch.MaxCores);
        Assert.Equal(9999, service.Current.HttpApi.Port);
        Assert.Equal("Baidu", service.Current.Translation.Provider);
        Assert.False(service.Current.General.SilentMode);

        // 2. 保存后升级为结构化分块
        service.Save(service.Current);
        string newJson = File.ReadAllText(_path);
        Assert.Contains("\"Hotkeys\":", newJson);
        Assert.Contains("\"Batch\":", newJson);
        Assert.Contains("\"HttpApi\":", newJson);

        // 3. 再次加载依然完好
        var reloaded = new JsonSettingsService(_path);
        Assert.Equal("Ctrl+Shift+O", reloaded.Current.Hotkeys.Ocr);
        Assert.Equal(45u, reloaded.Current.Recording.Fps);
        Assert.Equal(6, reloaded.Current.Batch.MaxCores);
    }

    [Fact]
    public void AppSettings_Clone_DeepClonesAllSections()
    {
        var original = new AppSettings();
        original.Hotkeys.Ocr = "Alt+1";
        original.Recording.Fps = 50;
        original.General.AutoCopy = false;

        var clone = original.Clone();

        // 验证属性相同
        Assert.Equal("Alt+1", clone.Hotkeys.Ocr);
        Assert.Equal(50u, clone.Recording.Fps);
        Assert.False(clone.General.AutoCopy);

        // 验证是深拷贝（修改 clone 不影响 original）
        clone.Hotkeys.Ocr = "Alt+2";
        clone.Recording.Fps = 120;
        clone.General.AutoCopy = true;

        Assert.Equal("Alt+1", original.Hotkeys.Ocr);
        Assert.Equal(50u, original.Recording.Fps);
        Assert.False(original.General.AutoCopy);
    }
}
