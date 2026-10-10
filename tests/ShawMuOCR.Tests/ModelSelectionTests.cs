using ShawMuOCR.Core.Hotkey;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.OCR;
using ShawMuOCR.ViewModels;
using Xunit;

namespace ShawMuOCR.Tests;

public class ModelSelectionTests
{
    [Fact]
    public void AppSettings_DefaultValues_AreEmbeddedSmall()
    {
        var settings = new AppSettings();

        Assert.False(settings.UseExternalModel);
        Assert.Equal("PP-OCRv6_small", settings.EmbeddedModel);
        Assert.Equal("PP-OCRv6_small", settings.Model);
        Assert.Equal("LocalDirectory", settings.ExternalModelType);
        Assert.Equal("models/v6", settings.ExternalModelPath);
        Assert.Equal("http://127.0.0.1:8080/ocr", settings.ExternalApiUrl);
    }

    [Fact]
    public void AppSettings_WhenExternalEnabled_ModelPropertyReflectsExternal()
    {
        var settings = new AppSettings
        {
            UseExternalModel = true,
            ExternalModelType = "WebApi",
            ExternalApiUrl = "http://localhost:5000/api"
        };

        Assert.Contains("WebApi", settings.Model);

        settings.ExternalModelType = "LocalDirectory";
        settings.ExternalModelPath = "C:/custom_models";
        Assert.Contains("Local", settings.Model);
    }

    [Fact]
    public void AppSettings_Clone_CopiesAllModelSelectionProperties()
    {
        var original = new AppSettings
        {
            UseExternalModel = true,
            EmbeddedModel = "PP-OCRv6_tiny",
            ExternalModelType = "WebApi",
            ExternalModelPath = "D:/models",
            ExternalApiUrl = "http://my-ocr.test/ocr"
        };

        var clone = original.Clone();

        Assert.Equal(original.UseExternalModel, clone.UseExternalModel);
        Assert.Equal(original.EmbeddedModel, clone.EmbeddedModel);
        Assert.Equal(original.ExternalModelType, clone.ExternalModelType);
        Assert.Equal(original.ExternalModelPath, clone.ExternalModelPath);
        Assert.Equal(original.ExternalApiUrl, clone.ExternalApiUrl);
    }

    [Fact]
    public void OcrEngineFactory_CreateWithSettings_CreatesExternalApiEngineWhenConfigured()
    {
        var factory = new OcrEngineFactory();
        var settings = new AppSettings
        {
            UseExternalModel = true,
            ExternalModelType = "WebApi",
            ExternalApiUrl = "http://127.0.0.1:9999/ocr"
        };

        using var engine = factory.Create(settings);

        Assert.IsType<ExternalApiOcrEngine>(engine);
        Assert.Contains("ExternalApi", engine.ModelName);
    }

    [Fact]
    public void OcrEngineFactory_CreateWithSettings_CreatesPaddleOcrV6EngineWhenEmbedded()
    {
        var factory = new OcrEngineFactory();
        var settings = new AppSettings
        {
            UseExternalModel = false,
            EmbeddedModel = "PP-OCRv6_tiny"
        };

        using var engine = factory.Create(settings);

        Assert.IsType<PaddleOcrV6Engine>(engine);
        Assert.Equal("PP-OCRv6_tiny", engine.ModelName);
    }

    [Fact]
    public void OcrModelProvider_ScanExternalModels_DiscoversV6Model()
    {
        var provider = new OcrModelProvider();
        var models = provider.GetAvailableModels(scanExternal: true);

        Assert.Contains("PP-OCRv6_small", models);
        Assert.Contains("PP-OCRv6_tiny", models);

        var ext = provider.ResolveExternalModel("v6");
        Assert.NotNull(ext);
        Assert.Contains("v6", ext.Name);
        Assert.True(System.IO.File.Exists(ext.DetectionPath));
        Assert.True(System.IO.File.Exists(ext.RecognitionPath));
    }

    [Fact]
    public void OcrEngineFactory_CreateWithExternalModelName_CreatesPaddleEngineWithExternalModel()
    {
        var provider = new OcrModelProvider();
        var factory = new OcrEngineFactory(modelProvider: provider);
        var settings = new AppSettings
        {
            ScanExternalModels = true,
            Model = "v6 (外部)"
        };

        using var engine = factory.Create(settings);
        Assert.IsType<PaddleOcrV6Engine>(engine);
        Assert.Contains("v6", engine.ModelName);
    }

    [Fact]
    public void SettingsViewModel_AvailableModels_LoadsDirectlyFromModelsFolder()
    {
        var settingsService = new FakeSettingsService();
        var startupService = new FakeStartupService();
        var hotkeyService = new FakeHotkeyService();
        var modelProvider = new OcrModelProvider();

        var vm = new SettingsViewModel(settingsService, startupService, hotkeyService, modelProvider);

        // 直接读取同目录下 models/ 文件夹中的模型：默认包含 PP-OCRv6_small 与 PP-OCRv6_tiny
        Assert.Contains("PP-OCRv6_small", vm.AvailableModels);
        Assert.Contains("PP-OCRv6_tiny", vm.AvailableModels);
        Assert.Equal("PP-OCRv6_small", vm.SelectedModel);
    }

    [Fact]
    public void OcrEngineFactory_WhenModelIsNullOrEmpty_DefaultsSafelyToSmall()
    {
        var factory = new OcrEngineFactory();
        var settings = new AppSettings { Model = null! };
        using var engine = factory.Create(settings);
        Assert.NotNull(engine);
        Assert.Equal("PP-OCRv6_small", engine.ModelName);
    }

    [Fact]
    public void OcrModelProvider_ResolveExternalModel_BuiltInModels_AlwaysReturnNull()
    {
        var provider = new OcrModelProvider();
        Assert.Null(provider.ResolveExternalModel("PP-OCRv6_small"));
        Assert.Null(provider.ResolveExternalModel("PP-OCRv6_tiny"));
    }

    [Fact]
    public void OcrEngineFactory_WhenScanExternalModelsEnabled_ButBuiltInModelSelected_LoadsBuiltInModel()
    {
        var provider = new OcrModelProvider();
        var factory = new OcrEngineFactory(modelProvider: provider);
        var settings = new AppSettings
        {
            ScanExternalModels = true,
            Model = "PP-OCRv6_small"
        };

        using var engine = factory.Create(settings);
        Assert.IsType<PaddleOcrV6Engine>(engine);
        Assert.Equal("PP-OCRv6_small", engine.ModelName);
    }

    [Fact]
    public void ModelMemoryDiagnostics_WhenScanExternalModelsEnabled_ButBuiltInModelSelected_ReportsBuiltInModel()
    {
        var provider = new OcrModelProvider();
        var settingsService = new FakeSettingsService
        {
            Current = new AppSettings
            {
                ScanExternalModels = true,
                Model = "PP-OCRv6_small"
            }
        };

        var diagnostics = new ShawMuOCR.Services.ModelMemoryDiagnosticsService(settingsService, provider);
        var report = diagnostics.GetReport();

        Assert.Equal("PP-OCRv6_small", report.ModelName);
        Assert.Contains("15.9 MB", report.ModelWeightsDescription);
    }

    [Fact]
    public void ModelMemoryDiagnostics_WhenExternalModelSelected_ReportsExternalCustomModel()
    {
        var provider = new OcrModelProvider();
        var settingsService = new FakeSettingsService
        {
            Current = new AppSettings
            {
                Model = "v6"
            }
        };

        var diagnostics = new ShawMuOCR.Services.ModelMemoryDiagnosticsService(settingsService, provider);
        var report = diagnostics.GetReport();

        Assert.Contains("自定义模型", report.ModelName);
        Assert.Contains("v6", report.ModelName);
        Assert.Contains("ONNX", report.ModelWeightsDescription);
    }

    private class FakeSettingsService : ISettingsService
    {
        public AppSettings Current { get; set; } = new();
        public string SettingsFilePath => "settings.json";
        public event Action<AppSettings>? SettingsChanged;
        public AppSettings Load() => Current;
        public void Save(AppSettings settings)
        {
            Current = settings.Clone();
            SettingsChanged?.Invoke(Current);
        }
    }

    private class FakeStartupService : IStartupService
    {
        public bool IsEnabled() => false;
        public void SetEnabled(bool enabled) { }
    }

    private class FakeHotkeyService : IGlobalHotkeyService
    {
        public bool IsRegistered => true;
        public HotkeyGesture CurrentGesture => default;
        public bool IsPaused { get; set; }
        public bool Register(HotkeyGesture gesture, Action callback) => true;
        public void Unregister() { }
        public bool Register(int id, HotkeyGesture gesture, Action callback) => true;
        public void Unregister(int id) { }
        public bool IsRegisteredId(int id) => true;
        public HotkeyGesture GetGesture(int id) => default;
        public void Dispose() { }
    }
}
