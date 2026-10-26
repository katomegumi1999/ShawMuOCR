using ShawMuOCR.Core.Capture;
using SkiaSharp;
using Xunit;

namespace ShawMuOCR.Tests;

/// <summary>
/// 长截图两块核心逻辑的验证：位移估计器与拼接器。
/// 用「一张超高文档按固定步长切视口」的方式合成滚动帧，这样每一帧的真实位移已知，
/// 拼出来的结果也能和原文档逐字节比对——比抓真实窗口可靠得多。
/// </summary>
public class LongScreenshotStitcherTests
{
    private const int DocWidth = LongScreenshotTestData.DocWidth;
    private const int ViewportHeight = LongScreenshotTestData.ViewportHeight;

    // ========================= 位移估计 =========================

    [Theory]
    [InlineData(5)]
    [InlineData(17)]
    [InlineData(60)]
    [InlineData(137)]
    [InlineData(300)]
    public void Estimate_KnownShift_ReturnsExactShift(int shift)
    {
        using var document = LongScreenshotTestData.CreateDocument();
        using var previous = LongScreenshotTestData.CutFrame(document, 0);
        using var next = LongScreenshotTestData.CutFrame(document, shift);

        var estimate = VerticalShiftEstimator.Estimate(previous, next);

        Assert.True(estimate.IsConfident, estimate.Reason);
        Assert.Equal(shift, estimate.ShiftPixels);
    }

    [Fact]
    public void Estimate_IdenticalFrames_ReturnsZeroAndConfident()
    {
        using var document = LongScreenshotTestData.CreateDocument();
        using var frame = LongScreenshotTestData.CutFrame(document, 0);

        var estimate = VerticalShiftEstimator.Estimate(frame, frame);

        Assert.True(estimate.IsConfident, estimate.Reason);
        Assert.Equal(0, estimate.ShiftPixels);
    }

    [Fact]
    public void Estimate_UnrelatedFrames_IsNotConfident()
    {
        // 内容结构完全不同（横条纹 vs 类文档），模拟「用户把窗口切走了」
        using var document = LongScreenshotTestData.CreateDocument();
        using var frameA = LongScreenshotTestData.CutFrame(document, 0);
        using var frameB = LongScreenshotTestData.CreateStripedFrame();

        var estimate = VerticalShiftEstimator.Estimate(frameA, frameB);

        Assert.False(estimate.IsConfident);
        Assert.Contains("差异过大", estimate.Reason);
    }

    [Fact]
    public void Estimate_SizeMismatch_IsNotConfident()
    {
        using var document = LongScreenshotTestData.CreateDocument();
        using var a = LongScreenshotTestData.CutFrame(document, 0);
        using var b = new SKBitmap(new SKImageInfo(ViewportHeight, ViewportHeight, SKColorType.Bgra8888, SKAlphaType.Premul));

        var estimate = VerticalShiftEstimator.Estimate(a, b);

        Assert.False(estimate.IsConfident);
        Assert.Contains("尺寸", estimate.Reason);
    }

    [Fact]
    public void Estimate_NullFrames_IsNotConfident()
    {
        var estimate = VerticalShiftEstimator.Estimate(null, null);

        Assert.False(estimate.IsConfident);
        Assert.Contains("空", estimate.Reason);
    }

    [Fact]
    public void Estimate_ShiftBeyondDetectableRange_IsNotConfident()
    {
        // 位移超过 0.65 倍帧高时模板带整条离开视野，必须报不可信而不是给个钳制值
        using var document = LongScreenshotTestData.CreateDocument();
        using var previous = LongScreenshotTestData.CutFrame(document, 0);
        using var next = LongScreenshotTestData.CutFrame(document, 590); // 590 > 600*0.65 = 390

        var estimate = VerticalShiftEstimator.Estimate(previous, next);

        Assert.False(estimate.IsConfident);
    }

    // ========================= 拼接 =========================

    [Fact]
    public void AppendFirst_SeedsCanvasWithViewportHeight()
    {
        using var document = LongScreenshotTestData.CreateDocument();
        using var frame = LongScreenshotTestData.CutFrame(document, 0);
        using var stitcher = new LongScreenshotStitcher(DocWidth, ViewportHeight);

        Assert.True(stitcher.AppendFirst(frame));

        Assert.Equal(DocWidth, stitcher.Width);
        Assert.Equal(ViewportHeight, stitcher.RenderedHeight);
        Assert.Equal(1, stitcher.FrameCount);
    }

    [Fact]
    public void Stitch_KnownScrolling_ReconstructsDocumentExactly()
    {
        // 端到端：切 7 帧、每帧滚 137 像素，拼出来应与原文档前 1422 行逐字节一致
        const int shift = 137;
        const int frameCount = 7;

        using var document = LongScreenshotTestData.CreateDocument();
        using var stitcher = new LongScreenshotStitcher(DocWidth, ViewportHeight);

        using (var first = LongScreenshotTestData.CutFrame(document, 0))
        {
            stitcher.AppendFirst(first);
        }

        for (int i = 1; i < frameCount; i++)
        {
            using var frame = LongScreenshotTestData.CutFrame(document, i * shift);
            bool appended = stitcher.TryAppendScrolled(frame, shift, out string? reason);
            Assert.True(appended, reason);
        }

        int expectedHeight = ViewportHeight + (frameCount - 1) * shift;
        Assert.Equal(expectedHeight, stitcher.RenderedHeight);

        using var result = stitcher.BuildResult();
        Assert.Equal(DocWidth, result.Width);
        Assert.Equal(expectedHeight, result.Height);

        // 与原文档对应区域逐字节比对
        byte[] expected = LongScreenshotTestData.ReadRows(document, 0, expectedHeight);
        byte[] actual = LongScreenshotTestData.ReadRows(result, 0, expectedHeight);
        Assert.Equal(expected.Length, actual.Length);

        int firstMismatch = -1;
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
            {
                firstMismatch = i;
                break;
            }
        }

        Assert.True(firstMismatch < 0,
            firstMismatch < 0
                ? string.Empty
                : $"第 {firstMismatch / 4} 个像素起不一致（行 {firstMismatch / (DocWidth * 4)}）");
    }

    [Fact]
    public void Stitch_EstimatorPlusStitcher_EndToEnd()
    {
        // 更贴近真实用法的组合：位移由估计器算，不靠测试喂真值
        using var document = LongScreenshotTestData.CreateDocument();
        using var stitcher = new LongScreenshotStitcher(DocWidth, ViewportHeight);

        using var first = LongScreenshotTestData.CutFrame(document, 0);
        stitcher.AppendFirst(first);

        SKBitmap previous = LongScreenshotTestData.CutFrame(document, 0);
        try
        {
            int offset = 0;
            for (int i = 1; i <= 5; i++)
            {
                offset += 173;
                using var current = LongScreenshotTestData.CutFrame(document, offset);

                var estimate = VerticalShiftEstimator.Estimate(previous, current);
                Assert.True(estimate.IsConfident, estimate.Reason);
                Assert.Equal(173, estimate.ShiftPixels);

                Assert.True(stitcher.TryAppendScrolled(current, estimate.ShiftPixels, out string? reason), reason);

                previous.Dispose();
                previous = current.Copy();
            }
        }
        finally
        {
            previous.Dispose();
        }

        int expectedHeight = ViewportHeight + 5 * 173;
        using var result = stitcher.BuildResult();
        Assert.Equal(expectedHeight, result.Height);

        byte[] expected = LongScreenshotTestData.ReadRows(document, 0, expectedHeight);
        byte[] actual = LongScreenshotTestData.ReadRows(result, 0, expectedHeight);
        Assert.True(expected.AsSpan().SequenceEqual(actual), "估计器 + 拼接器的结果与原文档不一致");
    }

    [Fact]
    public void TryAppendScrolled_ZeroShift_DoesNotGrowCanvas()
    {
        using var document = LongScreenshotTestData.CreateDocument();
        using var stitcher = new LongScreenshotStitcher(DocWidth, ViewportHeight);
        using var frame = LongScreenshotTestData.CutFrame(document, 0);
        stitcher.AppendFirst(frame);

        bool appended = stitcher.TryAppendScrolled(frame, 0, out string? reason);

        Assert.False(appended);
        Assert.Equal(ViewportHeight, stitcher.RenderedHeight);
        Assert.Contains("没有滚动", reason);
    }

    [Fact]
    public void TryAppendScrolled_NegativeShift_IsIgnoredAndCounted()
    {
        using var document = LongScreenshotTestData.CreateDocument();
        using var stitcher = new LongScreenshotStitcher(DocWidth, ViewportHeight);
        using var frame = LongScreenshotTestData.CutFrame(document, 0);
        stitcher.AppendFirst(frame);

        bool appended = stitcher.TryAppendScrolled(frame, -42, out string? reason);

        Assert.False(appended);
        Assert.Equal(ViewportHeight, stitcher.RenderedHeight);
        Assert.Equal(1, stitcher.IgnoredScrollBackCount);
        Assert.Contains("回滚", reason);
    }

    [Fact]
    public void TryAppendScrolled_WithoutFirstFrame_ReturnsFalse()
    {
        using var document = LongScreenshotTestData.CreateDocument();
        using var stitcher = new LongScreenshotStitcher(DocWidth, ViewportHeight);
        using var frame = LongScreenshotTestData.CutFrame(document, 0);

        bool appended = stitcher.TryAppendScrolled(frame, 10, out string? reason);

        Assert.False(appended);
        Assert.Contains("首帧", reason);
    }

    [Fact]
    public void TryAppendScrolled_ReachingMaxHeight_StopsAndReports()
    {
        const int maxHeight = 1000;
        using var document = LongScreenshotTestData.CreateDocument();
        using var stitcher = new LongScreenshotStitcher(DocWidth, ViewportHeight, maxHeight);

        using var first = LongScreenshotTestData.CutFrame(document, 0);
        stitcher.AppendFirst(first);

        bool appended = false;
        string? reason = null;
        for (int i = 1; i <= 6 && !stitcher.IsFull; i++)
        {
            using var frame = LongScreenshotTestData.CutFrame(document, i * 137);
            appended = stitcher.TryAppendScrolled(frame, 137, out reason);
        }

        Assert.True(stitcher.IsFull);
        Assert.Equal(maxHeight, stitcher.RenderedHeight);
        // 达到上限的那一帧可能部分写入（被截断到最后剩余行），也可能直接拒绝
        Assert.True(appended || reason is not null);
    }

    [Fact]
    public void TryAppendScrolled_FrameWidthMismatch_ReturnsFalse()
    {
        using var document = LongScreenshotTestData.CreateDocument();
        using var stitcher = new LongScreenshotStitcher(DocWidth, ViewportHeight);
        using var first = LongScreenshotTestData.CutFrame(document, 0);
        stitcher.AppendFirst(first);

        using var narrow = new SKBitmap(new SKImageInfo(DocWidth - 10, ViewportHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        bool appended = stitcher.TryAppendScrolled(narrow, 50, out string? reason);

        Assert.False(appended);
        Assert.Contains("宽度", reason);
    }

    [Fact]
    public void BuildResult_TrimsUnfilledBottom()
    {
        using var document = LongScreenshotTestData.CreateDocument();
        using var stitcher = new LongScreenshotStitcher(DocWidth, 5000);
        using var frame = LongScreenshotTestData.CutFrame(document, 0);
        stitcher.AppendFirst(frame);

        using var result = stitcher.BuildResult();

        // 画布初始 5000 高（视口高取 5000），但实际只拼了 600 行
        Assert.Equal(ViewportHeight, result.Height);
    }

    [Fact]
    public void Constructor_InvalidSize_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LongScreenshotStitcher(0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LongScreenshotStitcher(100, 0));
    }

}
