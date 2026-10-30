using System.Text.Json.Serialization;

namespace ShawMuOCR.Core.Settings;

/// <summary>
/// OCR 识别与模型分块配置
/// </summary>
public sealed class OcrSettings
{
    private string _model = "PP-OCRv6_small";

    /// <summary>
    /// 自动扫描同目录下 models 文件夹中的 Paddle 模型
    /// </summary>
    public bool ScanExternalModels { get; set; } = false;

    /// <summary>
    /// 兼容旧版配置字段
    /// </summary>
    [JsonIgnore]
    public bool UseExternalModel
    {
        get => ScanExternalModels;
        set => ScanExternalModels = value;
    }

    /// <summary>
    /// 外部模型类型："LocalDirectory" (本地自定义目录) 或 "WebApi" (HTTP 接口)
    /// </summary>
    public string ExternalModelType { get; set; } = "LocalDirectory";

    /// <summary>
    /// 外部本地模型目录路径（默认 "models/v6"）
    /// </summary>
    public string ExternalModelPath { get; set; } = "models/v6";

    /// <summary>
    /// 外部 Web API 接口地址（默认 "http://127.0.0.1:8080/ocr"）
    /// </summary>
    public string ExternalApiUrl { get; set; } = "http://127.0.0.1:8080/ocr";

    /// <summary>
    /// 当前生效的模型标识（默认 "PP-OCRv6_small"，如 "PP-OCRv6_tiny", "PP-OCRv6_medium" 等）
    /// </summary>
    public string Model
    {
        get
        {
            if (string.Equals(ExternalModelType, "WebApi", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(ExternalApiUrl))
            {
                return $"External:WebApi ({ExternalApiUrl})";
            }
            if (string.Equals(ExternalModelType, "LocalDirectory", StringComparison.OrdinalIgnoreCase) &&
                _model == "PP-OCRv6_small" && !string.IsNullOrWhiteSpace(ExternalModelPath) && ExternalModelPath != "models/v6")
            {
                return $"External:Local ({ExternalModelPath})";
            }
            return string.IsNullOrWhiteSpace(_model) ? "PP-OCRv6_small" : _model;
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                _model = "PP-OCRv6_small";
                return;
            }
            if (value.StartsWith("External:WebApi", StringComparison.OrdinalIgnoreCase))
            {
                ScanExternalModels = true;
                ExternalModelType = "WebApi";
            }
            else if (value.StartsWith("External:Local", StringComparison.OrdinalIgnoreCase))
            {
                ScanExternalModels = true;
                ExternalModelType = "LocalDirectory";
                _model = value;
            }
            else
            {
                _model = value;
            }
        }
    }

    /// <summary>
    /// 兼容旧版配置字段
    /// </summary>
    [JsonIgnore]
    public string EmbeddedModel
    {
        get => _model;
        set => _model = value;
    }

    /// <summary>
    /// OCR 推理硬件后端模式："Auto" (默认，自动选择最优后端), "GPU" (Vulkan GPU 加速), "CPU" (纯 CPU AVX2/SIMD)
    /// </summary>
    public string OcrBackend { get; set; } = "Auto";

    /// <summary>
    /// 指定使用的物理 GPU 设备索引（-1 为自动优选，0 为首选 GPU，1 为次选 GPU...）
    /// </summary>
    public int SelectedGpuIndex { get; set; } = -1;

    /// <summary>
    /// 文字方向检测：开启后检测倒立/旋转文字（默认开启）
    /// </summary>
    public bool EnableAngleDetection { get; set; } = true;

    /// <summary>
    /// 文本检测最大边长限制：等比缩放送检，兼顾显存与清晰度（推荐 1280，识别仍采用原图高清切片）
    /// </summary>
    public int MaxSideLen { get; set; } = 1280;

    /// <summary>
    /// 闲置自动休眠：开启后无操作达到指定时长时自动深度整理并释放非活跃物理内存
    /// </summary>
    public bool EnableIdleSleep { get; set; } = true;

    /// <summary>
    /// 闲置休眠等待时间（分钟）
    /// </summary>
    public int IdleSleepMinutes { get; set; } = 3;

    public OcrSettings Clone() => new()
    {
        ScanExternalModels = ScanExternalModels,
        ExternalModelType = ExternalModelType,
        ExternalModelPath = ExternalModelPath,
        ExternalApiUrl = ExternalApiUrl,
        Model = Model,
        EmbeddedModel = EmbeddedModel,
        OcrBackend = OcrBackend,
        SelectedGpuIndex = SelectedGpuIndex,
        EnableAngleDetection = EnableAngleDetection,
        MaxSideLen = MaxSideLen,
        EnableIdleSleep = EnableIdleSleep,
        IdleSleepMinutes = IdleSleepMinutes
    };
}
