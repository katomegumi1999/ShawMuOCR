using ShawMuOCR.Core.Ai;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.Core.Sorting;

namespace ShawMuOCR.OCR;

public sealed class OcrEngineFactory : IOcrEngineFactory
{
    private readonly IOcrTextSorter _sorter;
    private readonly IOcrModelProvider _modelProvider;
    private readonly IAiQuotaService? _aiQuota;

    public OcrEngineFactory(
        IOcrTextSorter? sorter = null,
        IOcrModelProvider? modelProvider = null,
        IAiQuotaService? aiQuota = null)
    {
        _sorter = sorter ?? new DefaultOcrTextSorter();
        _modelProvider = modelProvider ?? new OcrModelProvider();
        _aiQuota = aiQuota;
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

        // 0. AI 视觉接口引擎：从节点池里取用户选中的节点
        if (IsAiApiSelected(settings, modelName))
        {
            return CreateAiEngine(settings!);
        }

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

    /// <summary>
    /// 从节点池解析 OCR 所选节点并构造引擎。
    /// 节点被删除时构造一个空节点而不是抛异常：让识别阶段给出可操作的中文提示，
    /// 不打断启动与页面切换。
    /// </summary>
    private IOcrEngine CreateAiEngine(AppSettings settings)
    {
        var ocr = settings.Ocr;
        var profile = settings.Ai.Find(ocr.AiProfileId);

        if (profile == null)
        {
            profile = new AiApiProfile
            {
                Id = string.IsNullOrWhiteSpace(ocr.AiProfileId) ? "unset" : ocr.AiProfileId,
                Name = "未选择节点",
                ApiKey = string.Empty,
                Model = string.Empty
            };
        }

        var client = OpenAiCompatibleClient.FromProfile(
            profile,
            ocr.AiTimeoutSeconds,
            ocr.AiMaxConcurrency);

        return new AiApiOcrEngine(
            client,
            profile.Name,
            ocr.AiVisionPrompt,
            _aiQuota,
            ocr.AiMaxImageSide,
            ocr.AiJpegQuality);
    }

    /// <summary>
    /// 判定当前配置是否选择了 AI 视觉接口引擎。
    /// 同时看显式类型标记与模型名前缀，兼容历史配置里把类型编码进模型名的写法。
    /// </summary>
    public static bool IsAiApiSelected(AppSettings? settings, string modelName)
    {
        if (settings == null) return false;
        if (modelName.StartsWith("External:AiApi", StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(settings.Ocr.ExternalModelType, "AiApi", StringComparison.OrdinalIgnoreCase);
    }
}
