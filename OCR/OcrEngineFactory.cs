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
        return Create(modelName, enableAngle, maxSideLen, "Auto");
    }

    public IOcrEngine Create(string modelName, bool enableAngle, int maxSideLen, string ocrBackend)
    {
        return Create(modelName, enableAngle, maxSideLen, ocrBackend, -1);
    }

    public IOcrEngine Create(string modelName, bool enableAngle, int maxSideLen, string ocrBackend, int selectedGpuIndex)
    {
        var extModel = _modelProvider.ResolveExternalModel(modelName);
        if (extModel != null)
        {
            return new PaddleOcrV6Engine(
                modelName: modelName,
                sorter: _sorter,
                enableAngle: enableAngle,
                maxSideLen: maxSideLen,
                externalModel: extModel,
                ocrBackend: ocrBackend,
                selectedGpuIndex: selectedGpuIndex);
        }

        return new PaddleOcrV6Engine(
            modelName: modelName,
            sorter: _sorter,
            enableAngle: enableAngle,
            maxSideLen: maxSideLen,
            ocrBackend: ocrBackend,
            selectedGpuIndex: selectedGpuIndex);
    }

    public IOcrEngine Create(AppSettings settings)
    {
        string modelName = string.IsNullOrWhiteSpace(settings?.Model) ? "PP-OCRv6_small" : settings.Model.Trim();
        string ocrBackend = settings?.OcrBackend ?? "Auto";
        int selectedGpuIndex = settings?.SelectedGpuIndex ?? -1;
        bool enableAngle = settings?.EnableAngleDetection ?? true;
        int maxSideLen = settings?.MaxSideLen ?? 1280;

        // 1. 兼容 Web API (仅在明确指定 WebApi 时使用)
        if (!string.IsNullOrWhiteSpace(settings?.ExternalApiUrl) &&
            (string.Equals(settings?.ExternalModelType, "WebApi", StringComparison.OrdinalIgnoreCase) ||
             modelName.StartsWith("External:WebApi", StringComparison.OrdinalIgnoreCase)))
        {
            return new ExternalApiOcrEngine(settings!.ExternalApiUrl);
        }

        // 2. 兼容显式指定 ExternalModelPath 的场景 (仅在非空且明确以 External:Local 开头时)
        if (modelName.StartsWith("External:Local", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(settings?.ExternalModelPath) && Directory.Exists(settings.ExternalModelPath))
        {
            return new PaddleOcrV6Engine(
                modelName: "External:LocalDirectory",
                sorter: _sorter,
                enableAngle: enableAngle,
                maxSideLen: maxSideLen,
                customDirectory: settings.ExternalModelPath,
                ocrBackend: ocrBackend,
                selectedGpuIndex: selectedGpuIndex);
        }

        // 3. 从 models/ 目录解析所选模型；若配置中的模型不存在但目录中有其他可用模型，则自动回退至实际存在的模型
        var extModel = _modelProvider.ResolveExternalModel(modelName);
        if (extModel == null)
        {
            var availableModels = _modelProvider.GetAvailableModels();
            if (availableModels.Count == 0)
            {
                throw new DirectoryNotFoundException(
                    $"未在 models 文件夹 ({_modelProvider.BaseModelsDirectory}) 下检测到任何可用 OCR 模型！请将分发的 models 文件夹放置在与主程序同一目录下。");
            }

            string fallbackModel = availableModels[0];
            extModel = _modelProvider.ResolveExternalModel(fallbackModel);
            modelName = fallbackModel;
            if (settings != null)
            {
                settings.Model = fallbackModel;
                settings.EmbeddedModel = fallbackModel;
            }
        }

        if (extModel != null)
        {
            return new PaddleOcrV6Engine(
                modelName: modelName,
                sorter: _sorter,
                enableAngle: enableAngle,
                maxSideLen: maxSideLen,
                externalModel: extModel,
                ocrBackend: ocrBackend,
                selectedGpuIndex: selectedGpuIndex);
        }

        // 4. 兜底路径解析
        return new PaddleOcrV6Engine(
            modelName: modelName,
            sorter: _sorter,
            enableAngle: enableAngle,
            maxSideLen: maxSideLen,
            customDirectory: null,
            ocrBackend: ocrBackend,
            selectedGpuIndex: selectedGpuIndex);
    }
}
