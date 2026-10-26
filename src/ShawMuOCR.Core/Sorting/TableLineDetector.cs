using SkiaSharp;

namespace ShawMuOCR.Core.Sorting;

/// <summary>
/// 表格框线检测：直接从图像像素找出表格的竖直分隔线，取代「靠检测框之间剩多少空白猜列」的做法。
///
/// 为什么需要它：OCR 检测模型（DB 系）会对文本框做固定比例的向外膨胀，膨胀量随文字高度增长。
/// 当左右两列之间的真实空白较窄时（紧凑表格、长数字并排），膨胀后的两个框会贴在一起甚至重叠，
/// 检测框之间的空隙就从「够宽」掉到「不够宽」的临界点附近 —— 图上没有任何变化，判定却会随机翻转。
/// 表格自带的竖线是像素级稳定证据，不受膨胀影响，因此优先采用。
///
/// 算法（X 轴投影）：
/// 1. 按面积预算下采样，控制超大截图的遍历成本（4K 图也只扫约 20 万采样点）；
/// 2. 逐采样列统计「暗像素」占比，占比达到阈值的整列视为竖线候选；
/// 3. 合并相邻候选（2~3px 宽的线在采样后会占多格）；
/// 4. 剔除贴着图像左右边缘的线（那是截图外框，不是表格分隔线）；
/// 5. 剩下的内部线 + 图像左右边界即构成列区间。
///
/// 墨迹判定用两档阈值：先按绝对深色（黑线），失败再按「相对背景亮度」放宽，
/// 以便同时覆盖实线表格与 Excel / WPS 默认的浅灰网格线。
/// 图像没有内部竖线时返回 false，调用方回退到原有的投影法，行为与改动前一致。
/// </summary>
public static class TableLineDetector
{
    /// <summary>下采样后参与统计的目标像素规模，兼顾精度与耗时</summary>
    private const int AnalysisPixelBudget = 200_000;

    /// <summary>一条竖线至少要覆盖该方向 55% 的采样行，才认为是表格线而非文字笔画/背景纹理</summary>
    private const double MinVerticalLineCoverage = 0.55;

    /// <summary>绝对深色阈值：纯黑细线的亮度远低于此</summary>
    private const int AbsoluteDarkThreshold = 128;

    /// <summary>相对阈值：亮度低于「背景亮度 - 该值」即视为墨迹，用于捕捉浅灰网格线</summary>
    private const int RelativeDarkOffset = 42;

    private const int MinAdaptiveThreshold = 160;
    private const int MaxAdaptiveThreshold = 236;

    /// <summary>贴着图像左右边缘的线视为截图外框，按图宽比例判定</summary>
    private const double EdgeMarginRatio = 0.02;

    /// <summary>列区间最小宽度（像素），过窄的区间是线条噪声而非真实列</summary>
    private const double MinColumnWidthPixels = 6.0;

    /// <summary>
    /// 检测竖直表格线并给出列区间。
    /// </summary>
    public static bool TryDetectColumns(
        SKBitmap? bitmap,
        out List<(int Left, int Right)> columns,
        out string reason)
    {
        columns = new List<(int Left, int Right)>();
        reason = string.Empty;

        if (bitmap is null)
        {
            reason = "无源图像";
            return false;
        }

        int width = bitmap.Width;
        int height = bitmap.Height;
        if (width < 16 || height < 16)
        {
            reason = $"图像过小 ({width}×{height})";
            return false;
        }

        // 按面积预算下采样：4K 截图也不必逐像素遍历
        int step = Math.Max(1, (int)Math.Sqrt((double)width * height / AnalysisPixelBudget));
        int sampledWidth = Math.Max(1, width / step);
        int sampledHeight = Math.Max(1, height / step);

        var luminance = SampleLuminance(bitmap, step, sampledWidth, sampledHeight);

        var histogram = new int[256];
        foreach (byte value in luminance)
        {
            histogram[value]++;
        }

        int backgroundLuminance = ComputeMedian(histogram, luminance.Length);
        int adaptiveThreshold = Math.Clamp(
            backgroundLuminance - RelativeDarkOffset,
            MinAdaptiveThreshold,
            MaxAdaptiveThreshold);

        var thresholds = adaptiveThreshold > AbsoluteDarkThreshold
            ? new[] { AbsoluteDarkThreshold, adaptiveThreshold }
            : new[] { AbsoluteDarkThreshold };

        string lastReason = "未检测到竖直表格线";

        foreach (int threshold in thresholds)
        {
            if (!TryDetectWithThreshold(
                    luminance, sampledWidth, sampledHeight, step, width, threshold,
                    out columns, out string attemptReason))
            {
                lastReason = attemptReason;
                continue;
            }

            reason = attemptReason;
            return true;
        }

        reason = lastReason;
        columns = new List<(int Left, int Right)>();
        return false;
    }

    private static bool TryDetectWithThreshold(
        byte[] luminance,
        int sampledWidth,
        int sampledHeight,
        int step,
        int width,
        int threshold,
        out List<(int Left, int Right)> columns,
        out string reason)
    {
        columns = new List<(int Left, int Right)>();

        var darkPerColumn = new int[sampledWidth];
        for (int sy = 0; sy < sampledHeight; sy++)
        {
            int rowOffset = sy * sampledWidth;
            for (int sx = 0; sx < sampledWidth; sx++)
            {
                if (luminance[rowOffset + sx] < threshold)
                {
                    darkPerColumn[sx]++;
                }
            }
        }

        int minDarkRows = (int)Math.Ceiling(sampledHeight * MinVerticalLineCoverage);

        // 采集候选并把相邻采样格合并成一条线（取组中心映射回原图坐标）
        var lineCenters = new List<int>();
        int groupStart = -1;
        int groupEnd = -1;

        for (int sx = 0; sx <= sampledWidth; sx++)
        {
            bool isCandidate = sx < sampledWidth && darkPerColumn[sx] >= minDarkRows;

            if (isCandidate)
            {
                if (groupStart < 0)
                {
                    groupStart = sx;
                }
                groupEnd = sx;
                continue;
            }

            if (groupStart >= 0)
            {
                int centerSample = (groupStart + groupEnd) / 2;
                lineCenters.Add(Math.Min(centerSample * step, width - 1));
                groupStart = -1;
                groupEnd = -1;
            }
        }

        if (lineCenters.Count == 0)
        {
            reason = $"未检测到竖直表格线（阈值 {threshold}）";
            return false;
        }

        // 剔除贴边外框线
        double margin = width * EdgeMarginRatio;
        var innerLines = new List<int>();
        foreach (int x in lineCenters)
        {
            if (x > margin && x < width - margin)
            {
                innerLines.Add(x);
            }
        }

        if (innerLines.Count == 0)
        {
            reason = $"仅检测到外框（{lineCenters.Count} 条竖线均在图像边缘），无内部竖线";
            return false;
        }

        // 内部线 + 图像左右边界 = 列分界
        var boundaries = new List<int> { 0 };
        boundaries.AddRange(innerLines);
        boundaries.Add(width);

        for (int i = 0; i < boundaries.Count - 1; i++)
        {
            int left = boundaries[i];
            int right = boundaries[i + 1];
            if (right - left < MinColumnWidthPixels)
            {
                continue;
            }
            columns.Add((left, right));
        }

        if (columns.Count < 2)
        {
            reason = $"表格线未能形成 2 列（{lineCenters.Count} 条竖线 / {columns.Count} 个有效区间）";
            columns.Clear();
            return false;
        }

        reason = $"检测到 {innerLines.Count} 条内部竖线 → {columns.Count} 列（阈值 {threshold}）";
        return true;
    }

    /// <summary>
    /// 按步长采样出灰度阵列。
    /// 优先走零拷贝快路径：一次拿到整块像素缓冲（<c>SKPixmap.GetPixelSpan()</c>），
    /// 自己按 BGRA / RGBA 字节序解出 R/G/B，省掉 <c>GetPixel</c> 每点一次的格式校验与 SKColor 构造。
    /// 位图格式不在支持列表、或像素缓冲不可直接访问时，回退到逐点取色，保证任何输入都能算。
    /// </summary>
    private static byte[] SampleLuminance(SKBitmap bitmap, int step, int sampledWidth, int sampledHeight)
    {
        var luminance = new byte[sampledWidth * sampledHeight];

        if (TrySampleFromPixelSpan(bitmap, step, sampledWidth, sampledHeight, luminance))
        {
            return luminance;
        }

        int width = bitmap.Width;
        int height = bitmap.Height;
        for (int sy = 0; sy < sampledHeight; sy++)
        {
            int py = Math.Min(sy * step, height - 1);
            int rowOffset = sy * sampledWidth;
            for (int sx = 0; sx < sampledWidth; sx++)
            {
                int px = Math.Min(sx * step, width - 1);
                luminance[rowOffset + sx] = ToLuminance(bitmap.GetPixel(px, py));
            }
        }

        return luminance;
    }

    /// <summary>
    /// 零拷贝采样。<paramref name="destination"/> 需要在调用前按 <paramref name="sampledWidth"/> ×
    /// <paramref name="sampledHeight"/> 分配好。无法走快路径时返回 false，且不保证 destination 被写入。
    /// </summary>
    private static bool TrySampleFromPixelSpan(
        SKBitmap bitmap,
        int step,
        int sampledWidth,
        int sampledHeight,
        byte[] destination)
    {
        bool isBgra = bitmap.ColorType == SKColorType.Bgra8888;
        bool isRgba = bitmap.ColorType == SKColorType.Rgba8888;
        if (!isBgra && !isRgba)
        {
            return false;
        }

        try
        {
            using var pixmap = bitmap.PeekPixels();
            if (pixmap is null)
            {
                return false;
            }

            ReadOnlySpan<byte> pixels = pixmap.GetPixelSpan();
            int width = bitmap.Width;
            int height = bitmap.Height;
            int rowBytes = pixmap.RowBytes;

            // RowBytes 可能因对齐而大于 width × 4，必须按行跨度定位
            if (rowBytes < width * 4 || pixels.Length < rowBytes * (height - 1) + width * 4)
            {
                return false;
            }

            for (int sy = 0; sy < sampledHeight; sy++)
            {
                int py = Math.Min(sy * step, height - 1);
                int rowStart = py * rowBytes;
                int rowOffset = sy * sampledWidth;

                for (int sx = 0; sx < sampledWidth; sx++)
                {
                    int px = Math.Min(sx * step, width - 1);
                    int offset = rowStart + px * 4;

                    byte first = pixels[offset];
                    byte green = pixels[offset + 1];
                    byte third = pixels[offset + 2];
                    byte alpha = pixels[offset + 3];

                    if (alpha < 32)
                    {
                        destination[rowOffset + sx] = 255;
                        continue;
                    }

                    byte red = isBgra ? third : first;
                    byte blue = isBgra ? first : third;
                    destination[rowOffset + sx] = (byte)((red * 299 + green * 587 + blue * 114) / 1000);
                }
            }

            return true;
        }
        catch (Exception)
        {
            // 像素访问在某些平台上受限（如外部内存位图），退回逐点取色即可，不影响正确性
            return false;
        }
    }

    private static byte ToLuminance(SKColor color)
    {
        // 透明像素按白底处理，避免带 Alpha 的截图被误判成满屏墨迹
        if (color.Alpha < 32)
        {
            return 255;
        }

        int value = (color.Red * 299 + color.Green * 587 + color.Blue * 114) / 1000;
        return (byte)Math.Clamp(value, 0, 255);
    }

    private static int ComputeMedian(int[] histogram, int total)
    {
        if (total <= 0)
        {
            return 255;
        }

        int half = total / 2;
        int running = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            running += histogram[i];
            if (running >= half)
            {
                return i;
            }
        }

        return 255;
    }
}
