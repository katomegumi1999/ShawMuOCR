using MuRecorder.Interop;

namespace MuRecorder.Services;

/// <summary>
/// 编码器探针结果。三段分开给，是为了界面能分主次排版（标题半粗、提示小字、警告染色）。
/// </summary>
public readonly record struct EncoderProbeResult(string Title, string Hint, bool IsWarning);

/// <summary>
/// 「自动检测」到底会选哪个编码格式 —— 在点录制**之前**就算出来给用户看。
///
/// 为什么可以在 C# 侧推断，而不是等录制开始后读 stats：
/// Rust 核心的 Auto 候选表是按「编码器 × 容器模式」展开的固定顺序
/// （编码器档位 AV1 → HEVC → H.264，容器档位**分片优先**、普通 MP4 垫后），
/// 并用已缓存的探测结果把「确认不可用」的候选剔除（`src/recorder/mod.rs` 的
/// `filter_candidates_by_cached_probe`）。注意它把 `None`（没探测过 / 问不出来）
/// **当作可能可用**照常尝试，所以这里的判定必须是「!= Some(false)」而不是「== Some(true)」，
/// 否则会把「尚未探测完」错报成「不可用」。
///
/// 已知局限（不要试图在 C# 侧消除）：探测说支持、实际建链仍可能失败
/// （驱动版本、分辨率对齐、显存不足等），那种情况下 Rust 会继续往下一个候选降级。
/// 所以文案一律用「预计」，并在提示里说明以启动结果为准。
/// </summary>
public static class EncoderProbe
{
    /// <summary>自动链的编码器档位顺序，与 Rust 候选表一致（容器分档另算，见下）。</summary>
    public const string AutoCodecChain = "AV1 → H.265 (HEVC) → H.264 (AVC)";

    /// <summary>
    /// 完整的降级顺序，**必须体现「分片优先」**。
    ///
    /// Rust 侧不是简单地按「AV1 → HEVC → H.264 → 软编」走，而是按「编码器 × 容器模式」
    /// 展开成 7 个候选（`recorder/mod.rs` 的 Auto 分支）：
    /// AV1/HEVC/H.264 各自的分片档 → H.264 的 MF 分片档 → 三者的普通 MP4 档。
    /// 也就是说 **AV1 的普通 MP4 排在 H.264 的分片之后** —— 保住「分片」这个能力，
    /// 优先于保住编码器档位（分片能抗崩溃、能录制中切片）。
    /// 旧文案只写「AV1 → HEVC → H.264 → CPU 软编」，会让人以为 AV1 会一路试到底再轮到 HEVC。
    /// </summary>
    public static readonly string AutoFallbackChain = ChainFrom(AutoCodecChain);

    /// <summary>
    /// 从指定编码器起算的降级链文案。AV1 已被确认不可用时，链里不该再出现 AV1。
    /// </summary>
    private static string ChainFrom(string codecChain) =>
        $"分片优先：{codecChain}；分片都不成才退普通 MP4；最后 CPU 软编";

    public static EncoderProbeResult Describe(
        EncoderPreference selected,
        bool? hasAv1,
        bool? hasHevc,
        bool? hasH264)
    {
        // Auto 与 Amf 在 Rust 侧走同一张候选表（`_ =>` 分支），这里一并按自动链处理。
        if (selected is EncoderPreference.Auto or EncoderPreference.Amf)
        {
            return DescribeAuto(selected, hasAv1, hasHevc, hasH264);
        }

        return selected switch
        {
            EncoderPreference.Soft => new EncoderProbeResult(
                "手动指定：CPU · H.264（软编）",
                "不依赖显卡，兼容性优先；CPU 占用与文件体积都高于硬编。",
                IsWarning: false),

            EncoderPreference.GpuAv1 => DescribeManual("GPU · AV1（硬件加速）", "AV1", hasAv1),
            EncoderPreference.GpuHevc => DescribeManual("GPU · H.265 (HEVC)（硬件加速）", "H.265 (HEVC)", hasHevc),
            EncoderPreference.GpuH264 => DescribeManual("GPU · H.264 (AVC)（硬件加速）", "H.264 (AVC)", hasH264),

            _ => new EncoderProbeResult(
                "未知的编码器选项",
                "将按自动降级链尝试。",
                IsWarning: false),
        };
    }

    private static EncoderProbeResult DescribeAuto(
        EncoderPreference selected,
        bool? hasAv1,
        bool? hasHevc,
        bool? hasH264)
    {
        string prefix = selected == EncoderPreference.Amf ? "AMD AMF（走自动降级链）：" : "自动选择：";

        // 按 Rust 候选表顺序，取第一个「未被确认不可用」的格式
        if (hasAv1 != false)
        {
            return hasAv1 == true
                ? new EncoderProbeResult($"{prefix}GPU · AV1（硬件加速）", AutoFallbackChain, false)
                : new EncoderProbeResult($"{prefix}GPU · AV1（待确认）", "硬件探测尚未完成，将以启动时的实际建链结果为准。", false);
        }

        if (hasHevc != false)
        {
            return hasHevc == true
                ? new EncoderProbeResult($"{prefix}GPU · H.265 (HEVC)（硬件加速）", $"AV1 不可用；{ChainFrom("H.265 (HEVC) → H.264 (AVC)")}", false)
                : new EncoderProbeResult($"{prefix}GPU · H.265 (HEVC)（待确认）", "AV1 不可用，HEVC 尚未探测完成。", false);
        }

        if (hasH264 != false)
        {
            return hasH264 == true
                ? new EncoderProbeResult($"{prefix}GPU · H.264 (AVC)（硬件加速）", $"AV1 与 HEVC 均不可用；{ChainFrom("H.264 (AVC)")}", false)
                : new EncoderProbeResult($"{prefix}GPU · H.264 (AVC)（待确认）", "AV1 与 HEVC 均不可用，H.264 尚未探测完成。", false);
        }

        return new EncoderProbeResult(
            $"{prefix}CPU · H.264（软编）",
            "本机未检测到任何可用的硬件编码器；软编 CPU 占用更高、文件也更大。",
            IsWarning: true);
    }

    private static EncoderProbeResult DescribeManual(string displayName, string shortName, bool? available)
    {
        return available switch
        {
            true => new EncoderProbeResult(
                $"手动指定：{displayName}",
                "该编码器已确认可用，启动录制时不会降级。",
                IsWarning: false),

            false => new EncoderProbeResult(
                $"手动指定：{displayName}，但本机未检测到",
                "启动录制时会回退到 CPU 软编；建议改选「自动检测（智能降级）」",
                IsWarning: true),

            // null：还没探测完，或探测当时问不出来。绝不能写成「不可用」。
            _ => new EncoderProbeResult(
                $"手动指定：{displayName}（支持情况待确认）",
                $"{shortName} 的后台探测尚未完成。",
                IsWarning: false),
        };
    }
}
