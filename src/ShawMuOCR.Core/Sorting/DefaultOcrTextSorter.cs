using System.Text;
using ShawMuOCR.Core.OCR;

namespace ShawMuOCR.Core.Sorting;

public sealed class DefaultOcrTextSorter : IOcrTextSorter
{
    public string SortAndFormat(IReadOnlyList<OcrTextBlock> blocks) => SortAndFormat(blocks, false);

    public string SortAndFormat(IReadOnlyList<OcrTextBlock> blocks, bool separateColumns)
    {
        if (blocks == null || blocks.Count == 0)
        {
            return string.Empty;
        }

        if (blocks.Count == 1)
        {
            return blocks[0].Text.Trim();
        }

        // 按 Y 轴中心排序
        var sorted = blocks
            .OrderBy(b => b.BoundingBox.Top + b.BoundingBox.Height / 2.0)
            .ToList();

        // 聚类为多行
        var lines = new List<List<OcrTextBlock>>();
        List<OcrTextBlock>? currentLine = null;
        double currentLineCenterY = 0;
        double currentLineAvgHeight = 0;

        foreach (var block in sorted)
        {
            double centerY = block.BoundingBox.Top + block.BoundingBox.Height / 2.0;
            double height = Math.Max(block.BoundingBox.Height, 1);

            if (currentLine == null)
            {
                currentLine = new List<OcrTextBlock> { block };
                currentLineCenterY = centerY;
                currentLineAvgHeight = height;
                lines.Add(currentLine);
            }
            else
            {
                // 如果当前块的中心 Y 与该行平均中心 Y 偏差在半个行高以内，则视为同一行
                double threshold = Math.Max(12.0, Math.Min(height, currentLineAvgHeight) * 0.55);
                if (Math.Abs(centerY - currentLineCenterY) <= threshold)
                {
                    currentLine.Add(block);
                    // 动态更新行平均中心和平均高度
                    currentLineCenterY = currentLine.Average(b => b.BoundingBox.Top + b.BoundingBox.Height / 2.0);
                    currentLineAvgHeight = currentLine.Average(b => b.BoundingBox.Height);
                }
                else
                {
                    currentLine = new List<OcrTextBlock> { block };
                    currentLineCenterY = centerY;
                    currentLineAvgHeight = height;
                    lines.Add(currentLine);
                }
            }
        }

        // 每行内按 X 坐标从左到右排序
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            var lineBlocks = lines[i].OrderBy(b => b.BoundingBox.Left).ToList();
            var lineSb = new StringBuilder();

            for (int j = 0; j < lineBlocks.Count; j++)
            {
                var text = lineBlocks[j].Text.Trim();
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                if (lineSb.Length > 0)
                {
                    if (separateColumns)
                    {
                        // 开启按检测框分列：同一行内不同的文字框以制表符 '\t' 分隔，写入 Excel 时自动拆入相邻单元格（A1, B1, C1...）
                        lineSb.Append('\t');
                    }
                    else
                    {
                        // 如果两端都是中日韩字符，不加空格；若是西文字符，则增加空格分隔
                        if (!(OcrCjkTextHelper.IsCjk(lineSb[^1]) && OcrCjkTextHelper.IsCjk(text[0])))
                        {
                            lineSb.Append(' ');
                        }
                    }
                }

                lineSb.Append(text);
            }

            if (lineSb.Length > 0)
            {
                if (sb.Length > 0)
                {
                    sb.AppendLine();
                }
                sb.Append(lineSb);
            }
        }

        return sb.ToString();
    }
}
