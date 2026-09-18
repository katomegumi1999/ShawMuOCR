namespace ShawMuOCR.Core.OCR;

public sealed class ExternalPaddleModelInfo
{
    /// <summary>
    /// 模型名称标识，例如 "v6 (外部)" 或 "v6"
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// 模型所在根文件夹路径
    /// </summary>
    public required string DirectoryPath { get; init; }

    /// <summary>
    /// 文字检测模型 (*det*.onnx) 完整路径
    /// </summary>
    public required string DetectionPath { get; init; }

    /// <summary>
    /// 文字识别模型 (*rec*.onnx) 完整路径
    /// </summary>
    public required string RecognitionPath { get; init; }

    /// <summary>
    /// 字典文件 (*dict*.txt 或 *.txt) 完整路径（为空时自动复用内置官方字典）
    /// </summary>
    public string? DictionaryPath { get; init; }

    /// <summary>
    /// 文本行方向分类模型 (*cls*.onnx) 完整路径（为空时自动复用内置 CLS 模型）
    /// </summary>
    public string? ClassificationPath { get; init; }
}

public interface IOcrModelProvider
{
    string BaseModelsDirectory { get; }
    string GetModelPath(string modelName);
    bool HasModel(string modelName);

    IReadOnlyList<string> GetAvailableModels();
    IReadOnlyList<string> GetAvailableModels(bool scanExternal) => GetAvailableModels();
    IReadOnlyList<ExternalPaddleModelInfo> ScanExternalModels() => Array.Empty<ExternalPaddleModelInfo>();
    ExternalPaddleModelInfo? ResolveExternalModel(string modelName) => null;
}
