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

        string exeDir = GetExeDirectory();
        string lower = Path.Combine(exeDir, "models");
        if (Directory.Exists(lower)) return lower;

        string upper = Path.Combine(exeDir, "Models");
        if (Directory.Exists(upper)) return upper;

        return lower;
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
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return false;
        }

        if (ResolveExternalModel(modelName) != null)
        {
            return true;
        }

        string path = GetModelPath(modelName);
        return TryParsePaddleModelDirectory(path) != null;
    }

    public IReadOnlyList<string> GetAvailableModels() => GetAvailableModels(true);

    public IReadOnlyList<string> GetAvailableModels(bool scanExternal = true)
    {
        var models = new List<string>();

        var externalModels = ScanExternalModels();
        foreach (var ext in externalModels)
        {
            if (!models.Contains(ext.Name, StringComparer.OrdinalIgnoreCase))
            {
                models.Add(ext.Name);
            }
        }

        // 若实际存在 PP-OCRv6_small，将其排在首位作为优先推荐项；绝不硬编码塞入不存在的模型
        int smallIdx = models.FindIndex(m => string.Equals(m, "PP-OCRv6_small", StringComparison.OrdinalIgnoreCase));
        if (smallIdx > 0)
        {
            string small = models[smallIdx];
            models.RemoveAt(smallIdx);
            models.Insert(0, small);
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

        // 1. 扫描 models 根目录下的每个子文件夹（如 models/PP-OCRv6_small, models/PP-OCRv6_tiny 等）
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

            // 2. 如果 models 根目录直接放置了 det 和 rec 模型，也作为一个模型加载
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

        // 1. 从当前 models 目录扫描实际存在的模型
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

            // 兼容别名：v6 映射到 PP-OCRv6_small，tiny 映射到 PP-OCRv6_tiny
            if ((string.Equals(modelName, "v6", StringComparison.OrdinalIgnoreCase) || string.Equals(modelName, "v6 (外部)", StringComparison.OrdinalIgnoreCase)) &&
                (folderName.Contains("small", StringComparison.OrdinalIgnoreCase) || folderName.Contains("v6", StringComparison.OrdinalIgnoreCase)))
            {
                return info;
            }

            if ((string.Equals(modelName, "tiny", StringComparison.OrdinalIgnoreCase) || string.Equals(modelName, "tiny (外部)", StringComparison.OrdinalIgnoreCase)) &&
                folderName.Contains("tiny", StringComparison.OrdinalIgnoreCase))
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
            string name = customName ?? folderName;

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
