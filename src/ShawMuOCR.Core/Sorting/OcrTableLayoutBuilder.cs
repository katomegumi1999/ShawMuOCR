using System.Text;
using ShawMuOCR.Core.OCR;
using SkiaSharp;

namespace ShawMuOCR.Core.Sorting;

/// <summary>
/// 从 OCR 文字检测框的几何位置还原表格行列结构（XY-Cut 投影法的简化实现），
/// 并输出可被 Excel / WPS / 腾讯文档直接分列粘贴的 TSV。
///
/// 判定的两条路径，优先用更可靠的那条：
/// A. 表格线优先：源图像存在内部竖线时，直接按竖线切分列（<see cref="TableLineDetector"/>）。
///    竖线是像素级稳定证据，不受 OCR 文本检测框膨胀的影响，紧凑表格、长数字并排都能稳定分列。
/// B. 投影法回退：没有竖线时才依靠检测框几何位置推断：
///    1. 先按垂直中心坐标把检测框聚成「行」；
///    2. 再对 X 轴做逐行投影，统计每个像素列被多少「行」占据（同一行内的多个框只计一次，
///       这样表头的合并单元格、跨列长文本不会把整张表的列间隙抹平）；
///    3. 占据行数达到阈值的连续区间即为一列，间隔小于最小列间隙的零碎空洞并入同一列。
///
/// 两条路径共用同一套网格填充与校验：列数 ≥ 2 且单元格密度达标才判定为表格。
/// 对普通段落文字（每行只有一个检测框，或只有零星长句）会返回 null，交由调用方沿用原有纯文本结果。
/// </summary>
public static class OcrTableLayoutBuilder
{
    private const int MinColumnCount = 2;

    /// <summary>单元格密度下限：非空单元格占网格比例，过低说明列是被切错的而非真表格</summary>
    private const double MinCellDensity = 0.3;

    /// <summary>多行表格的跨行覆盖率（某像素列至少被这么多行占用才算列内容）</summary>
    private const double MultiRowOccupancyRatio = 0.3;

    /// <summary>
    /// 列间隙的绝对像素下限：低于约一个字符宽的空白一律视为同一单元格内的碎框间隔，
    /// 避免「华南 区」这类被 OCR 切成两段的单元格凭空多出一列、导致整行右移。
    /// </summary>
    private const double MinColumnGapPixels = 10.0;

    /// <summary>多行表格的列间隙系数：跨行证据充分，允许较窄的空白切开，保证紧凑表格也能分列</summary>
    private const double MultiRowColumnGapRatio = 0.35;

    /// <summary>双行表格的列间隙系数：跨行验证只有两行，适当收紧</summary>
    private const double TwoRowColumnGapRatio = 0.6;

    /// <summary>
    /// 单行表格的列间隙系数：没有跨行验证，理论上要更严；但实测中「一行数据」的相邻列
    /// （尤其是长数字之间）空白往往只有 0.4~0.5 个字高，定得更严会把真实列合并掉，故与多行同档。
    /// </summary>
    private const double SingleRowColumnGapRatio = 0.4;

    /// <summary>
    /// 尝试还原表格结构。非表格形态返回 null。
    /// </summary>
    public static OcrTableLayout? TryBuild(IReadOnlyList<OcrTextBlock>? blocks) => TryBuild(blocks, out _);

    /// <summary>
    /// 尝试还原表格结构（带源图像）：当图像中存在表格竖线时按线切分列，否则回退到投影法。
    /// 这是截屏 / 批量识别场景应使用的重载。
    /// </summary>
    public static OcrTableLayout? TryBuild(
        IReadOnlyList<OcrTextBlock>? blocks,
        SKBitmap? sourceImage,
        out string? reason)
    {
        List<(int Left, int Right)>? lineColumns = null;
        string lineInfo = string.Empty;

        if (TableLineDetector.TryDetectColumns(sourceImage, out var detected, out string lineReason))
        {
            lineColumns = detected;
            lineInfo = lineReason;
        }
        else if (sourceImage is not null)
        {
            lineInfo = lineReason;
        }

        var layout = Build(blocks, lineColumns, out string? coreReason);

        reason = string.IsNullOrEmpty(lineInfo)
            ? coreReason
            : $"{lineInfo}；{coreReason}";
        return layout;
    }

    /// <summary>
    /// 尝试还原表格结构。非表格形态返回 null，并通过 <paramref name="reason"/> 说明放弃原因（便于日志诊断）。
    /// </summary>
    public static OcrTableLayout? TryBuild(IReadOnlyList<OcrTextBlock>? blocks, out string? reason)
        => Build(blocks, null, out reason);

    /// <summary>
    /// 用「已经算好的」表格线列区间还原表格结构。
    /// 供把表格线检测与 OCR 推理并行执行的调用方使用 —— 线检测只读像素、不依赖 OCR 结果，
    /// 可以提前或在另一个线程上完成，这里直接消费结果，避免在内部重复扫描一遍像素。
    /// <paramref name="lineColumns"/> 为 null 或不足以成列时，等价于纯投影法。
    /// </summary>
    public static OcrTableLayout? TryBuildWithColumns(
        IReadOnlyList<OcrTextBlock>? blocks,
        List<(int Left, int Right)>? lineColumns,
        out string? reason)
        => Build(blocks, lineColumns, out reason);

    private static OcrTableLayout? Build(
        IReadOnlyList<OcrTextBlock>? blocks,
        List<(int Left, int Right)>? lineColumns,
        out string? reason)
    {
        reason = null;

        if (blocks is null || blocks.Count < 2)
        {
            reason = "检测框数量不足，按纯文本处理";
            return null;
        }

        var items = new List<OcrTextBlock>(blocks.Count);
        foreach (var block in blocks)
        {
            if (block is null || string.IsNullOrWhiteSpace(block.Text))
            {
                continue;
            }
            if (block.BoundingBox.Width <= 0 || block.BoundingBox.Height <= 0)
            {
                continue;
            }
            items.Add(block);
        }

        if (items.Count < 2)
        {
            reason = "有效检测框数量不足，按纯文本处理";
            return null;
        }

        // 1) 行聚类
        var rows = ClusterRows(items);

        // 2) 列切分
        double medianHeight = Median(items.Select(b => (double)b.BoundingBox.Height));

        // 跨行覆盖阈值：一个像素列至少要被这么多「行」占用才算列内容，既定义列的下限，
        // 也顺带过滤掉「同一单元格被切成碎框」造成的幽灵列（那种空隙只有在极少数行出现）。
        int occupancyThreshold = rows.Count >= 3
            ? Math.Max(2, (int)Math.Ceiling(rows.Count * MultiRowOccupancyRatio))
            : 1;

        // 列间隙阈值按行数分档：行数越多，跨行证据越充分，就允许越窄的空白切开 ——
        // 紧凑表格（列间空白不足一个行高）也能正确分列；单行时缺少跨行验证，必须要求更宽的空白。
        double gapRatio = rows.Count >= 3
            ? MultiRowColumnGapRatio
            : (rows.Count == 2 ? TwoRowColumnGapRatio : SingleRowColumnGapRatio);
        double minColumnGap = Math.Max(MinColumnGapPixels, medianHeight * gapRatio);

        int maxX = 0;
        foreach (var block in items)
        {
            if (block.BoundingBox.Right > maxX)
            {
                maxX = block.BoundingBox.Right;
            }
        }

        var columns = new List<(int Left, int Right)>();
        string columnSource = "投影法";

        // 路径 A：表格线。竖线是像素级证据，不受检测框膨胀影响，优先级高于几何推断。
        if (lineColumns is { Count: >= MinColumnCount })
        {
            var fromLines = RemoveEmptyColumns(lineColumns, items);
            if (fromLines.Count >= MinColumnCount)
            {
                columns = fromLines;
                columnSource = "表格线";
            }
        }

        // 路径 B：没有任何可用竖线时，退回检测框投影
        if (columns.Count < MinColumnCount)
        {
            columns = SplitColumns(rows, maxX, minColumnGap, occupancyThreshold);
            columnSource = "投影法";
        }

        if (columns.Count < MinColumnCount)
        {
            reason = $"仅检测到 {columns.Count} 列（{rows.Count} 行 / {items.Count} 个检测框 / 行高 {medianHeight:F0}px / 列间隙阈值 {minColumnGap:F0}px），按纯文本处理";
            return null;
        }

        // 3) 填充网格
        var grid = new List<IReadOnlyList<string>>(rows.Count);
        var firstCellSpans = new List<int>(rows.Count);

        foreach (var row in rows)
        {
            var buckets = new List<OcrTextBlock>?[columns.Count];

            // 整行只有一个检测框且横跨多列 → 典型为合并单元格的标题/合计行，固定落到第 1 列，
            // 粘贴进表格后从该行最左侧开始显示，不会因原图居中而"跳格"到中间列。
            int mergedSpan = row.Count == 1 ? CountOverlappingColumns(columns, row[0].BoundingBox) : 0;
            bool forceFirstColumn = mergedSpan >= 2;

            foreach (var block in row)
            {
                int col = forceFirstColumn ? 0 : FindColumnIndex(columns, block.BoundingBox);
                var bucket = buckets[col];
                if (bucket is null)
                {
                    bucket = new List<OcrTextBlock>(1);
                    buckets[col] = bucket;
                }
                bucket.Add(block);
            }

            firstCellSpans.Add(forceFirstColumn ? Math.Min(mergedSpan, columns.Count) : 1);

            var cells = new string[columns.Count];
            for (int c = 0; c < columns.Count; c++)
            {
                var bucket = buckets[c];
                cells[c] = bucket is null ? string.Empty : JoinCell(bucket);
            }
            grid.Add(cells);
        }

        var layout = new OcrTableLayout(grid, columns.Count, firstCellSpans);

        // 唯一的兜底校验：网格里绝大多数格子都是空的，说明列是被切错的（不是真表格）
        double density = layout.NonEmptyCellCount / (double)(layout.RowCount * layout.ColumnCount);
        if (density < MinCellDensity)
        {
            reason = $"单元格密度过低 ({density:P0})，判定列切分不可靠，按纯文本处理";
            return null;
        }

        reason = $"识别为表格 {layout.RowCount} 行 × {layout.ColumnCount} 列（按{columnSource}分列）";
        return layout;
    }

    /// <summary>
    /// 剔除完全不包含任何检测框的列区间。
    /// 竖线可能来自表格之外的装饰线、截入画面的窗口分隔条等，这类列整列无字，直接丢弃可避免多出空列。
    /// 以检测框中心点归属判定，比按重叠判定更严格：横跨多列的合并单元格只归属一列，不会把空列"救活"。
    /// </summary>
    private static List<(int Left, int Right)> RemoveEmptyColumns(
        List<(int Left, int Right)> columns,
        List<OcrTextBlock> items)
    {
        if (columns.Count == 0 || items.Count == 0)
        {
            return columns;
        }

        var kept = new List<(int Left, int Right)>(columns.Count);

        foreach (var column in columns)
        {
            foreach (var block in items)
            {
                double centerX = block.BoundingBox.Left + block.BoundingBox.Width / 2.0;
                if (centerX >= column.Left && centerX < column.Right)
                {
                    kept.Add(column);
                    break;
                }
            }
        }

        return kept;
    }

    /// <summary>
    /// 按垂直中心坐标把检测框聚成行，行内按 X 从左到右排序。
    /// 聚类阈值与 <see cref="DefaultOcrTextSorter"/> 保持一致，确保表格行列与纯文本阅读顺序不冲突。
    /// </summary>
    private static List<List<OcrTextBlock>> ClusterRows(List<OcrTextBlock> items)
    {
        var sorted = items
            .OrderBy(b => b.BoundingBox.Top + b.BoundingBox.Height / 2.0)
            .ToList();

        var rows = new List<List<OcrTextBlock>>();
        List<OcrTextBlock>? currentRow = null;
        double currentCenterY = 0;
        double currentAvgHeight = 0;

        foreach (var block in sorted)
        {
            double centerY = block.BoundingBox.Top + block.BoundingBox.Height / 2.0;
            double height = Math.Max(block.BoundingBox.Height, 1);

            if (currentRow is null)
            {
                currentRow = new List<OcrTextBlock> { block };
                currentCenterY = centerY;
                currentAvgHeight = height;
                rows.Add(currentRow);
                continue;
            }

            double threshold = Math.Max(12.0, Math.Min(height, currentAvgHeight) * 0.55);
            if (Math.Abs(centerY - currentCenterY) <= threshold)
            {
                currentRow.Add(block);
                currentCenterY = currentRow.Average(b => b.BoundingBox.Top + b.BoundingBox.Height / 2.0);
                currentAvgHeight = currentRow.Average(b => b.BoundingBox.Height);
            }
            else
            {
                currentRow = new List<OcrTextBlock> { block };
                currentCenterY = centerY;
                currentAvgHeight = height;
                rows.Add(currentRow);
            }
        }

        foreach (var row in rows)
        {
            row.Sort((a, b) => a.BoundingBox.Left.CompareTo(b.BoundingBox.Left));
        }

        return rows;
    }

    /// <summary>
    /// X 轴逐行投影切分列：统计每个像素列被多少「行」占据，得到列区间列表。
    /// <paramref name="occupancyThreshold"/> 即「一个像素列至少要被多少行占用才算列内容」。
    /// </summary>
    private static List<(int Left, int Right)> SplitColumns(
        List<List<OcrTextBlock>> rows,
        int maxX,
        double minColumnGap,
        int occupancyThreshold)
    {
        var columns = new List<(int Left, int Right)>();
        if (maxX <= 0)
        {
            return columns;
        }

        // coverage[x] = 有多少「行」在 x 处存在文字（同一行内多个框只计 1 次）
        var coverage = new int[maxX + 1];
        var diff = new int[maxX + 2];

        foreach (var row in rows)
        {
            bool touched = false;
            foreach (var block in row)
            {
                int left = Math.Clamp(block.BoundingBox.Left, 0, maxX);
                int right = Math.Clamp(block.BoundingBox.Right, 0, maxX);
                if (right <= left)
                {
                    continue;
                }
                diff[left]++;
                diff[right]--;
                touched = true;
            }

            if (!touched)
            {
                continue;
            }

            int running = 0;
            for (int x = 0; x <= maxX; x++)
            {
                running += diff[x];
                if (running > 0)
                {
                    coverage[x]++;
                }
            }

            // 只回滚本行改动过的位置，避免每次 O(width) 清零
            foreach (var block in row)
            {
                int left = Math.Clamp(block.BoundingBox.Left, 0, maxX);
                int right = Math.Clamp(block.BoundingBox.Right, 0, maxX);
                if (right <= left)
                {
                    continue;
                }
                diff[left] = 0;
                diff[right] = 0;
            }
        }

        // 占据行数达到阈值的像素列视为「被占用」：合并单元格表头只贡献 1 行，不会抹平列间隙
        bool IsOccupied(int x) => x >= 0 && x <= maxX && coverage[x] >= occupancyThreshold;

        int cursor = 0;
        while (cursor <= maxX)
        {
            if (!IsOccupied(cursor))
            {
                cursor++;
                continue;
            }

            int start = cursor;
            int lastOccupied = cursor;

            while (cursor <= maxX)
            {
                if (IsOccupied(cursor))
                {
                    lastOccupied = cursor;
                    cursor++;
                }
                else if (cursor - lastOccupied < minColumnGap)
                {
                    // 词间小空白：并入当前列
                    cursor++;
                }
                else
                {
                    break;
                }
            }

            columns.Add((start, lastOccupied));
        }

        return columns;
    }

    /// <summary>
    /// 统计检测框与多少个列区间存在水平重叠（用于识别横跨多列的合并单元格）
    /// </summary>
    private static int CountOverlappingColumns(List<(int Left, int Right)> columns, SkiaSharp.SKRectI box)
    {
        int count = 0;
        foreach (var column in columns)
        {
            if (Math.Min(box.Right, column.Right) - Math.Max(box.Left, column.Left) > 0)
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// 把检测框归属到某一列。若检测框横跨多列（典型为合并单元格的表头/标题），归入其覆盖的最左一列，
    /// 使粘贴到 Excel 后内容从该行最左侧开始显示，不会因居中而"跳格"。
    /// </summary>
    private static int FindColumnIndex(List<(int Left, int Right)> columns, SkiaSharp.SKRectI box)
    {
        int firstOverlapping = -1;
        int bestOverlap = 0;
        int best = 0;

        for (int i = 0; i < columns.Count; i++)
        {
            int overlap = Math.Min(box.Right, columns[i].Right) - Math.Max(box.Left, columns[i].Left);
            if (overlap <= 0)
            {
                continue;
            }

            if (firstOverlapping < 0)
            {
                firstOverlapping = i;
            }

            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                best = i;
            }
        }

        return firstOverlapping >= 0 ? firstOverlapping : best;
    }

    /// <summary>
    /// 拼接同一单元格内的多个检测框：CJK 之间直连，西文之间补空格；
    /// 同时剔除会破坏 TSV 结构的制表符与换行符。
    /// </summary>
    private static string JoinCell(IReadOnlyList<OcrTextBlock> blocks)
    {
        var sb = new StringBuilder();

        foreach (var block in blocks)
        {
            string text = Sanitize(block.Text);
            if (text.Length == 0)
            {
                continue;
            }

            OcrCjkTextHelper.AppendInline(sb, text);
        }

        return sb.ToString();
    }

    private static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        bool lastWasSpace = false;

        foreach (char c in text)
        {
            if (c is '\t' or '\r' or '\n' or '\u00A0' or '\u3000')
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
                continue;
            }

            sb.Append(c);
            lastWasSpace = c == ' ';
        }

        return sb.ToString().Trim();
    }

    private static double Median(IEnumerable<double> values)
    {
        var list = values.Where(v => v > 0).OrderBy(v => v).ToList();
        if (list.Count == 0)
        {
            return 0;
        }

        int mid = list.Count / 2;
        return list.Count % 2 == 0 ? (list[mid - 1] + list[mid]) / 2.0 : list[mid];
    }
}
