using System.Text;

namespace ShawMuOCR.Windows.Clipboard;

/// <summary>
/// 构造 Windows 剪贴板 "HTML Format"（CF_HTML）数据。
///
/// CF_HTML 的数据不是裸 HTML，而是「头部描述 + HTML 文档」：
/// <code>
/// Version:0.9
/// StartHTML:0000000123
/// EndHTML:0000000456
/// StartFragment:0000000200
/// EndFragment:0000000300
/// &lt;html&gt;...&lt;!--StartFragment--&gt;片段&lt;!--EndFragment--&gt;...&lt;/html&gt;
/// </code>
/// 其中所有偏移都是**从数据起始位置算起的 UTF-8 字节偏移**，不是字符数。
/// Excel / WPS / Word 等程序粘贴时优先读取该格式，从而把内容还原成真正的表格/富文本；
/// 只支持纯文本的程序（聊天窗口、搜索框、记事本）则忽略它，回落到 CF_UNICODETEXT。
/// </summary>
public static class CfHtmlBuilder
{
    /// <summary>偏移字段固定 10 位宽，保证头部长度恒定，才能先算头部长度再回填偏移</summary>
    private const string HeaderFormat =
        "Version:0.9\r\n" +
        "StartHTML:{0:D10}\r\n" +
        "EndHTML:{1:D10}\r\n" +
        "StartFragment:{2:D10}\r\n" +
        "EndFragment:{3:D10}\r\n";

    private const string DocumentPrefix =
        "<html>\r\n<head>\r\n" +
        "<meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\">\r\n" +
        "</head>\r\n<body>\r\n<!--StartFragment-->";

    private const string DocumentSuffix = "<!--EndFragment-->\r\n</body>\r\n</html>\r\n";

    /// <summary>
    /// 把 HTML 片段包装成完整的 CF_HTML 数据字符串。
    /// </summary>
    /// <param name="htmlFragment">HTML 片段（如 &lt;table&gt;...&lt;/table&gt;）</param>
    public static string Wrap(string htmlFragment)
    {
        ArgumentNullException.ThrowIfNull(htmlFragment);

        int headerLength = Encoding.UTF8.GetByteCount(string.Format(HeaderFormat, 0, 0, 0, 0));
        int prefixLength = Encoding.UTF8.GetByteCount(DocumentPrefix);
        int fragmentLength = Encoding.UTF8.GetByteCount(htmlFragment);
        int suffixLength = Encoding.UTF8.GetByteCount(DocumentSuffix);

        int startHtml = headerLength;
        int startFragment = startHtml + prefixLength;
        int endFragment = startFragment + fragmentLength;
        int endHtml = endFragment + suffixLength;

        string header = string.Format(HeaderFormat, startHtml, endHtml, startFragment, endFragment);
        return string.Concat(header, DocumentPrefix, htmlFragment, DocumentSuffix);
    }

    /// <summary>
    /// 把 HTML 片段包装成可直接写入剪贴板的 UTF-8 字节块（末尾补 NUL，符合 CF_HTML 约定）。
    /// </summary>
    public static byte[] WrapToUtf8Bytes(string htmlFragment)
    {
        byte[] payload = Encoding.UTF8.GetBytes(Wrap(htmlFragment));
        byte[] withTerminator = new byte[payload.Length + 1];
        Buffer.BlockCopy(payload, 0, withTerminator, 0, payload.Length);
        return withTerminator;
    }
}
