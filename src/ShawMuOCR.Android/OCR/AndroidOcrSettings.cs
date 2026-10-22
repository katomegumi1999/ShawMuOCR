using System;
using Sdcb.SimdPaddleOCR;

namespace ShawMuOCR.Android.OCR;

public sealed class AndroidOcrSettings
{
    // 默认 Auto 自动匹配：在骁龙 Adreno 上自动启用 Vulkan (250ms)，在天玑 Mali 上自动启用全大核 CPU (1s)
    public string Backend { get; set; } = "Auto"; // "Auto", "CPU", "GPU"

    // 算子线程数：锁定 4 大核
    public int DetIntraOpThreads { get; set; } = 4;
    // 对齐官方测试 --workers 4 并发参数
    public int LineWorkerCount { get; set; } = 4;
    // 对齐官方 GPU 最佳输入长边 960
    public int MaxSideLen { get; set; } = 960;
    // 完整功能保留：方向分类
    public bool EnableAngle { get; set; } = true;

    public OcrBackend ResolveOcrBackend()
    {
        if (string.Equals(Backend, "CPU", StringComparison.OrdinalIgnoreCase))
        {
            return OcrBackend.Cpu;
        }

        if (string.Equals(Backend, "GPU", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Backend, "Vulkan", StringComparison.OrdinalIgnoreCase))
        {
            return OcrBackend.Vulkan;
        }

        return OcrBackend.Auto;
    }
}

public sealed record OcrTextLine(string Text, float Score);

public sealed record OcrExecutionResult(
    string FullText,
    IReadOnlyList<OcrTextLine> Lines,
    long ElapsedMilliseconds,
    string ModelName,
    string BackendUsed);
