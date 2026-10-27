using SkiaSharp;

namespace ShawMuOCR.Core.Capture;

/// <summary>垂直位移估计结果。<see cref="ShiftPixels"/> 为正表示内容向上移动（用户向下滚动）。</summary>
public readonly record struct ShiftEstimate(
    int ShiftPixels,
    bool IsConfident,
    double BestScore,
    double RunnerUpScore,
    string Reason)
{
    /// <summary>位移为 0 且不可信的拒绝结果。</summary>
    public static ShiftEstimate Rejected(string reason) => new(0, false, double.NaN, double.NaN, reason);
}

/// <summary>
/// 估计相邻两帧之间的垂直滚动位移。
/// <para>
/// 两步走：
/// <list type="number">
/// <item>行投影粗定位——把每一行压成一个平均亮度值，得到一条长度等于帧高的一维信号，
/// 在下一帧的行投影里滑窗找上一帧模板带落到哪一行。一维滑窗天然是 1 像素精度，成本极低。</item>
/// <item>二维 SAD 复核——对粗定位给出的前几个候选，用真实像素算绝对差，挑出真正最好的那个。
/// 这一步是为了排除「等距行高导致的周期性混叠」：纯行投影在等行距文本上会出现多个几乎一样好的峰。</item>
/// </list>
/// </para>
/// <para>
/// 为什么不用「先按 8 像素步长做二维粗搜再精修」：那种做法只在位移恰好是步长整数倍时能对上，
/// 因为模板带与候选区域采样的是同一组行号错位后的行，位移一旦不是步长倍数就整条带错行，
/// 分数会高得像完全无关的画面，最优解反而被随机位置抢走。行投影没有这个问题。
/// </para>
/// <para>
/// 模板带取在帧高的 65% 处而不是顶部：内容向上滚动时这条带在新帧里整体上移，带越靠上可检测的
/// 位移上限越小——取顶部的话位移稍大就整条带离开视野，估计直接失效。取 65% 处，单帧最多可检测
/// 0.65 倍帧高的前向位移（1080p 约 700 像素），远超人手滚一帧的量级，还留有少量反向余量。
/// </para>
/// </summary>
public static class VerticalShiftEstimator
{
    /// <summary>模板带顶部位置（占帧高比例）。</summary>
    private const double BandTopRatio = 0.65;

    /// <summary>模板带高度（占帧高比例）。</summary>
    private const double BandHeightRatio = 0.25;

    /// <summary>行投影的横向采样步长。取 16 是因为平均掉横向细节后仍足够稳定，成本却减半。</summary>
    private const int ProfileStepX = 16;

    /// <summary>候选上限：投影分数落在容差内的位置最多验这么多个，防止等距内容把候选表撑爆。</summary>
    private const int MaxCandidates = 12;

    /// <summary>候选容差的固定部分（灰度平均差）。</summary>
    private const double CandidateToleranceBase = 2.0;

    /// <summary>候选容差随最优分数放大的比例。</summary>
    private const double CandidateToleranceRatio = 0.25;

    /// <summary>二维复核的横向采样步长。</summary>
    private const int VerifyStepX = 8;

    /// <summary>二维复核的纵向采样步长。隔 4 行取样已足够区分 1 像素级别的错位。</summary>
    private const int VerifyStepY = 4;

    /// <summary>判定「同一内容」的采样点平均绝对差上限（0~255 灰度）。</summary>
    private const double MaxMeanDifference = 42.0;

    /// <summary>与最优解相距多少像素以外才算是「另一个真实候选」。</summary>
    private const int DistinctCandidateDistance = 16;

    /// <summary>最优解必须比次优解好到这个比例以内，否则视为不可信（重复纹理导致的误匹配）。</summary>
    private const double ConfidenceRatio = 0.75;

    /// <summary>
    /// 估计 <paramref name="previous"/> → <paramref name="next"/> 的垂直位移。
    /// 尺寸不一致、像素不可读、内容差异过大时返回 <see cref="ShiftEstimate.IsConfident"/> 为 false 的结果。
    /// </summary>
    public static ShiftEstimate Estimate(SKBitmap? previous, SKBitmap? next)
    {
        if (previous is null || next is null)
        {
            return ShiftEstimate.Rejected("帧为空");
        }

        int width = previous.Width;
        int height = previous.Height;
        if (next.Width != width || next.Height != height)
        {
            return ShiftEstimate.Rejected("两帧尺寸不一致");
        }

        if (width < ProfileStepX * 4 || height < 16)
        {
            return ShiftEstimate.Rejected("帧过小，无法估计位移");
        }

        int bandHeight = Math.Max(4, (int)(height * BandHeightRatio));
        int maxCandidateTop = height - bandHeight;
        if (maxCandidateTop <= 0)
        {
            return ShiftEstimate.Rejected("帧高不足一个模板带");
        }

        // 尝试在 0.35 ~ 0.75 帧高范围内挑选最具特征（亮度对比度最大）的模板带
        // 优先在下半部分（0.65），若该区域为纯白/纯色留白则向上寻找具有文本/边缘的带
        int defaultBandTop = Math.Clamp((int)(height * BandTopRatio), 0, maxCandidateTop);
        int bestBandTop = defaultBandTop;
        int bestBandContrast = -1;
        byte[]? bestPreviousBandProfile = null;

        int[] trialTops = new[]
        {
            defaultBandTop,
            Math.Clamp((int)(height * 0.50), 0, maxCandidateTop),
            Math.Clamp((int)(height * 0.35), 0, maxCandidateTop),
            Math.Clamp((int)(height * 0.70), 0, maxCandidateTop)
        };

        foreach (int trial in trialTops)
        {
            byte[] prof = RowProfile(previous, ProfileStepX, trial, bandHeight);
            int minP = 255, maxP = 0;
            for (int i = 0; i < prof.Length; i++)
            {
                if (prof[i] < minP) minP = prof[i];
                if (prof[i] > maxP) maxP = prof[i];
            }
            int contrast = maxP - minP;
            if (contrast > bestBandContrast)
            {
                bestBandContrast = contrast;
                bestBandTop = trial;
                bestPreviousBandProfile = prof;
                if (contrast >= 20) break; // 已经有足够明显的文本/边缘对比度
            }
        }

        int bandTop = bestBandTop;
        byte[] previousBandProfile = bestPreviousBandProfile ?? RowProfile(previous, ProfileStepX, bandTop, bandHeight);

        byte[] nextProfile;
        try
        {
            nextProfile = RowProfile(next, ProfileStepX, 0, height);
        }
        catch (Exception ex)
        {
            return ShiftEstimate.Rejected($"读取像素失败：{ex.Message}");
        }

        if (nextProfile.Length < bandHeight || previousBandProfile.Length < bandHeight)
        {
            return ShiftEstimate.Rejected("行投影采样不足");
        }

        // 第一步：一维滑窗，逐个 top 计算行投影 SAD
        var profileScores = new (int Top, double Score)[maxCandidateTop + 1];
        for (int top = 0; top <= maxCandidateTop; top++)
        {
            long sum = 0;
            for (int i = 0; i < bandHeight; i++)
            {
                sum += Math.Abs(previousBandProfile[i] - nextProfile[top + i]);
            }

            profileScores[top] = (top, (double)sum / bandHeight);
        }

        // 找出投影分数的最小值，再把落进容差范围的位置全部收为候选。
        double minProfileScore = double.MaxValue;
        for (int top = 0; top <= maxCandidateTop; top++)
        {
            if (profileScores[top].Score < minProfileScore)
            {
                minProfileScore = profileScores[top].Score;
            }
        }

        double tolerance = CandidateToleranceBase + minProfileScore * CandidateToleranceRatio;
        var candidates = new List<(int Top, double Score)>(MaxCandidates);

        // 关键防护：确保 bandTop (对应位移 0) 只要在容差范围内，必定进入候选表且优先核验
        if (bandTop <= maxCandidateTop && profileScores[bandTop].Score <= minProfileScore + tolerance)
        {
            candidates.Add(profileScores[bandTop]);
        }

        for (int top = 0; top <= maxCandidateTop; top++)
        {
            if (profileScores[top].Score > minProfileScore + tolerance)
            {
                continue;
            }

            if (TooCloseToExisting(candidates, top))
            {
                continue;
            }

            candidates.Add(profileScores[top]);
            if (candidates.Count >= MaxCandidates)
            {
                break;
            }
        }

        if (candidates.Count == 0)
        {
            return ShiftEstimate.Rejected("没有可用候选位置");
        }

        // 第二步：二维复核。用真实像素逐行比较，排除等行距造成的周期性误匹配
        int verifyBandHeight = Math.Max(2, bandHeight / VerifyStepY);
        byte[] verifyBand;
        try
        {
            verifyBand = SampleLuminance(previous, 0, bandTop, width, verifyBandHeight * VerifyStepY, VerifyStepX, VerifyStepY);
        }
        catch (Exception ex)
        {
            return ShiftEstimate.Rejected($"读取上一帧像素失败：{ex.Message}");
        }

        int bestTop = candidates[0].Top;
        double bestScore = double.NaN;
        var verified = new List<(int Top, double Score)>(candidates.Count);

        foreach (var (top, _) in candidates)
        {
            double score = VerifyScore(next, top, verifyBand, width, verifyBandHeight);
            if (double.IsNaN(score))
            {
                continue;
            }

            verified.Add((top, score));
            // 若分数更优，或者与最优分数打平/几乎打平（容差 0.5 灰度）且该候选为零位移 (top == bandTop)，优先判定为静止
            if (double.IsNaN(bestScore) || score < bestScore || (Math.Abs(score - bestScore) <= 0.5 && top == bandTop))
            {
                bestScore = score;
                bestTop = top;
            }
        }

        if (double.IsNaN(bestScore))
        {
            return ShiftEstimate.Rejected("二维复核失败");
        }

        // 次优解：与最优位置相距足够远的候选里最好的那个
        double runnerUp = double.NaN;
        foreach (var (top, score) in verified)
        {
            if (Math.Abs(top - bestTop) < DistinctCandidateDistance)
            {
                continue;
            }

            if (double.IsNaN(runnerUp) || score < runnerUp)
            {
                runnerUp = score;
            }
        }

        int shift = bandTop - bestTop;

        if (bestScore > MaxMeanDifference)
        {
            return new ShiftEstimate(shift, false, bestScore, runnerUp, $"内容差异过大（平均差 {bestScore:F1}），可能窗口已切换");
        }

        // 1. 若候选在 bandTop（位移 0）处几乎完美重合（平均差极小），判定为可信的静止帧
        if (shift == 0 && bestScore <= CandidateToleranceBase + 2.0)
        {
            return new ShiftEstimate(0, true, bestScore, runnerUp, "ok");
        }

        // 2. 歧义性检查：若次优解与最优解分数相当（包括混叠与大差异噪声），说明无法确切唯一定位位移
        if (!double.IsNaN(runnerUp))
        {
            if (runnerUp <= bestScore || bestScore >= runnerUp * ConfidenceRatio)
            {
                return new ShiftEstimate(shift, false, bestScore, runnerUp, "存在多个同样匹配的位置，位移不可信");
            }
        }

        // 3. 匹配位置被顶到搜索窗口边缘，说明真实位移可能超过可检测上限，此时给出的是钳制值而非真实值
        if (bestTop <= 0)
        {
            return new ShiftEstimate(shift, false, bestScore, runnerUp, "位移达到可检测上限，实际滚动可能更大");
        }

        return new ShiftEstimate(shift, true, bestScore, runnerUp, "ok");
    }

    /// <summary>
    /// 判断候选是否与已收的位置挤在一起；相距不到 4 像素的邻点没有区分意义。
    /// </summary>
    private static bool TooCloseToExisting(List<(int Top, double Score)> chosen, int top)
    {
        foreach (var existing in chosen)
        {
            if (Math.Abs(existing.Top - top) < 4)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>二维复核分数：把候选区域按真实像素抽样后与模板带求平均绝对差。失败返回 NaN。</summary>
    private static double VerifyScore(SKBitmap next, int top, byte[] band, int width, int bandRows)
    {
        byte[] candidate;
        try
        {
            candidate = SampleLuminance(next, 0, top, width, bandRows * VerifyStepY, VerifyStepX, VerifyStepY);
        }
        catch
        {
            return double.NaN;
        }

        int count = Math.Min(band.Length, candidate.Length);
        if (count == 0)
        {
            return double.NaN;
        }

        long sum = 0;
        for (int i = 0; i < count; i++)
        {
            sum += Math.Abs(band[i] - candidate[i]);
        }

        return (double)sum / count;
    }

    /// <summary>
    /// 把 <paramref name="rowCount"/> 行各自压成一个平均亮度值。
    /// 单趟融合实现：边扫边累加，不分配中间灰度数组——1080p 下这一步省掉一次 26 万元素的分配与二次遍历。
    /// </summary>
    private static byte[] RowProfile(SKBitmap bitmap, int stepX, int rowStart, int rowCount)
    {
        var profile = new byte[rowCount];
        int width = bitmap.Width;
        int height = bitmap.Height;
        int columns = width / stepX;
        if (columns <= 0)
        {
            return profile;
        }

        using var pixmap = bitmap.PeekPixels();
        if (pixmap is not null && TryGetPixelSpan(pixmap, width, height, out ReadOnlySpan<byte> span, out int rowBytes))
        {
            bool bgra = pixmap.ColorType == SKColorType.Bgra8888;
            for (int row = 0; row < rowCount; row++)
            {
                int y = rowStart + row;
                if (y < 0 || y >= height)
                {
                    continue;
                }

                int lineStart = y * rowBytes;
                int sum = 0;
                for (int col = 0; col < columns; col++)
                {
                    int offset = lineStart + col * stepX * 4;
                    sum += bgra
                        ? (span[offset + 2] * 77 + span[offset + 1] * 151 + span[offset] * 28) >> 8
                        : (span[offset] * 77 + span[offset + 1] * 151 + span[offset + 2] * 28) >> 8;
                }

                profile[row] = (byte)(sum / columns);
            }

            return profile;
        }

        // 回退路径：像素访问受限或格式非常规时逐点读
        for (int row = 0; row < rowCount; row++)
        {
            int y = rowStart + row;
            if (y < 0 || y >= height)
            {
                continue;
            }

            int sum = 0;
            for (int col = 0; col < columns; col++)
            {
                sum += ToLuminance(bitmap.GetPixel(col * stepX, y));
            }

            profile[row] = (byte)(sum / columns);
        }

        return profile;
    }

    /// <summary>校验并取出连续像素内存；不满足条件返回 false，调用方走逐点读取的回退路径。</summary>
    private static bool TryGetPixelSpan(SKPixmap pixmap, int width, int height, out ReadOnlySpan<byte> span, out int rowBytes)
    {
        span = default;
        rowBytes = pixmap.RowBytes;

        var colorType = pixmap.ColorType;
        if (colorType != SKColorType.Bgra8888 && colorType != SKColorType.Rgba8888)
        {
            return false;
        }

        if (rowBytes < width * 4)
        {
            return false;
        }

        ReadOnlySpan<byte> pixels = pixmap.GetPixelSpan();
        if (pixels.Length < rowBytes * (height - 1) + width * 4)
        {
            return false;
        }

        span = pixels;
        return true;
    }

    /// <summary>
    /// 按步长抽样出灰度值。优先走 <c>PeekPixels</c> 的连续内存，平台不允许时退回逐点读取。
    /// </summary>
    private static byte[] SampleLuminance(SKBitmap bitmap, int x0, int y0, int width, int height, int stepX, int stepY)
    {
        int columns = width / stepX;
        int rows = height / stepY;
        if (columns <= 0 || rows <= 0)
        {
            return Array.Empty<byte>();
        }

        var result = new byte[columns * rows];

        using var pixmap = bitmap.PeekPixels();
        if (pixmap is not null && TryGetPixelSpan(pixmap, bitmap.Width, bitmap.Height, out ReadOnlySpan<byte> span, out int rowBytes))
        {
            bool bgra = pixmap.ColorType == SKColorType.Bgra8888;
            int index = 0;
            for (int row = 0; row < rows; row++)
            {
                int y = y0 + row * stepY;
                if (y < 0 || y >= bitmap.Height)
                {
                    index += columns;
                    continue;
                }

                int lineStart = y * rowBytes;
                for (int col = 0; col < columns; col++)
                {
                    int x = x0 + col * stepX;
                    if (x < 0 || x >= bitmap.Width)
                    {
                        continue;
                    }

                    int offset = lineStart + x * 4;
                    byte c0 = span[offset];
                    byte c1 = span[offset + 1];
                    byte c2 = span[offset + 2];
                    // bgra: [B,G,R]  rgba: [R,G,B] —— 两种顺序下加权系数要跟着换
                    result[index++] = bgra
                        ? (byte)((c2 * 77 + c1 * 151 + c0 * 28) >> 8)
                        : (byte)((c0 * 77 + c1 * 151 + c2 * 28) >> 8);
                }
            }

            return result;
        }

        // 回退路径：像素访问受限或格式非常规时逐点读
        int fallbackIndex = 0;
        for (int row = 0; row < rows; row++)
        {
            int y = y0 + row * stepY;
            for (int col = 0; col < columns; col++)
            {
                int x = x0 + col * stepX;
                result[fallbackIndex] = x >= 0 && x < bitmap.Width && y >= 0 && y < bitmap.Height
                    ? ToLuminance(bitmap.GetPixel(x, y))
                    : (byte)0;
                fallbackIndex++;
            }
        }

        return result;
    }

    private static byte ToLuminance(SKColor color) =>
        (byte)((color.Red * 77 + color.Green * 151 + color.Blue * 28) >> 8);
}
