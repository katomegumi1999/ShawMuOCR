using System.Text;
using ShawMuOCR.Windows.Clipboard;
using Xunit;

namespace ShawMuOCR.Tests;

/// <summary>
/// CF_HTML 包装的正确性测试。偏移量一旦算错（尤其是中文等多字节字符场景），
/// Excel / WPS 就取不到表格片段，粘贴会退化成一大段乱文本，因此这里逐字节校验。
/// </summary>
public class CfHtmlBuilderTests
{
    private const string Fragment = "<table><tr><td>中文物资</td><td>120</td></tr></table>";

    [Fact]
    public void Wrap_OffsetsMatchActualUtf8BytePositions()
    {
        string cfHtml = CfHtmlBuilder.Wrap(Fragment);
        byte[] bytes = Encoding.UTF8.GetBytes(cfHtml);

        int startHtml = ReadOffset(cfHtml, "StartHTML:");
        int endHtml = ReadOffset(cfHtml, "EndHTML:");
        int startFragment = ReadOffset(cfHtml, "StartFragment:");
        int endFragment = ReadOffset(cfHtml, "EndFragment:");

        // EndHTML 必须正好等于数据总长度（UTF-8 字节数，而非字符数）
        Assert.Equal(bytes.Length, endHtml);

        // StartHTML 必须正指向 <html>
        Assert.Equal("<html>", Encoding.UTF8.GetString(bytes, startHtml, 6));

        // StartFragment/EndFragment 之间必须精确等于原始片段（中文参与时最能暴露按字符数计算的错误）
        Assert.Equal(Fragment, Encoding.UTF8.GetString(bytes, startFragment, endFragment - startFragment));
    }

    [Fact]
    public void Wrap_HeaderIsWellFormedAndFragmentMarkersPresent()
    {
        string cfHtml = CfHtmlBuilder.Wrap(Fragment);

        Assert.StartsWith("Version:0.9\r\n", cfHtml);
        Assert.Contains("<!--StartFragment-->", cfHtml);
        Assert.Contains("<!--EndFragment-->", cfHtml);
        Assert.Contains("charset=utf-8", cfHtml);

        // 头部偏移字段固定 10 位，保证长度恒定
        Assert.Matches(@"StartHTML:\d{10}\r\n", cfHtml);
        Assert.Matches(@"EndFragment:\d{10}\r\n", cfHtml);
    }

    [Fact]
    public void WrapToUtf8Bytes_AppendsNullTerminator()
    {
        byte[] payload = CfHtmlBuilder.WrapToUtf8Bytes(Fragment);
        byte[] plain = Encoding.UTF8.GetBytes(CfHtmlBuilder.Wrap(Fragment));

        Assert.Equal(plain.Length + 1, payload.Length);
        Assert.Equal(0, payload[^1]);
        Assert.Equal(plain, payload[..^1]);
    }

    [Fact]
    public void Wrap_EmptyFragment_StillProducesValidDocument()
    {
        string cfHtml = CfHtmlBuilder.Wrap(string.Empty);
        byte[] bytes = Encoding.UTF8.GetBytes(cfHtml);

        int startFragment = ReadOffset(cfHtml, "StartFragment:");
        int endFragment = ReadOffset(cfHtml, "EndFragment:");

        Assert.Equal(0, endFragment - startFragment);
        Assert.Equal(bytes.Length, ReadOffset(cfHtml, "EndHTML:"));
    }

    private static int ReadOffset(string cfHtml, string key)
    {
        foreach (string line in cfHtml.Split("\r\n"))
        {
            if (line.StartsWith(key, StringComparison.Ordinal))
            {
                return int.Parse(line[key.Length..]);
            }
        }

        throw new InvalidOperationException($"CF_HTML 缺少偏移字段: {key}");
    }
}
