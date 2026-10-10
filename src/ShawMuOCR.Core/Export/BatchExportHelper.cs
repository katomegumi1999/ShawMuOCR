using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ShawMuOCR.Core.Export;

/// <summary>
/// 纯 .NET 原生实现的批量文档导出工具（完全不依赖任何第三方 NuGet 依赖，支持 AOT）
/// 支持 TXT、XLSX (OpenXML 电子表格)、DOCX (OpenXML 文档)
/// </summary>
public static class BatchExportHelper
{
    /// <summary>
    /// 转义 XML 字符并过滤 XML 1.0 非法控制字符
    /// </summary>
    public static string EscapeXml(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        var sb = new StringBuilder(input.Length + 16);
        foreach (char c in input)
        {
            // 过滤小于 0x20 且非 \t, \n, \r 的 XML 1.0 非法控制字符
            if (c < 0x20 && c != 0x09 && c != 0x0A && c != 0x0D)
            {
                continue;
            }

            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 将 1 开始的列号转换为 Excel 列名 (1 -> A, 2 -> B, 26 -> Z, 27 -> AA)
    /// </summary>
    public static string GetColumnLetter(int columnNumber)
    {
        string col = string.Empty;
        while (columnNumber > 0)
        {
            int modulo = (columnNumber - 1) % 26;
            col = Convert.ToChar('A' + modulo) + col;
            columnNumber = (columnNumber - modulo) / 26;
        }
        return col;
    }

    /// <summary>
    /// 导出为纯文本 UTF-8 文件 (.txt)
    /// </summary>
    public static async Task ExportTxtAsync(string text, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outputPath);
        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(outputPath, text ?? string.Empty, new UTF8Encoding(false), cancellationToken);
    }

    /// <summary>
    /// 导出为纯 .NET 原生 OpenXML 电子表格 (.xlsx)
    /// 遵循换行规则：每行文本填入对应行号，若行内包含制表符 '\t' 则按多列排布
    /// </summary>
    public static async Task ExportXlsxAsync(string text, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outputPath);
        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, false, Encoding.UTF8);

        // 1. [Content_Types].xml
        var contentTypesEntry = archive.CreateEntry("[Content_Types].xml", CompressionLevel.Fastest);
        await using (var writer = new StreamWriter(contentTypesEntry.Open(), new UTF8Encoding(false)))
        {
            await writer.WriteAsync(
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">\r\n" +
                "  <Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>\r\n" +
                "  <Default Extension=\"xml\" ContentType=\"application/xml\"/>\r\n" +
                "  <Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>\r\n" +
                "  <Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>\r\n" +
                "</Types>");
        }

        // 2. _rels/.rels
        var rootRelsEntry = archive.CreateEntry("_rels/.rels", CompressionLevel.Fastest);
        await using (var writer = new StreamWriter(rootRelsEntry.Open(), new UTF8Encoding(false)))
        {
            await writer.WriteAsync(
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">\r\n" +
                "  <Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>\r\n" +
                "</Relationships>");
        }

        // 3. xl/_rels/workbook.xml.rels
        var wbRelsEntry = archive.CreateEntry("xl/_rels/workbook.xml.rels", CompressionLevel.Fastest);
        await using (var writer = new StreamWriter(wbRelsEntry.Open(), new UTF8Encoding(false)))
        {
            await writer.WriteAsync(
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">\r\n" +
                "  <Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>\r\n" +
                "</Relationships>");
        }

        // 4. xl/workbook.xml
        var wbEntry = archive.CreateEntry("xl/workbook.xml", CompressionLevel.Fastest);
        await using (var writer = new StreamWriter(wbEntry.Open(), new UTF8Encoding(false)))
        {
            await writer.WriteAsync(
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">\r\n" +
                "  <sheets>\r\n" +
                "    <sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/>\r\n" +
                "  </sheets>\r\n" +
                "</workbook>");
        }

        // 5. xl/worksheets/sheet1.xml
        var sheetEntry = archive.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Fastest);
        await using (var writer = new StreamWriter(sheetEntry.Open(), new UTF8Encoding(false)))
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">\r\n");
            sb.Append("  <sheetData>\r\n");

            var lines = (text ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            int rowIndex = 1;
            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line))
                {
                    sb.Append($"    <row r=\"{rowIndex}\"/>\r\n");
                }
                else
                {
                    sb.Append($"    <row r=\"{rowIndex}\">\r\n");
                    if (line.Contains('\t'))
                    {
                        var cols = line.Split('\t');
                        for (int c = 0; c < cols.Length; c++)
                        {
                            string colLetter = GetColumnLetter(c + 1);
                            string escaped = EscapeXml(cols[c]);
                            sb.Append($"      <c r=\"{colLetter}{rowIndex}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{escaped}</t></is></c>\r\n");
                        }
                    }
                    else
                    {
                        string escaped = EscapeXml(line);
                        sb.Append($"      <c r=\"A{rowIndex}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{escaped}</t></is></c>\r\n");
                    }
                    sb.Append("    </row>\r\n");
                }
                rowIndex++;
            }

            sb.Append("  </sheetData>\r\n");
            sb.Append("</worksheet>");

            await writer.WriteAsync(sb.ToString());
        }
    }

    /// <summary>
    /// 导出为纯 .NET 原生 OpenXML 文字处理文档 (.docx)
    /// </summary>
    public static async Task ExportDocxAsync(string text, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outputPath);
        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, false, Encoding.UTF8);

        // 1. [Content_Types].xml
        var contentTypesEntry = archive.CreateEntry("[Content_Types].xml", CompressionLevel.Fastest);
        await using (var writer = new StreamWriter(contentTypesEntry.Open(), new UTF8Encoding(false)))
        {
            await writer.WriteAsync(
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">\r\n" +
                "  <Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>\r\n" +
                "  <Default Extension=\"xml\" ContentType=\"application/xml\"/>\r\n" +
                "  <Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>\r\n" +
                "</Types>");
        }

        // 2. _rels/.rels
        var rootRelsEntry = archive.CreateEntry("_rels/.rels", CompressionLevel.Fastest);
        await using (var writer = new StreamWriter(rootRelsEntry.Open(), new UTF8Encoding(false)))
        {
            await writer.WriteAsync(
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">\r\n" +
                "  <Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>\r\n" +
                "</Relationships>");
        }

        // 3. word/_rels/document.xml.rels
        var docRelsEntry = archive.CreateEntry("word/_rels/document.xml.rels", CompressionLevel.Fastest);
        await using (var writer = new StreamWriter(docRelsEntry.Open(), new UTF8Encoding(false)))
        {
            await writer.WriteAsync(
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"/>");
        }

        // 4. word/document.xml
        var docEntry = archive.CreateEntry("word/document.xml", CompressionLevel.Fastest);
        await using (var writer = new StreamWriter(docEntry.Open(), new UTF8Encoding(false)))
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n");
            sb.Append("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">\r\n");
            sb.Append("  <w:body>\r\n");

            var lines = (text ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line))
                {
                    sb.Append("    <w:p/>\r\n");
                }
                else
                {
                    string escaped = EscapeXml(line);
                    sb.Append("    <w:p>\r\n");
                    sb.Append("      <w:r>\r\n");
                    sb.Append($"        <w:t xml:space=\"preserve\">{escaped}</w:t>\r\n");
                    sb.Append("      </w:r>\r\n");
                    sb.Append("    </w:p>\r\n");
                }
            }

            // A4 纸张页面属性
            sb.Append("    <w:sectPr>\r\n");
            sb.Append("      <w:pgSz w:w=\"11906\" w:h=\"16838\"/>\r\n");
            sb.Append("      <w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\"/>\r\n");
            sb.Append("    </w:sectPr>\r\n");
            sb.Append("  </w:body>\r\n");
            sb.Append("</w:document>");

            await writer.WriteAsync(sb.ToString());
        }
    }
}
