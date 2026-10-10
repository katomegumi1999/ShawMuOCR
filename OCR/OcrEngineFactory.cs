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

        // 3. 优先尝试解析外部自定义模型结构 (例如用户放入 models/ 下的自定义子目录)
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

        // 4. 直接使用同目录下 models 目录作为模型（PP-OCRv6_small, PP-OCRv6_tiny, PP-OCRv6_medium 等，默认是 small）
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
