using ShawMuOCR.Core.OCR;

namespace ShawMuOCR.OCR;

public sealed class OcrModelProvider : IOcrModelProvider
{
    public string BaseModelsDirectory { get; }

    public OcrModelProvider(string? baseDirectory = null)
    {
        BaseModelsDirectory = ResolveModelsDirectory(baseDirectory);
    }

    public static string ResolveModelsDirectory(string? preferredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath))
        {
            return Path.IsPathRooted(preferredPath)
                ? preferredPath
                : Path.Combine(GetExeDirectory(), preferredPath);
        }

        // 严格锁定在当前 EXE 所在目录下的 models 文件夹，禁止向上查找父级目录
        return Path.Combine(GetExeDirectory(), "models");
    }

    public static string GetExeDirectory()
    {
        if (!string.IsNullOrEmpty(Environment.ProcessPath))
        {
            string? dir = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                return dir;
            }
        }

        return AppContext.BaseDirectory;
    }

    public string GetModelPath(string modelName)
    {
        return Path.Combine(BaseModelsDirectory, modelName);
    }

    public bool HasModel(string modelName)
    {
        if (string.Equals(modelName, "PP-OCRv6_small", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(modelName, "PP-OCRv6_tiny", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (ResolveExternalModel(modelName) != null)
        {
            return true;
        }

        string path = GetModelPath(modelName);
        return Directory.Exists(path) || File.Exists(path);
    }

    public IReadOnlyList<string> GetAvailableModels() => GetAvailableModels(false);

    public IReadOnlyList<string> GetAvailableModels(bool scanExternal = false)
    {
        var models = new List<string>
        {
            "PP-OCRv6_small",
            "PP-OCRv6_tiny"
        };

        if (scanExternal)
        {
            var externalModels = ScanExternalModels();
            foreach (var ext in externalModels)
            {
                if (!models.Contains(ext.Name, StringComparer.OrdinalIgnoreCase))
                {
                    models.Add(ext.Name);
                }
            }
        }

        return models;
    }

    public IReadOnlyList<ExternalPaddleModelInfo> ScanExternalModels()
    {
        var results = new List<ExternalPaddleModelInfo>();

        if (!Directory.Exists(BaseModelsDirectory))
        {
            return results;
        }

        // 1. 扫描 models 根目录下的每个子文件夹（如 models/v6, models/v4）
        try
        {
            string[] subDirs = Directory.GetDirectories(BaseModelsDirectory);
            foreach (string subDir in subDirs)
            {
                var info = TryParsePaddleModelDirectory(subDir);
                if (info != null)
                {
                    results.Add(info);
                }
            }

            // 2. 如果 models 根目录直接放置了 det 和 rec 模型，也作为一个外部模型加载
            var rootInfo = TryParsePaddleModelDirectory(BaseModelsDirectory, "models (根目录)");
            if (rootInfo != null && !results.Any(r => string.Equals(r.DirectoryPath, BaseModelsDirectory, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(rootInfo);
            }
        }
        catch
        {
            // 目录访问受限或异常时宽容处理
        }

        return results;
    }

    public ExternalPaddleModelInfo? ResolveExternalModel(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return null;

        // 内置模型切勿解析为外部模型
        if (string.Equals(modelName, "PP-OCRv6_small", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(modelName, "PP-OCRv6_tiny", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // 1. 从当前 models 目录扫描
        var scanned = ScanExternalModels();
        foreach (var info in scanned)
        {
            if (string.Equals(info.Name, modelName, StringComparison.OrdinalIgnoreCase))
            {
                return info;
            }

            string folderName = Path.GetFileName(info.DirectoryPath);
            if (string.Equals(folderName, modelName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals($"{folderName} (外部)", modelName, StringComparison.OrdinalIgnoreCase))
            {
                return info;
            }
        }

        // 2. 若 modelName 本身直接是一个有效路径（如包含路径分隔符或绝对路径）
        if (modelName.Contains('/') || modelName.Contains('\\') || modelName.StartsWith("External:", StringComparison.OrdinalIgnoreCase))
        {
            string cleanName = modelName.StartsWith("External:Local (", StringComparison.OrdinalIgnoreCase) && modelName.EndsWith(")")
                ? modelName.Substring("External:Local (".Length, modelName.Length - "External:Local (".Length - 1)
                : modelName;

            string candidatePath = Path.IsPathRooted(cleanName)
                ? cleanName
                : Path.Combine(BaseModelsDirectory, cleanName.Replace("models/", "").Replace("models\\", ""));

            if (Directory.Exists(candidatePath))
            {
                var direct = TryParsePaddleModelDirectory(candidatePath);
                if (direct != null) return direct;
            }
        }

        return null;
    }

    private static ExternalPaddleModelInfo? TryParsePaddleModelDirectory(string dirPath, string? customName = null)
    {
        try
        {
            if (!Directory.Exists(dirPath)) return null;

            string[] onnxFiles = Directory.GetFiles(dirPath, "*.onnx");
            if (onnxFiles.Length < 2) return null;

            string? detFile = onnxFiles.FirstOrDefault(f => Path.GetFileName(f).Contains("det", StringComparison.OrdinalIgnoreCase));
            string? recFile = onnxFiles.FirstOrDefault(f => Path.GetFileName(f).Contains("rec", StringComparison.OrdinalIgnoreCase));

            if (detFile == null || recFile == null) return null;

            string[] txtFiles = Directory.GetFiles(dirPath, "*.txt");
            string? dictFile = txtFiles.FirstOrDefault(f =>
                Path.GetFileName(f).Contains("dict", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(f).Contains("key", StringComparison.OrdinalIgnoreCase))
                ?? txtFiles.FirstOrDefault();

            string? clsFile = onnxFiles.FirstOrDefault(f => Path.GetFileName(f).Contains("cls", StringComparison.OrdinalIgnoreCase));

            string folderName = Path.GetFileName(dirPath);
            string name = customName ?? $"{folderName} (外部)";

            return new ExternalPaddleModelInfo
            {
                Name = name,
                DirectoryPath = dirPath,
                DetectionPath = detFile,
                RecognitionPath = recFile,
                DictionaryPath = dictFile,
                ClassificationPath = clsFile
            };
        }
        catch
        {
            return null;
        }
    }
}
