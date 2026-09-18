using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.Core.Sorting;

namespace ShawMuOCR.OCR;

public sealed class OcrEngineFactory : IOcrEngineFactory
{
    private readonly IOcrTextSorter _sorter;
    private readonly IOcrModelProvider _modelProvider;

    public OcrEngineFactory(IOcrTextSorter? sorter = null, IOcrModelProvider? modelProvider = null)
    {
        _sorter = sorter ?? new DefaultOcrTextSorter();
        _modelProvider = modelProvider ?? new OcrModelProvider();
    }

    public IOcrEngine Create(string modelName)
    {
        return Create(modelName, false, 1280);
    }

    public IOcrEngine Create(string modelName, bool enableAngle, int maxSideLen)
    {
        var extModel = _modelProvider.ResolveExternalModel(modelName);
        if (extModel != null)
        {
            return new PaddleOcrV6Engine(
                modelName: modelName,
                sorter: _sorter,
                enableAngle: enableAngle,
                maxSideLen: maxSideLen,
                externalModel: extModel);
        }

        return new PaddleOcrV6Engine(modelName, _sorter, enableAngle, maxSideLen);
    }

    public IOcrEngine Create(AppSettings settings)
    {
        // 1. 若用户当前选中的是内置模型，坚决加载内置嵌入式模型（不受扫描开关或磁盘 models/ 目录影响）
        if (string.Equals(settings.Model, "PP-OCRv6_small", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(settings.Model, "PP-OCRv6_tiny", StringComparison.OrdinalIgnoreCase))
        {
            return new PaddleOcrV6Engine(
                modelName: settings.Model,
                sorter: _sorter,
                enableAngle: settings.EnableAngleDetection,
                maxSideLen: settings.MaxSideLen,
                customDirectory: null);
        }

        // 2. 兼容 Web API
        if (settings.ScanExternalModels && (string.Equals(settings.ExternalModelType, "WebApi", StringComparison.OrdinalIgnoreCase) ||
                                            settings.Model.StartsWith("External:WebApi", StringComparison.OrdinalIgnoreCase)))
        {
            return new ExternalApiOcrEngine(settings.ExternalApiUrl);
        }

        // 3. 优先尝试解析当前选中的模型是否为外部扫描模型（如 "v6 (外部)"）
        var extModel = _modelProvider.ResolveExternalModel(settings.Model);
        if (extModel != null)
        {
            return new PaddleOcrV6Engine(
                modelName: settings.Model,
                sorter: _sorter,
                enableAngle: settings.EnableAngleDetection,
                maxSideLen: settings.MaxSideLen,
                externalModel: extModel);
        }

        // 4. 兼容显式指定 ExternalModelPath 的场景（仅在非内置且明确标识 External:Local 时）
        if (settings.ScanExternalModels && settings.Model.StartsWith("External:Local", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(settings.ExternalModelPath) && Directory.Exists(settings.ExternalModelPath))
        {
            return new PaddleOcrV6Engine(
                modelName: "External:LocalDirectory",
                sorter: _sorter,
                enableAngle: settings.EnableAngleDetection,
                maxSideLen: settings.MaxSideLen,
                customDirectory: settings.ExternalModelPath);
        }

        // 5. 兜底内置模型加载（PP-OCRv6_small / PP-OCRv6_tiny）
        return new PaddleOcrV6Engine(
            modelName: settings.Model,
            sorter: _sorter,
            enableAngle: settings.EnableAngleDetection,
            maxSideLen: settings.MaxSideLen,
            customDirectory: null);
    }
}
