using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using ShawMuOCR.Core.Export;
using Xunit;

namespace ShawMuOCR.Tests;

public class BatchExportHelperTests
{
    [Fact]
    public void GetColumnLetter_ConvertsCorrectly()
    {
        Assert.Equal("A", BatchExportHelper.GetColumnLetter(1));
        Assert.Equal("B", BatchExportHelper.GetColumnLetter(2));
        Assert.Equal("Z", BatchExportHelper.GetColumnLetter(26));
        Assert.Equal("AA", BatchExportHelper.GetColumnLetter(27));
        Assert.Equal("AB", BatchExportHelper.GetColumnLetter(28));
        Assert.Equal("AZ", BatchExportHelper.GetColumnLetter(52));
        Assert.Equal("BA", BatchExportHelper.GetColumnLetter(53));
    }

    [Fact]
    public void EscapeXml_EscapesSpecialAndStripsControlCharacters()
    {
        string raw = "Hello & <World> \" ' \u0001\u0002 Test \t \r \n";
        string escaped = BatchExportHelper.EscapeXml(raw);

        Assert.Contains("&amp;", escaped);
        Assert.Contains("&lt;", escaped);
        Assert.Contains("&gt;", escaped);
        Assert.Contains("&quot;", escaped);
        Assert.Contains("&apos;", escaped);
        Assert.DoesNotContain('\u0001', escaped);
        Assert.DoesNotContain('\u0002', escaped);
        Assert.Contains("\t", escaped);
        Assert.Contains("\n", escaped);
    }

    [Fact]
    public async Task ExportTxtAsync_GeneratesUtf8File()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"test_export_{Guid.NewGuid():N}.txt");
        try
        {
            string content = "第一行文本\r\n第二行内容\r\nEnglish 123";
            await BatchExportHelper.ExportTxtAsync(content, tempFile);

            Assert.True(File.Exists(tempFile));
            string read = await File.ReadAllTextAsync(tempFile, Encoding.UTF8);
            Assert.Equal(content, read);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ExportXlsxAsync_GeneratesValidOpenXmlSpreadsheet()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"test_export_{Guid.NewGuid():N}.xlsx");
        try
        {
            string content = "标题行\t数量\t备注\r\n商品A\t100\t正常\r\n\r\n末尾行";
            await BatchExportHelper.ExportXlsxAsync(content, tempFile);

            Assert.True(File.Exists(tempFile));

            // 验证 ZIP 结构
            using (var archive = ZipFile.OpenRead(tempFile))
            {
                Assert.NotNull(archive.GetEntry("[Content_Types].xml"));
                Assert.NotNull(archive.GetEntry("_rels/.rels"));
                Assert.NotNull(archive.GetEntry("xl/workbook.xml"));
                Assert.NotNull(archive.GetEntry("xl/_rels/workbook.xml.rels"));

                var sheetEntry = archive.GetEntry("xl/worksheets/sheet1.xml");
                Assert.NotNull(sheetEntry);

                using var stream = sheetEntry.Open();
                var doc = XDocument.Load(stream);
                XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

                var rows = doc.Descendants(ns + "row").ToList();
                Assert.Equal(4, rows.Count); // 4行 (含空行)

                // 第一行有3列: A1, B1, C1
                var firstRowCells = rows[0].Elements(ns + "c").ToList();
                Assert.Equal(3, firstRowCells.Count);
                Assert.Equal("A1", firstRowCells[0].Attribute("r")?.Value);
                Assert.Equal("B1", firstRowCells[1].Attribute("r")?.Value);
                Assert.Equal("C1", firstRowCells[2].Attribute("r")?.Value);

                // 验证 inlineStr 文本
                var textElement = firstRowCells[0].Element(ns + "is")?.Element(ns + "t");
                Assert.Equal("标题行", textElement?.Value);
            }
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ExportDocxAsync_GeneratesValidOpenXmlDocument()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"test_export_{Guid.NewGuid():N}.docx");
        try
        {
            string content = "第一段文本：小沐 OCR\r\n\r\n第二段文本：2026 最新版本";
            await BatchExportHelper.ExportDocxAsync(content, tempFile);

            Assert.True(File.Exists(tempFile));

            // 验证 ZIP 结构
            using (var archive = ZipFile.OpenRead(tempFile))
            {
                Assert.NotNull(archive.GetEntry("[Content_Types].xml"));
                Assert.NotNull(archive.GetEntry("_rels/.rels"));
                Assert.NotNull(archive.GetEntry("word/_rels/document.xml.rels"));

                var docEntry = archive.GetEntry("word/document.xml");
                Assert.NotNull(docEntry);

                using var stream = docEntry.Open();
                var doc = XDocument.Load(stream);
                XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

                var paragraphs = doc.Descendants(w + "p").ToList();
                Assert.Equal(3, paragraphs.Count); // 第一行, 空行, 第二行

                var firstRunText = paragraphs[0].Descendants(w + "t").FirstOrDefault();
                Assert.Equal("第一段文本：小沐 OCR", firstRunText?.Value);
            }
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
