using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace ShawMuOCR.Services.Http;

/// <summary>
/// 纯 .NET BCL 零外部依赖的高性能 Multipart/Form-Data 内存解析器
/// </summary>
public static class HttpMultipartParser
{
    public sealed class ParseResult
    {
        public byte[]? FileBytes { get; set; }
        public string? FileName { get; set; }
        public Dictionary<string, string> FormFields { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 默认请求体上限（64MB），仅用于兼容旧调用点。
    /// </summary>
    private const long DefaultMaxBytes = 64L * 1024 * 1024;

    public static ParseResult? Parse(Stream stream, string? contentType)
        => Parse(stream, contentType, DefaultMaxBytes, CancellationToken.None);

    /// <summary>
    /// 解析 Multipart 请求体，并对读取总量设置硬上限，防止超大请求耗尽内存。
    /// </summary>
    /// <exception cref="PayloadTooLargeException">累计读取量超过 <paramref name="maxBytes"/> 时抛出</exception>
    public static ParseResult? Parse(Stream stream, string? contentType, long maxBytes, CancellationToken cancellationToken = default)
    {
        if (stream == null || string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        string? boundary = ExtractBoundary(contentType);
        if (string.IsNullOrEmpty(boundary))
        {
            return null;
        }

        byte[] boundaryBytes = Encoding.ASCII.GetBytes("--" + boundary);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81_920);
        byte[] rawBytes;
        using (var ms = new MemoryStream())
        {
            try
            {
                long total = 0;
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    total += read;
                    if (total > maxBytes)
                    {
                        throw new PayloadTooLargeException(maxBytes, total);
                    }
                    ms.Write(buffer, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            rawBytes = ms.ToArray();
        }

        var result = new ParseResult();
        ReadOnlySpan<byte> data = rawBytes;

        int searchIndex = 0;
        while (searchIndex < data.Length)
        {
            int boundaryIndex = data.Slice(searchIndex).IndexOf(boundaryBytes);
            if (boundaryIndex < 0) break;

            int partStart = searchIndex + boundaryIndex + boundaryBytes.Length;
            // 跳过可能紧随的 \r\n
            if (partStart + 2 <= data.Length && data[partStart] == '\r' && data[partStart + 1] == '\n')
            {
                partStart += 2;
            }
            else if (partStart + 2 <= data.Length && data[partStart] == '-' && data[partStart + 1] == '-')
            {
                // 结束边界 --boundary--
                break;
            }

            int nextBoundaryIndex = data.Slice(partStart).IndexOf(boundaryBytes);
            if (nextBoundaryIndex < 0) break;

            int partEnd = partStart + nextBoundaryIndex;
            // 剥除末尾的前置 \r\n
            if (partEnd >= partStart + 2 && data[partEnd - 2] == '\r' && data[partEnd - 1] == '\n')
            {
                partEnd -= 2;
            }

            if (partEnd > partStart)
            {
                ParsePart(data.Slice(partStart, partEnd - partStart), result);
            }

            searchIndex = partStart + nextBoundaryIndex;
        }

        return result;
    }

    private static void ParsePart(ReadOnlySpan<byte> partData, ParseResult result)
    {
        // 查找 Header 与 Body 的分隔符 \r\n\r\n
        ReadOnlySpan<byte> delimiter = stackalloc byte[] { 13, 10, 13, 10 };
        int headerEnd = partData.IndexOf(delimiter);
        int delimiterLen = 4;

        if (headerEnd < 0)
        {
            // 尝试宽松的 \n\n
            ReadOnlySpan<byte> delimiterLf = stackalloc byte[] { 10, 10 };
            headerEnd = partData.IndexOf(delimiterLf);
            delimiterLen = 2;
            if (headerEnd < 0) return;
        }

        string headerText = Encoding.UTF8.GetString(partData.Slice(0, headerEnd));
        ReadOnlySpan<byte> bodyData = partData.Slice(headerEnd + delimiterLen);

        string? fieldName = null;
        string? fileName = null;

        var lines = headerText.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith("Content-Disposition", StringComparison.OrdinalIgnoreCase))
            {
                fieldName = ExtractHeaderAttribute(line, "name");
                fileName = ExtractHeaderAttribute(line, "filename");
            }
        }

        if (!string.IsNullOrEmpty(fileName) || (fieldName != null && (fieldName.Equals("file", StringComparison.OrdinalIgnoreCase) || fieldName.Equals("image", StringComparison.OrdinalIgnoreCase))))
        {
            result.FileBytes = bodyData.ToArray();
            result.FileName = fileName;
        }
        else if (!string.IsNullOrEmpty(fieldName))
        {
            string fieldValue = Encoding.UTF8.GetString(bodyData);
            result.FormFields[fieldName] = fieldValue.Trim();
        }
        else if (result.FileBytes == null && bodyData.Length > 0)
        {
            // 兜底：如果未指定文件名或字段名，但有二进制内容，当成文件
            result.FileBytes = bodyData.ToArray();
        }
    }

    private static string? ExtractBoundary(string contentType)
    {
        var parts = contentType.Split(';');
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase))
            {
                string val = trimmed["boundary=".Length..].Trim();
                if (val.StartsWith('"') && val.EndsWith('"') && val.Length >= 2)
                {
                    val = val[1..^1];
                }
                return val;
            }
        }
        return null;
    }

    private static string? ExtractHeaderAttribute(string headerLine, string attributeName)
    {
        string pattern = attributeName + "=";
        int idx = headerLine.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        string sub = headerLine[(idx + pattern.Length)..].Trim();
        if (sub.StartsWith('"'))
        {
            int end = sub.IndexOf('"', 1);
            if (end > 0)
            {
                return sub[1..end];
            }
        }
        else
        {
            int end = sub.IndexOf(';');
            if (end > 0)
            {
                return sub[..end].Trim();
            }
            return sub.Trim();
        }
        return null;
    }
}
