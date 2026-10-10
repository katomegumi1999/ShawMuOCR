using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.OCR;
using ShawMuOCR.Windows.Win32;

namespace ShawMuOCR.Services;

public record ModelMemoryReport(
    double PhysicalWorkingSetMb,
    double PeakWorkingSetMb,
    double PrivateMemoryMb,
    double ManagedMemoryMb,
    double NativeEngineMemoryMb,
    uint GdiHandles,
    uint UserHandles,
    string ModelName,
    string ModelStatus,
    string ModelWeightsDescription,
    string EngineDescription,
    DateTime Timestamp,
    string CpuHardwareText = "",
    string CpuInstructionSetText = "",
    string GpuArchitectureText = "",
    string ConfiguredBackendText = "",
    string EffectiveBackendText = "",
    string EffectiveBackendBadgeText = "")
{
    public string SummaryText =>
        $"工作集: {PhysicalWorkingSetMb:F1} MB | 专用: {PrivateMemoryMb:F1} MB | 模型原生: {NativeEngineMemoryMb:F1} MB";
}

public interface IModelMemoryDiagnosticsService
{
    ModelMemoryReport GetReport();
    ModelMemoryReport TrimMemory();
}

public sealed class ModelMemoryDiagnosticsService : IModelMemoryDiagnosticsService
{
    private static readonly string s_cachedCpuName = DetectCpuName();
    private static readonly string s_cachedCpuSimd = DetectCpuSimdInstructionSets();

    private readonly ISettingsService _settingsService;
    private readonly IOcrModelProvider? _modelProvider;

    public ModelMemoryDiagnosticsService(ISettingsService settingsService, IOcrModelProvider? modelProvider = null)
    {
        _settingsService = settingsService;
        _modelProvider = modelProvider;
    }

    public ModelMemoryReport GetReport()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();

        long workingSet = process.WorkingSet64;
        long peakWorkingSet = process.PeakWorkingSet64;
        long privateMemory = process.PrivateMemorySize64;
        long managedMemory = GC.GetTotalMemory(false);
        long nativeMemory = Math.Max(0, privateMemory - managedMemory);

        uint gdiHandles = 0;
        uint userHandles = 0;
        try
        {
            gdiHandles = User32.GetGuiResources(process.Handle, User32.GR_GDIOBJECTS);
            userHandles = User32.GetGuiResources(process.Handle, User32.GR_USEROBJECTS);
        }
        catch
        {
            // 非 Windows 或受限权限降级
        }

        var current = _settingsService.Current;
        string activeModel = string.IsNullOrWhiteSpace(current.Model) ? "PP-OCRv6_small" : current.Model;
        string modelName;
        string modelWeights;
        string engineDesc;

        bool isBuiltIn = string.Equals(activeModel, "PP-OCRv6_small", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(activeModel, "PP-OCRv6_tiny", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(activeModel, "PP-OCRv6_medium", StringComparison.OrdinalIgnoreCase);

        var probe = PaddleOcrV6Engine.ProbeBackendInfo();
        var allGpus = PaddleOcrV6Engine.EnumerateAllGpuDevices();
        DiscoveredGpuDevice? manualGpu = current.SelectedGpuIndex >= 0
            ? allGpus.FirstOrDefault(g => g.Index == current.SelectedGpuIndex)
            : null;

        string configuredBackendText;
        string effectiveBackendText;
        string effectiveBackendBadge;

        string backendNormalized = (current.OcrBackend ?? "Auto").Trim();
        if (string.Equals(backendNormalized, "Auto", StringComparison.OrdinalIgnoreCase) ||
            backendNormalized.Contains("Auto", StringComparison.OrdinalIgnoreCase) ||
            backendNormalized.Contains("自动", StringComparison.OrdinalIgnoreCase))
        {
            configuredBackendText = manualGpu != null
                ? $"Auto (手动指定设备 #{manualGpu.Index}: {manualGpu.Name})"
                : "Auto (自动选择最优 · 推荐)";
            if (probe.AutoSelectsGpu)
            {
                string gpuName = manualGpu?.Name ?? probe.GpuDeviceName ?? "未知 GPU";
                effectiveBackendText = manualGpu != null
                    ? $"GPU 加速 (手动指定: {gpuName})"
                    : $"GPU 加速 (自动优选: {probe.GpuDeviceName})";
                effectiveBackendBadge = "⚡ GPU 加速 (Auto)";
            }
            else
            {
                effectiveBackendText = $"CPU 向量加速 (自动优选: {probe.AutoDecisionReason})";
                effectiveBackendBadge = "🖥️ CPU 优选 (Auto)";
            }
        }
        else if (string.Equals(backendNormalized, "GPU", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(backendNormalized, "Vulkan", StringComparison.OrdinalIgnoreCase) ||
                 backendNormalized.Contains("GPU", StringComparison.OrdinalIgnoreCase) ||
                 backendNormalized.Contains("Vulkan", StringComparison.OrdinalIgnoreCase))
        {
            configuredBackendText = manualGpu != null
                ? $"GPU (手动指定 Vulkan 设备 #{manualGpu.Index}: {manualGpu.Name})"
                : "GPU (Vulkan 强制硬件加速)";
            if (probe.HasVulkanDevice)
            {
                string gpuName = manualGpu?.Name ?? probe.GpuDeviceName ?? "未知 GPU";
                effectiveBackendText = $"GPU 加速 (Vulkan · {gpuName})";
                effectiveBackendBadge = "⚡ GPU 加速就绪";
            }
            else
            {
                effectiveBackendText = "纯 CPU (未检测到兼容 GPU，自动降级回退)";
                effectiveBackendBadge = "🖥️ CPU 回退";
            }
        }
        else
        {
            configuredBackendText = "CPU (纯 C# SIMD 模式)";
            effectiveBackendText = "纯 CPU 推理 (纯 C# SIMD 向量加速)";
            effectiveBackendBadge = "🖥️ CPU 向量加速";
        }

        string gpuArchText;
        if (probe.HasVulkanDevice)
        {
            var targetGpu = manualGpu ?? allGpus.FirstOrDefault();
            string gpuName = targetGpu?.Name ?? probe.GpuDeviceName ?? "Vulkan 兼容设备";
            bool coop = targetGpu?.SupportsCoopMatrix ?? probe.SupportsCoopMatrix;
            uint sgMin = targetGpu?.SubgroupMin ?? probe.SubgroupMin;
            string subgroupDesc = sgMin > 0 ? $" · Subgroup Wave{sgMin}" : string.Empty;
            string manualTag = manualGpu != null ? $" [用户指定 #{manualGpu.Index}]" : "";
            gpuArchText = coop
                ? $"{gpuName}{manualTag} (Vulkan · 硬件协作矩阵 Tensor Core / WMMA{subgroupDesc})"
                : $"{gpuName}{manualTag} (Vulkan · 计算着色器{subgroupDesc} · 无协作矩阵)";
        }
        else
        {
            gpuArchText = "未检测到支持 Vulkan 计算的物理 GPU 设备";
        }

        string backendText = (current.OcrBackend?.ToUpperInvariant()) switch
        {
            "GPU" or "VULKAN" => "GPU (Vulkan) 加速",
            "AUTO" => $"Auto (当前自动选择: {probe.AutoResolvedDisplayText})",
            _ => "纯C# SIMD / AVX2 加速"
        };

        string modelStatus = "引擎就绪";
        var provider = _modelProvider ?? new OcrModelProvider();
        bool isWebApi = string.Equals(current.ExternalModelType, "WebApi", StringComparison.OrdinalIgnoreCase) ||
                        activeModel.StartsWith("External:WebApi", StringComparison.OrdinalIgnoreCase);

        if (isWebApi)
        {
            modelName = $"外部接口 ({current.ExternalApiUrl})";
            modelWeights = "远程 / 自定义 Web 服务 (本地无模型权重占用)";
            engineDesc = $"External Web API ({current.ExternalApiUrl})";
        }
        else
        {
            var extModel = provider.ResolveExternalModel(activeModel);
            if (extModel == null)
            {
                var available = provider.GetAvailableModels();
                if (available.Count > 0)
                {
                    extModel = provider.ResolveExternalModel(available[0]);
                }
            }

            if (extModel == null)
            {
                modelName = "未检测到模型 (models 目录为空)";
                modelStatus = "⚠️ 缺少模型文件";
                modelWeights = "请将分发的 models 文件夹放置在与主程序同一目录下";
                engineDesc = $"SimdPaddleOCR ({backendText}, 状态: 未检测到可用模型)";
            }
            else if (isBuiltIn)
            {
                modelName = extModel.Name;
                modelWeights = modelName.Contains("tiny", StringComparison.OrdinalIgnoreCase)
                    ? "~10.2 MB (本地 Tiny DET+REC+CLS+字典)"
                    : modelName.Contains("medium", StringComparison.OrdinalIgnoreCase)
                        ? "~140 MB (本地 Medium DET+REC+CLS+字典)"
                        : "~15.9 MB (本地 Small DET+REC+CLS+字典)";
                string angleInfo = current.EnableAngleDetection ? "开启" : "关闭(低内存)";
                engineDesc = $"SimdPaddleOCR ({backendText}, 模型: {modelName}, 长边≤{current.MaxSideLen}, 角度:{angleInfo})";
            }
            else
            {
                string extDisplayName = extModel.Name;
                string targetPath = extModel.DirectoryPath;
                modelName = $"自定义模型 ({extDisplayName})";
                modelWeights = $"本地 ONNX 模型 ({targetPath})";
                string angleInfo = current.EnableAngleDetection ? "开启" : "关闭(低内存)";
                engineDesc = $"SimdPaddleOCR ({backendText}, 模型: {extDisplayName}, 长边≤{current.MaxSideLen}, 角度:{angleInfo})";
            }
        }

        return new ModelMemoryReport(
            PhysicalWorkingSetMb: Math.Round(workingSet / (1024.0 * 1024.0), 1),
            PeakWorkingSetMb: Math.Round(peakWorkingSet / (1024.0 * 1024.0), 1),
            PrivateMemoryMb: Math.Round(privateMemory / (1024.0 * 1024.0), 1),
            ManagedMemoryMb: Math.Round(managedMemory / (1024.0 * 1024.0), 1),
            NativeEngineMemoryMb: Math.Round(nativeMemory / (1024.0 * 1024.0), 1),
            GdiHandles: gdiHandles,
            UserHandles: userHandles,
            ModelName: modelName,
            ModelStatus: modelStatus,
            ModelWeightsDescription: modelWeights,
            EngineDescription: engineDesc,
            Timestamp: DateTime.Now,
            CpuHardwareText: s_cachedCpuName,
            CpuInstructionSetText: s_cachedCpuSimd,
            GpuArchitectureText: gpuArchText,
            ConfiguredBackendText: configuredBackendText,
            EffectiveBackendText: effectiveBackendText,
            EffectiveBackendBadgeText: effectiveBackendBadge);
    }

    private static string DetectCpuName()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var regVal = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                    "ProcessorNameString",
                    null) as string;
                if (!string.IsNullOrWhiteSpace(regVal))
                {
                    return $"{regVal.Trim()} ({Environment.ProcessorCount} 逻辑核心)";
                }
            }
        }
        catch
        {
            // 忽略注册表读取异常
        }

        var env = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return $"{env.Trim()} ({Environment.ProcessorCount} 逻辑核心)";
        }

        return $"{Environment.ProcessorCount} 逻辑核心处理器";
    }

    private static string DetectCpuSimdInstructionSets()
    {
        var list = new List<string>(6);
        if (X86Base.IsSupported)
        {
            if (Avx512F.IsSupported) list.Add("AVX-512");
            if (Avx2.IsSupported) list.Add("AVX2");
            if (Fma.IsSupported) list.Add("FMA");
            if (Bmi2.IsSupported) list.Add("BMI2");
            if (list.Count == 0 && Avx.IsSupported) list.Add("AVX");
            if (list.Count == 0 && Sse42.IsSupported) list.Add("SSE4.2");
            if (list.Count == 0 && Sse2.IsSupported) list.Add("SSE2");
        }
        else if (ArmBase.IsSupported)
        {
            if (AdvSimd.IsSupported) list.Add("NEON");
            if (ArmBase.Arm64.IsSupported) list.Add("ARM64");
        }

        string instructionString = list.Count > 0 ? string.Join(" / ", list) : "标量";

        string width = (X86Base.IsSupported && Avx512F.IsSupported) ? "512-bit" :
                       (X86Base.IsSupported && Avx2.IsSupported) ? "256-bit" :
                       ((ArmBase.IsSupported && AdvSimd.IsSupported) || (X86Base.IsSupported && Sse2.IsSupported)) ? "128-bit" :
                       Vector.IsHardwareAccelerated ? "Vector" : "标量";

        return $"{instructionString} ({width} SIMD 硬件加速)";
    }

    public ModelMemoryReport TrimMemory()
    {
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);

        try
        {
            using var process = Process.GetCurrentProcess();
            User32.EmptyWorkingSet(process.Handle);
        }
        catch
        {
            // 忽略非致命异常
        }

        return GetReport();
    }
}
