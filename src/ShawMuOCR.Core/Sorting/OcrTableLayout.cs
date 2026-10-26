using System.Text;

namespace ShawMuOCR.Core.Sorting;

/// <summary>
/// OCR 表格结构还原结果：由文字检测框的几何位置推断出行列网格，可序列化为
/// TSV（制表符分列）或 HTML &lt;table&gt;（粘贴进 Excel / WPS / Word 时还原为真正的单元格，
/// 且支持合并单元格 colspan），从而在「表格软件」与「普通输入框」两类目标下都得到正确结果。
/// </summary>
public sealed class OcrTableLayout
{
    public OcrTableLayout(
        IReadOnlyList<IReadOnlyList<string>> rows,
        int columnCount,
        IReadOnlyList<int>? rowFirstCellSpans = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (columnCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(columnCount), "列数必须大于 0");
        }

        Rows = rows;
        ColumnCount = columnCount;
        RowFirstCellSpans = rowFirstCellSpans;
    }

    /// <summary>
    /// 逐行逐列的单元格文本（等宽：每行长度均为 <see cref="ColumnCount"/>，空单元格为空字符串）
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; }

    /// <summary>
    /// 每行首个单元格横跨的列数（合并单元格表头），null 表示所有单元格均只占一列
    /// </summary>
    public IReadOnlyList<int>? RowFirstCellSpans { get; }

    public int ColumnCount { get; }

    public int RowCount => Rows.Count;

    /// <summary>
    /// 非空单元格数量，用于诊断与表格密度校验
    /// </summary>
    public int NonEmptyCellCount
    {
        get
        {
            int count = 0;
            foreach (var row in Rows)
            {
                foreach (var cell in row)
                {
                    if (!string.IsNullOrWhiteSpace(cell))
                    {
                        count++;
                    }
                }
            }
            return count;
        }
    }

    /// <summary>
    /// 序列化为 TSV：单元格之间用制表符 \t，行之间用 \r\n（与 Excel/WPS 粘贴解析规则一致）。
    /// 空单元格输出为空字符串，靠连续的制表符占位，从而保证列不错位。
    /// </summary>
    public string ToTsv()
    {
        var sb = new StringBuilder();
        for (int r = 0; r < Rows.Count; r++)
        {
            if (r > 0)
            {
                sb.Append("\r\n");
            }

            var row = Rows[r];
            for (int c = 0; c < ColumnCount; c++)
            {
                if (c > 0)
                {
                    sb.Append('\t');
                }

                if (c < row.Count)
                {
                    sb.Append(row[c]);
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 序列化为 HTML &lt;table&gt; 片段（不含 html/body 外壳，外壳与 CF_HTML 头由剪贴板层负责）：
    /// Excel / WPS / Word 粘贴时会优先采用该格式，还原为真正的单元格结构，
    /// 且合并单元格表头可通过 colspan 表达，空单元格也保持为空而非被压缩。
    /// </summary>
    public string ToHtmlTable()
    {
        var sb = new StringBuilder();
        // border="0"：只还原单元格结构，不强行套上边框，粘贴到 Excel 后由用户自行决定样式
        sb.Append("<table border=\"0\" cellspacing=\"0\" cellpadding=\"3\" style=\"border-collapse:collapse;font-family:'Microsoft YaHei',SimSun,sans-serif;font-size:10.5pt;\">");

        for (int r = 0; r < Rows.Count; r++)
        {
            sb.Append("<tr>");

            var row = Rows[r];
            int span = GetFirstCellSpan(r);
            sb.Append("<td");
            if (span > 1)
            {
                sb.Append(" colspan=\"").Append(span).Append('"');
            }
            sb.Append('>');
            sb.Append(EscapeHtml(row.Count > 0 ? row[0] : string.Empty));
            sb.Append("</td>");

            for (int c = span; c < ColumnCount; c++)
            {
                sb.Append("<td>");
                sb.Append(EscapeHtml(c < row.Count ? row[c] : string.Empty));
                sb.Append("</td>");
            }

            sb.Append("</tr>");
        }

        sb.Append("</table>");
        return sb.ToString();
    }

    private int GetFirstCellSpan(int rowIndex)
    {
        int span = 1;
        if (RowFirstCellSpans is not null && rowIndex < RowFirstCellSpans.Count)
        {
            span = RowFirstCellSpans[rowIndex];
        }

        return Math.Clamp(span, 1, ColumnCount);
    }

    private static string EscapeHtml(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length + 8);
        foreach (char c in text)
        {
            switch (c)
            {
                case '&':
                    sb.Append("&amp;");
                    break;
                case '<':
                    sb.Append("&lt;");
                    break;
                case '>':
                    sb.Append("&gt;");
                    break;
                case '"':
                    sb.Append("&quot;");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    public override string ToString() => $"{RowCount} 行 × {ColumnCount} 列";
}