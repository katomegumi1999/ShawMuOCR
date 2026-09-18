using System;
using System.Diagnostics;
using System.IO;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Settings;
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
    DateTime Timestamp)
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
        string activeModel = current.Model;
        string modelName;
        string modelWeights;
        string engineDesc;

        bool isBuiltIn = string.Equals(activeModel, "PP-OCRv6_small", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(activeModel, "PP-OCRv6_tiny", StringComparison.OrdinalIgnoreCase);

        if (isBuiltIn)
        {
            modelName = activeModel;
            modelWeights = modelName.Contains("tiny", StringComparison.OrdinalIgnoreCase)
                ? "~10.2 MB (嵌入式 Tiny DET+REC+CLS+字典)"
                : "~15.9 MB (嵌入式 Small DET+REC+CLS+字典)";
            string angleInfo = current.EnableAngleDetection ? "开启" : "关闭(低内存)";
            engineDesc = $"SimdPaddleOCR (纯C# SIMD / AVX2 NHWC加速, 内置 {modelName}, 长边≤{current.MaxSideLen}, 角度:{angleInfo})";
        }
        else
        {
            var extModel = _modelProvider?.ResolveExternalModel(activeModel);
            bool isWebApi = (current.ScanExternalModels && string.Equals(current.ExternalModelType, "WebApi", StringComparison.OrdinalIgnoreCase)) ||
                            activeModel.StartsWith("External:WebApi", StringComparison.OrdinalIgnoreCase);

            if (isWebApi)
            {
                modelName = $"外部接口 ({current.ExternalApiUrl})";
                modelWeights = "远程 / 自定义 Web 服务 (本地无模型权重占用)";
                engineDesc = $"External Web API ({current.ExternalApiUrl})";
            }
            else
            {
                string extDisplayName = extModel?.Name ?? activeModel;
                string targetPath = extModel?.DirectoryPath ?? current.ExternalModelPath;
                modelName = $"外部自定义模型 ({extDisplayName})";
                modelWeights = $"本地自定义 ONNX 模型 ({targetPath})";
                string angleInfo = current.EnableAngleDetection ? "开启" : "关闭(低内存)";
                engineDesc = $"SimdPaddleOCR (纯C# SIMD / AVX2, 自定义模型: {extDisplayName}, 长边≤{current.MaxSideLen}, 角度:{angleInfo})";
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
            ModelStatus: "引擎就绪",
            ModelWeightsDescription: modelWeights,
            EngineDescription: engineDesc,
            Timestamp: DateTime.Now);
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
