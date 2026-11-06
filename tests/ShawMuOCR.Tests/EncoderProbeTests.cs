using MuRecorder.Interop;
using MuRecorder.Services;
using Xunit;

namespace ShawMuOCR.Tests;

/// <summary>
/// 编码器探针：把 Rust 核心 Auto 候选表的判定规则映射到 UI 文案。
///
/// 最要紧的一条是「未知 ≠ 不可用」：探测缓存里 <c>null</c> 表示还没探到或当时问不出来，
/// Rust 侧对这种编码器**照常尝试**，所以探针也必须继续往下选，不能把它当成"不支持"跳过。
/// </summary>
public class EncoderProbeTests
{
    [Fact]
    public void Auto_PrefersAv1WhenConfirmed()
    {
        var r = EncoderProbe.Describe(EncoderPreference.Auto, hasAv1: true, hasHevc: true, hasH264: true);

        Assert.Contains("AV1", r.Title);
        Assert.Contains("自动选择", r.Title);
        Assert.False(r.IsWarning);
        Assert.Contains("分片优先", r.Hint);
    }

    [Fact]
    public void Auto_Hint_StatesFragmentFirstInsteadOfFlatCodecChain()
    {
        // 回归守卫：旧文案是「AV1 → H.265 (HEVC) → H.264 (AVC) → CPU 软编」，
        // 编码器层面没错，但读起来像「AV1 会一路试到底才轮到 HEVC」。
        // Rust 的真实候选表是「编码器 × 容器」展开的 7 项，分片档全试完才进普通 MP4，
        // 所以 AV1 的普通 MP4 排在 H.264 的分片之后。提示行必须把「分片」和
        // 「普通 MP4」「CPU 软编」三个层次都点出来，否则用户对降级的预期是错的。
        var r = EncoderProbe.Describe(EncoderPreference.Auto, hasAv1: true, hasHevc: true, hasH264: true);

        Assert.Contains("分片优先", r.Hint);
        Assert.Contains("普通 MP4", r.Hint);
        Assert.Contains("CPU 软编", r.Hint);
        Assert.Contains(EncoderProbe.AutoCodecChain, r.Hint);
    }

    [Fact]
    public void Auto_FallsBackToHevcWhenAv1Missing()
    {
        var r = EncoderProbe.Describe(EncoderPreference.Auto, hasAv1: false, hasHevc: true, hasH264: true);

        Assert.Contains("HEVC", r.Title);
        Assert.Contains("AV1 不可用", r.Hint);
        Assert.False(r.IsWarning);
    }

    [Fact]
    public void Auto_HevcHint_DoesNotPutAv1BackIntoChain()
    {
        // AV1 已确认不可用，链里再从 AV1 起算等于给了个跑不到的起点
        var r = EncoderProbe.Describe(EncoderPreference.Auto, hasAv1: false, hasHevc: true, hasH264: true);

        Assert.DoesNotContain("AV1 →", r.Hint);
        Assert.Contains("分片优先", r.Hint);
    }

    [Fact]
    public void Auto_FallsBackToH264WhenAv1AndHevcMissing()
    {
        var r = EncoderProbe.Describe(EncoderPreference.Auto, hasAv1: false, hasHevc: false, hasH264: true);

        Assert.Contains("H.264", r.Title);
        Assert.Contains("AV1 与 HEVC 均不可用", r.Hint);
        Assert.Contains("分片优先", r.Hint);
    }

    [Fact]
    public void Auto_WarnsAndUsesSoftEncoderWhenNothingAvailable()
    {
        var r = EncoderProbe.Describe(EncoderPreference.Auto, hasAv1: false, hasHevc: false, hasH264: false);

        Assert.Contains("CPU", r.Title);
        Assert.Contains("软编", r.Title);
        // 全部硬编不可用是「会明显变慢、文件变大」的事，必须告警而不是静默降级
        Assert.True(r.IsWarning);
    }

    [Fact]
    public void Auto_TreatsUnknownAsTryableAndStillPrefersIt()
    {
        // 全 null = 还没预热完。此时不能报"不支持"，应继续按 AV1 首选并标注待确认。
        var r = EncoderProbe.Describe(EncoderPreference.Auto, hasAv1: null, hasHevc: null, hasH264: null);

        Assert.Contains("AV1", r.Title);
        Assert.Contains("待确认", r.Title);
        Assert.False(r.IsWarning);
    }

    [Fact]
    public void Auto_UnknownAv1DoesNotSkipToHevc()
    {
        // AV1 未知、HEVC 明确可用 —— Rust 仍会先试 AV1，探针必须跟着一致
        var r = EncoderProbe.Describe(EncoderPreference.Auto, hasAv1: null, hasHevc: true, hasH264: true);

        Assert.Contains("AV1", r.Title);
        Assert.DoesNotContain("HEVC", r.Title);
    }

    [Fact]
    public void Amf_UsesSameFallbackChainAsAuto()
    {
        var r = EncoderProbe.Describe(EncoderPreference.Amf, hasAv1: false, hasHevc: true, hasH264: true);

        Assert.Contains("AMF", r.Title);
        Assert.Contains("HEVC", r.Title);
    }

    [Fact]
    public void ManualGpu_ConfirmedAvailable_NoWarning()
    {
        var r = EncoderProbe.Describe(EncoderPreference.GpuHevc, hasAv1: null, hasHevc: true, hasH264: null);

        Assert.Contains("手动指定", r.Title);
        Assert.Contains("HEVC", r.Title);
        Assert.False(r.IsWarning);
    }

    [Fact]
    public void ManualGpu_ConfirmedMissing_WarnsAboutSoftFallback()
    {
        var r = EncoderProbe.Describe(EncoderPreference.GpuHevc, hasAv1: null, hasHevc: false, hasH264: null);

        Assert.Contains("未检测到", r.Title);
        Assert.Contains("回退", r.Hint);
        Assert.True(r.IsWarning);
    }

    [Fact]
    public void ManualGpu_Unknown_IsNotReportedAsMissing()
    {
        var r = EncoderProbe.Describe(EncoderPreference.GpuHevc, hasAv1: null, hasHevc: null, hasH264: null);

        Assert.Contains("待确认", r.Title);
        Assert.DoesNotContain("未检测到", r.Title);
        Assert.False(r.IsWarning);
    }

    [Fact]
    public void ManualSoft_DescribesSoftEncoder()
    {
        var r = EncoderProbe.Describe(EncoderPreference.Soft, hasAv1: true, hasHevc: true, hasH264: true);

        Assert.Contains("软编", r.Title);
        Assert.False(r.IsWarning);
    }
}
