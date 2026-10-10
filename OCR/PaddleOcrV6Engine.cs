using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Sdcb.SimdPaddleOCR;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Sorting;
using SkiaSharp;
using CoreOcrResult = ShawMuOCR.Core.OCR.OcrResult;

namespace ShawMuOCR.OCR;

public sealed record OcrBackendProbeInfo(
    bool AutoSelectsGpu,
    bool HasVulkanDevice,
    string? GpuDeviceName,
    bool SupportsCoopMatrix,
    uint SubgroupMin)
{
    public string AutoResolvedMode => AutoSelectsGpu ? "GPU" : "CPU";

    public string AutoResolvedDisplayText => AutoSelectsGpu
        ? (!string.IsNullOrWhiteSpace(GpuDeviceName)
            ? $"GPU 加速 (Vulkan · {GpuDeviceName})"
            : "GPU 加速 (Vulkan)")
        : "纯 CPU (AVX2 SIMD)";

    public string AutoDecisionReason => AutoSelectsGpu
        ? (SupportsCoopMatrix
            ? "支持硬件协作矩阵 (CoopMatrix)"
            : $"支持 Wave{SubgroupMin} 子群并行 (SubgroupMin={SubgroupMin}≥64)")
        : (HasVulkanDevice && !string.IsNullOrWhiteSpace(GpuDeviceName)
            ? $"核显 {GpuDeviceName} 无协作矩阵且 SubgroupMin={SubgroupMin}<64，已自动优选 CPU"
            : "未检测到满足条件的 Vulkan GPU，已自动优选 CPU");
}

public sealed record DiscoveredGpuDevice(int Index, string Name, bool SupportsCoopMatrix, uint SubgroupMin, uint VendorId)
{
    public string DisplayName =>
        Index < 0
            ? "自动优选 (Auto - 智能选择最优显卡)"
            : $"[GPU {Index}] {Name}" +
              (SupportsCoopMatrix ? " (独显 · Tensor Core)" :
               VendorId == 4098 && SubgroupMin >= 64 ? " (核显 · Wave64)" :
               VendorId == 4318 ? " (NVIDIA 独显)" :
               VendorId == 32902 ? " (Intel 显卡)" : string.Empty);
}

public sealed class PaddleOcrV6Engine : IOcrEngine
{
    private static OcrBackendProbeInfo? s_cachedProbeInfo;
    private static IReadOnlyList<DiscoveredGpuDevice>? s_cachedDiscoveredGpus;
    private static int s_currentSelectedGpuIndex = -1;
    private static readonly object s_probeLock = new();
    private readonly PaddleOcrAll _ocr;
    private readonly IOcrTextSorter _sorter;
    private bool _disposed;

    static PaddleOcrV6Engine()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SIMD_OCR_VK_PRIORITY")))
        {
            Environment.SetEnvironmentVariable("SIMD_OCR_VK_PRIORITY", "off");
        }
    }

    public string ModelName { get; }
    public OcrBackend ConfiguredBackend { get; }
    public string BackendMode { get; }
    public int SelectedGpuIndex { get; }

    public PaddleOcrV6Engine(
        string modelName = "PP-OCRv6_small",
        IOcrTextSorter? sorter = null,
        bool enableAngle = true,
        int maxSideLen = 1280,
        string? customDirectory = null,
        ExternalPaddleModelInfo? externalModel = null,
        string ocrBackend = "Auto",
        int selectedGpuIndex = -1)
    {
        ModelName = modelName;
        _sorter = sorter ?? new DefaultOcrTextSorter();
        SelectedGpuIndex = selectedGpuIndex;

        var backend = ParseOcrBackend(ocrBackend);
        ConfiguredBackend = backend;
        BackendMode = backend switch
        {
            OcrBackend.Cpu => "CPU",
            OcrBackend.Vulkan => "GPU",
            _ => "Auto"
        };

        if (backend != OcrBackend.Cpu)
        {
            SetSelectedGpuIndex(selectedGpuIndex);
        }

        var options = CreatePaddleOcrOptions(enableAngle, maxSideLen, backend);

        string detPath;
        string recPath;
        string? dictPath;
        string? clsPath;

        if (externalModel != null)
        {
            detPath = externalModel.DetectionPath;
            recPath = externalModel.RecognitionPath;
            dictPath = externalModel.DictionaryPath;
            clsPath = externalModel.ClassificationPath;
        }
        else if (!string.IsNullOrWhiteSpace(customDirectory))
        {
            string targetDir = Path.IsPathRooted(customDirectory)
                ? customDirectory
                : Path.Combine(OcrModelProvider.GetExeDirectory(), customDirectory);

            if (!Directory.Exists(targetDir))
            {
                throw new DirectoryNotFoundException($"未找到指定的模型目录: {targetDir}");
            }

            detPath = Directory.GetFiles(targetDir, "*det*.onnx").FirstOrDefault()
                ?? throw new FileNotFoundException($"模型目录 [{targetDir}] 缺少检测模型 (*det*.onnx)");
            recPath = Directory.GetFiles(targetDir, "*rec*.onnx").FirstOrDefault()
                ?? throw new FileNotFoundException($"模型目录 [{targetDir}] 缺少识别模型 (*rec*.onnx)");
            dictPath = Directory.GetFiles(targetDir, "*dict*.txt").FirstOrDefault()
                ?? Directory.GetFiles(targetDir, "*.txt").FirstOrDefault();
            clsPath = Directory.GetFiles(targetDir, "*cls*.onnx").FirstOrDefault();
        }
        else
        {
            // 默认从本地 models 目录解析模型
            string baseDir = OcrModelProvider.ResolveModelsDirectory();
            string targetDir = ResolveTargetModelDirectory(baseDir, modelName);

            if (!Directory.Exists(targetDir))
            {
                throw new DirectoryNotFoundException(
                    $"[PaddleOcrV6Engine] 未找到模型目录: '{targetDir}'。\n" +
                    $"请确保已将 models 文件夹放置在与主程序同一目录下。");
            }

            detPath = Directory.GetFiles(targetDir, "*det*.onnx").FirstOrDefault()
                ?? throw new FileNotFoundException($"模型目录 [{targetDir}] 缺少检测模型 (*det*.onnx)");
            recPath = Directory.GetFiles(targetDir, "*rec*.onnx").FirstOrDefault()
                ?? throw new FileNotFoundException($"模型目录 [{targetDir}] 缺少识别模型 (*rec*.onnx)");
            dictPath = Directory.GetFiles(targetDir, "*dict*.txt").FirstOrDefault()
                ?? Directory.GetFiles(targetDir, "*.txt").FirstOrDefault();
            clsPath = Directory.GetFiles(targetDir, "*cls*.onnx").FirstOrDefault();
        }

        // 字典与方向分类器兜底策略：若未单独提供，自动在 models 目录下回退查找通用官方字典与方向分类模型
        dictPath ??= FindFallbackDictionaryPath();
        if (string.IsNullOrEmpty(dictPath) || !File.Exists(dictPath))
        {
            throw new FileNotFoundException(
                "[PaddleOcrV6Engine] 未找到 OCR 字符字典文件 (*dict*.txt)。\n" +
                "请检查 models 目录下是否存在 ppocrv6_dict.txt。");
        }

        clsPath ??= FindFallbackClassificationPath();
        if (string.IsNullOrEmpty(clsPath) || !File.Exists(clsPath))
        {
            throw new FileNotFoundException(
                "[PaddleOcrV6Engine] 未找到方向分类模型文件 (*cls*.onnx)。\n" +
                "请检查 models 目录下是否存在 ch_ppocr_mobile_v2.0_cls.onnx。");
        }

        using var detStream = File.OpenRead(detPath);
        using var recStream = File.OpenRead(recPath);
        using var dictStream = File.OpenRead(dictPath);
        using var clsStream = File.OpenRead(clsPath);

        try
        {
            _ocr = PaddleOcrAll.LoadAsync(
                detectionOnnx: detStream,
                classificationOnnx: clsStream,
                recognitionOnnx: recStream,
                dictionaryUtf8: dictStream,
                options: options).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (backend != OcrBackend.Cpu)
        {
            Debug.WriteLine($"[PaddleOcrV6Engine] GPU/Auto 后端初始化异常，自动回退至 CPU 后端: {ex.Message}");
            detStream.Position = 0;
            clsStream.Position = 0;
            recStream.Position = 0;
            dictStream.Position = 0;

            var cpuOptions = CreatePaddleOcrOptions(enableAngle, maxSideLen, OcrBackend.Cpu);
            _ocr = PaddleOcrAll.LoadAsync(
                detectionOnnx: detStream,
                classificationOnnx: clsStream,
                recognitionOnnx: recStream,
                dictionaryUtf8: dictStream,
                options: cpuOptions).GetAwaiter().GetResult();
        }
    }

    public static OcrBackend ParseOcrBackend(string? backend)
    {
        if (string.IsNullOrWhiteSpace(backend))
        {
            return OcrBackend.Auto;
        }

        if (string.Equals(backend, "Auto", StringComparison.OrdinalIgnoreCase) ||
            backend.Contains("Auto", StringComparison.OrdinalIgnoreCase) ||
            backend.Contains("自动", StringComparison.OrdinalIgnoreCase))
        {
            return OcrBackend.Auto;
        }

        if (string.Equals(backend, "GPU", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(backend, "Vulkan", StringComparison.OrdinalIgnoreCase) ||
            backend.Contains("Vulkan", StringComparison.OrdinalIgnoreCase) ||
            backend.Contains("GPU", StringComparison.OrdinalIgnoreCase))
        {
            return OcrBackend.Vulkan;
        }

        return OcrBackend.Cpu;
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods, "Sdcb.SimdPaddleOCR.Backends.Vulkan.GpuBackend", "Sdcb.SimdPaddleOCR")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties, "Sdcb.SimdPaddleOCR.Backends.Vulkan.VkDevice", "Sdcb.SimdPaddleOCR")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "GpuBackend and VkDevice members are preserved via DynamicDependency")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "VkDevice members are preserved via DynamicDependency")]
    public static OcrBackendProbeInfo ProbeBackendInfo()
    {
        if (s_cachedProbeInfo != null)
        {
            return s_cachedProbeInfo;
        }

        lock (s_probeLock)
        {
            if (s_cachedProbeInfo != null)
            {
                return s_cachedProbeInfo;
            }

            try
            {
                var asm = typeof(PaddleOcrAll).Assembly;
                var gpuBackendType = asm.GetType("Sdcb.SimdPaddleOCR.Backends.Vulkan.GpuBackend");
                if (gpuBackendType != null)
                {
                    var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                    var usesGpuMethod = gpuBackendType.GetMethod("UsesGpu", flags);
                    var tryGetDeviceMethod = gpuBackendType.GetMethod("TryGetDevice", flags);

                    bool autoSelectsGpu = usesGpuMethod?.Invoke(null, [OcrBackend.Auto]) is true;
                    object? vkDevice = tryGetDeviceMethod?.Invoke(null, null);

                    if (vkDevice != null)
                    {
                        var devType = vkDevice.GetType();
                        var instFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                        string? deviceName = devType.GetField("DeviceName", instFlags)?.GetValue(vkDevice) as string;
                        bool coopGemm = devType.GetProperty("CoopGemm", instFlags)?.GetValue(vkDevice) is true;
                        uint subgroupMin = devType.GetField("SubgroupMin", instFlags)?.GetValue(vkDevice) is uint sg ? sg : 0u;

                        s_cachedProbeInfo = new OcrBackendProbeInfo(
                            AutoSelectsGpu: autoSelectsGpu,
                            HasVulkanDevice: true,
                            GpuDeviceName: deviceName,
                            SupportsCoopMatrix: coopGemm,
                            SubgroupMin: subgroupMin);
                        return s_cachedProbeInfo;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PaddleOcrV6Engine] 探测 Vulkan 后端能力异常: {ex.Message}");
            }

            s_cachedProbeInfo = new OcrBackendProbeInfo(
                AutoSelectsGpu: false,
                HasVulkanDevice: false,
                GpuDeviceName: null,
                SupportsCoopMatrix: false,
                SubgroupMin: 0);
            return s_cachedProbeInfo;
        }
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields, "Sdcb.SimdPaddleOCR.Backends.Vulkan.GpuBackend", "Sdcb.SimdPaddleOCR")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties | DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods, "Sdcb.SimdPaddleOCR.Backends.Vulkan.VkDevice", "Sdcb.SimdPaddleOCR")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Preserved via DynamicDependency")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Preserved via DynamicDependency")]
    public static IReadOnlyList<DiscoveredGpuDevice> EnumerateAllGpuDevices()
    {
        if (s_cachedDiscoveredGpus != null)
        {
            return s_cachedDiscoveredGpus;
        }

        lock (s_probeLock)
        {
            if (s_cachedDiscoveredGpus != null)
            {
                return s_cachedDiscoveredGpus;
            }

            var list = new List<DiscoveredGpuDevice>();
            try
            {
                var asm = typeof(PaddleOcrAll).Assembly;
                var vkDeviceType = asm.GetType("Sdcb.SimdPaddleOCR.Backends.Vulkan.VkDevice");
                if (vkDeviceType != null)
                {
                    var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                    var createMethod = vkDeviceType.GetMethod("Create", flags, [typeof(uint)]);
                    var instFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                    for (uint i = 0; i < 8; i++)
                    {
                        try
                        {
                            object? dev = createMethod?.Invoke(null, [i]);
                            if (dev == null) break;

                            try
                            {
                                string? name = vkDeviceType.GetField("DeviceName", instFlags)?.GetValue(dev) as string;
                                bool coop = vkDeviceType.GetProperty("CoopGemm", instFlags)?.GetValue(dev) is true;
                                uint sg = vkDeviceType.GetField("SubgroupMin", instFlags)?.GetValue(dev) is uint s ? s : 0u;
                                uint vendor = vkDeviceType.GetField("VendorId", instFlags)?.GetValue(dev) is uint v ? v : 0u;

                                if (!string.IsNullOrWhiteSpace(name))
                                {
                                    list.Add(new DiscoveredGpuDevice((int)i, name, coop, sg, vendor));
                                }
                            }
                            finally
                            {
                                if (dev is IDisposable d)
                                {
                                    d.Dispose();
                                }
                            }
                        }
                        catch
                        {
                            // 超出显卡索引上限或无法初始化，终止遍历
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PaddleOcrV6Engine] 枚举物理 GPU 设备异常: {ex.Message}");
            }

            s_cachedDiscoveredGpus = list;
            return list;
        }
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields, "Sdcb.SimdPaddleOCR.Backends.Vulkan.GpuBackend", "Sdcb.SimdPaddleOCR")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties | DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods, "Sdcb.SimdPaddleOCR.Backends.Vulkan.VkDevice", "Sdcb.SimdPaddleOCR")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Preserved via DynamicDependency")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Preserved via DynamicDependency")]
    public static void SetSelectedGpuIndex(int deviceIndex)
    {
        lock (s_probeLock)
        {
            if (s_currentSelectedGpuIndex == deviceIndex)
            {
                return;
            }

            try
            {
                var asm = typeof(PaddleOcrAll).Assembly;
                var gpuBackendType = asm.GetType("Sdcb.SimdPaddleOCR.Backends.Vulkan.GpuBackend");
                var vkDeviceType = asm.GetType("Sdcb.SimdPaddleOCR.Backends.Vulkan.VkDevice");
                if (gpuBackendType != null && vkDeviceType != null)
                {
                    var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                    var sDeviceField = gpuBackendType.GetField("s_device", flags);
                    var sProbedField = gpuBackendType.GetField("s_probed", flags);

                    if (deviceIndex >= 0)
                    {
                        var createMethod = vkDeviceType.GetMethod("Create", flags, [typeof(uint)]);
                        object? newDev = createMethod?.Invoke(null, [(uint)deviceIndex]);
                        if (newDev != null)
                        {
                            var oldDev = sDeviceField?.GetValue(null) as IDisposable;
                            sDeviceField?.SetValue(null, newDev);
                            sProbedField?.SetValue(null, true);
                            oldDev?.Dispose();
                            s_currentSelectedGpuIndex = deviceIndex;
                            s_cachedProbeInfo = null;
                            return;
                        }
                    }
                    else
                    {
                        // 自动模式：清理旧设备，交由 GpuBackend 重新自动决策
                        var oldDev = sDeviceField?.GetValue(null) as IDisposable;
                        sDeviceField?.SetValue(null, null);
                        sProbedField?.SetValue(null, false);
                        oldDev?.Dispose();
                        s_currentSelectedGpuIndex = -1;
                        s_cachedProbeInfo = null;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PaddleOcrV6Engine] 切换指定 GPU 索引 ({deviceIndex}) 异常: {ex.Message}");
            }
        }
    }

    private static PaddleOcrOptions CreatePaddleOcrOptions(bool enableAngle, int maxSideLen, OcrBackend backend)
    {
        return new PaddleOcrOptions
        {
            UseDirectionClassification = enableAngle,
            Detector = new PaddleOcrDetectorOptions
            {
                LimitSideLength = maxSideLen > 0 ? maxSideLen : 960,
                Backend = backend
            },
            Recognizer = new PaddleOcrRecognizerOptions
            {
                Backend = backend
            },
            Classifier = new PaddleOcrClassifierOptions
            {
                Backend = backend
            }
        };
    }

    private static string ResolveTargetModelDirectory(string baseDir, string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return Path.Combine(baseDir, "PP-OCRv6_small");
        }

        // 1. 如果 modelName 是 Tiny
        if (string.Equals(modelName, "PP-OCRv6_tiny", StringComparison.OrdinalIgnoreCase) ||
            modelName.Contains("tiny", StringComparison.OrdinalIgnoreCase))
        {
            string tinyDir = Path.Combine(baseDir, "PP-OCRv6_tiny");
            if (Directory.Exists(tinyDir)) return tinyDir;

            string legacyTinyDir = Path.Combine(baseDir, "tiny");
            if (Directory.Exists(legacyTinyDir)) return legacyTinyDir;

            string v6TinyDir = Path.Combine(baseDir, "v6_tiny");
            if (Directory.Exists(v6TinyDir)) return v6TinyDir;
        }

        // 2. 如果 modelName 是 Small 或默认
        if (string.Equals(modelName, "PP-OCRv6_small", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(modelName, "PP-OCRv6", StringComparison.OrdinalIgnoreCase) ||
            modelName.Contains("small", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(modelName, "v6", StringComparison.OrdinalIgnoreCase))
        {
            string smallDir = Path.Combine(baseDir, "PP-OCRv6_small");
            if (Directory.Exists(smallDir)) return smallDir;

            string legacyV6Dir = Path.Combine(baseDir, "v6");
            if (Directory.Exists(legacyV6Dir)) return legacyV6Dir;
        }

        // 3. 如果 modelName 是 Medium
        if (string.Equals(modelName, "PP-OCRv6_medium", StringComparison.OrdinalIgnoreCase) ||
            modelName.Contains("medium", StringComparison.OrdinalIgnoreCase))
        {
            string mediumDir = Path.Combine(baseDir, "PP-OCRv6_medium");
            if (Directory.Exists(mediumDir)) return mediumDir;

            string legacyMediumDir = Path.Combine(baseDir, "medium");
            if (Directory.Exists(legacyMediumDir)) return legacyMediumDir;
        }

        // 4. 直接子目录匹配
        string direct = Path.Combine(baseDir, modelName);
        if (Directory.Exists(direct)) return direct;

        // 4. 根目录兜底（如果 baseDir 本身就包含 det*.onnx 与 rec*.onnx）
        if (Directory.Exists(baseDir) &&
            Directory.GetFiles(baseDir, "*det*.onnx").Length > 0 &&
            Directory.GetFiles(baseDir, "*rec*.onnx").Length > 0)
        {
            return baseDir;
        }

        // 默认返回 PP-OCRv6_small
        return Path.Combine(baseDir, "PP-OCRv6_small");
    }

    private static string? FindFallbackDictionaryPath()
    {
        string modelsDir = OcrModelProvider.ResolveModelsDirectory();
        if (!Directory.Exists(modelsDir)) return null;

        string smallDict = Path.Combine(modelsDir, "PP-OCRv6_small", "ppocrv6_dict.txt");
        if (File.Exists(smallDict)) return smallDict;

        string v6Dict = Path.Combine(modelsDir, "v6", "ppocrv6_dict.txt");
        if (File.Exists(v6Dict)) return v6Dict;

        string tinyDict = Path.Combine(modelsDir, "PP-OCRv6_tiny", "ppocrv6_dict_tiny.txt");
        if (File.Exists(tinyDict)) return tinyDict;

        string legacyTinyDict = Path.Combine(modelsDir, "tiny", "ppocrv6_dict_tiny.txt");
        if (File.Exists(legacyTinyDict)) return legacyTinyDict;

        var anyDict = Directory.GetFiles(modelsDir, "*dict*.txt", SearchOption.AllDirectories).FirstOrDefault();
        if (anyDict != null) return anyDict;

        return Directory.GetFiles(modelsDir, "*.txt", SearchOption.AllDirectories).FirstOrDefault();
    }

    private static string? FindFallbackClassificationPath()
    {
        string modelsDir = OcrModelProvider.ResolveModelsDirectory();
        if (!Directory.Exists(modelsDir)) return null;

        // 1. models 根目录下的公共共享方向分类模型 (首选)
        string rootCls = Path.Combine(modelsDir, "ch_ppocr_mobile_v2.0_cls.onnx");
        if (File.Exists(rootCls)) return rootCls;

        string simpleRootCls = Path.Combine(modelsDir, "cls.onnx");
        if (File.Exists(simpleRootCls)) return simpleRootCls;

        var anyRootCls = Directory.GetFiles(modelsDir, "*cls*.onnx", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (anyRootCls != null) return anyRootCls;

        // 2. 兼容子文件夹中的 cls 模型
        string smallCls = Path.Combine(modelsDir, "PP-OCRv6_small", "ch_ppocr_mobile_v2.0_cls.onnx");
        if (File.Exists(smallCls)) return smallCls;

        string v6Cls = Path.Combine(modelsDir, "v6", "ch_ppocr_mobile_v2.0_cls.onnx");
        if (File.Exists(v6Cls)) return v6Cls;

        string tinyCls = Path.Combine(modelsDir, "PP-OCRv6_tiny", "ch_ppocr_mobile_v2.0_cls.onnx");
        if (File.Exists(tinyCls)) return tinyCls;

        string legacyTinyCls = Path.Combine(modelsDir, "tiny", "ch_ppocr_mobile_v2.0_cls.onnx");
        if (File.Exists(legacyTinyCls)) return legacyTinyCls;

        return Directory.GetFiles(modelsDir, "*cls*.onnx", SearchOption.AllDirectories).FirstOrDefault();
    }

    public async Task<CoreOcrResult> RecognizeAsync(OcrImage image, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(image);

        var sw = Stopwatch.StartNew();
        try
        {
            var bitmap = image.Bitmap;
            if (bitmap == null || bitmap.IsNull || bitmap.Handle == IntPtr.Zero || bitmap.Width <= 0 || bitmap.Height <= 0)
            {
                return CoreOcrResult.Empty(TimeSpan.Zero);
            }

            // 1. 确定位图格式：优先直通 Bgra8888 (Windows 原生) 与 Rgba8888 原生物理内存
            SKBitmap workingBitmap = bitmap;
            bool disposeWorking = false;

            if (bitmap.ColorType != SKColorType.Bgra8888 && bitmap.ColorType != SKColorType.Rgba8888)
            {
                var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                var converted = new SKBitmap(info);
                using (var canvas = new SKCanvas(converted))
                {
                    canvas.DrawBitmap(bitmap, 0, 0, SKSamplingOptions.Default);
                }
                workingBitmap = converted;
                disposeWorking = true;
            }

            int width = workingBitmap.Width;
            int height = workingBitmap.Height;
            PaddleOcrResult rawResult;
            try
            {
                var pixelFormat = workingBitmap.ColorType == SKColorType.Rgba8888
                    ? ImagePixelFormat.Rgba32
                    : ImagePixelFormat.Bgra32;

                // 在后台线程调度纯 C# SIMD 推理，确保绝对不阻塞 UI 线程
                // 在工作线程栈上直接获取原生连续像素 Span，零中间内存分配与零拷贝！
                rawResult = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var pixelSpan = workingBitmap.GetPixelSpan();
                    return _ocr.Run(pixelSpan, width, height, format: pixelFormat);
                }, cancellationToken);
            }
            finally
            {
                if (disposeWorking)
                {
                    workingBitmap.Dispose();
                }
            }

            sw.Stop();

            if (rawResult.Lines == null || rawResult.Lines.Length == 0)
            {
                return CoreOcrResult.Empty(sw.Elapsed);
            }

            var blocks = new List<OcrTextBlock>(rawResult.Lines.Length);
            foreach (var line in rawResult.Lines)
            {
                if (string.IsNullOrWhiteSpace(line.Text))
                {
                    continue;
                }

                var box = line.Box;
                var boxPoints = new SKPointI[]
                {
                    new((int)Math.Round(box.X1), (int)Math.Round(box.Y1)),
                    new((int)Math.Round(box.X2), (int)Math.Round(box.Y2)),
                    new((int)Math.Round(box.X3), (int)Math.Round(box.Y3)),
                    new((int)Math.Round(box.X4), (int)Math.Round(box.Y4)),
                };

                int minX = (int)Math.Min(Math.Min(box.X1, box.X2), Math.Min(box.X3, box.X4));
                int minY = (int)Math.Min(Math.Min(box.Y1, box.Y2), Math.Min(box.Y3, box.Y4));
                int maxX = (int)Math.Max(Math.Max(box.X1, box.X2), Math.Max(box.X3, box.X4));
                int maxY = (int)Math.Max(Math.Max(box.Y1, box.Y2), Math.Max(box.Y3, box.Y4));

                var boundingBox = new SKRectI(
                    Math.Clamp(minX, 0, width),
                    Math.Clamp(minY, 0, height),
                    Math.Clamp(maxX, 0, width),
                    Math.Clamp(maxY, 0, height));

                blocks.Add(new OcrTextBlock
                {
                    Text = line.Text,
                    Confidence = line.RecognitionScore,
                    BoundingBox = boundingBox,
                    BoxPoints = boxPoints
                });
            }

            // 按规划文档第十六节要求进行 Y-X 几何拓扑排序拼接
            string formattedText = _sorter.SortAndFormat(blocks);

            return new CoreOcrResult
            {
                Text = formattedText,
                Blocks = blocks,
                Elapsed = sw.Elapsed,
                IsSuccess = true
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return CoreOcrResult.Failed(ex.Message, sw.Elapsed);
        }
    }

    public async Task WarmupAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            // 构造 128x64 微型位图并绘制简短字符，空跑 Det 与 Rec 全流水线，将 ONNX 算子与权重热入物理工作集
            using var warmupBmp = new SKBitmap(128, 64, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(warmupBmp))
            {
                canvas.Clear(SKColors.White);
                using var font = new SKFont { Size = 20 };
                using var paint = new SKPaint
                {
                    Color = SKColors.Black,
                    IsAntialias = true
                };
                canvas.DrawText("OCR", 16, 40, SKTextAlign.Left, font, paint);
            }

            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pixelSpan = warmupBmp.GetPixelSpan();
                _ocr.Run(pixelSpan, 128, 64, format: ImagePixelFormat.Bgra32);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PaddleOcrV6Engine] 引擎预热异常(可安全忽略): {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _ocr.Dispose();
            _disposed = true;
        }
    }
}
