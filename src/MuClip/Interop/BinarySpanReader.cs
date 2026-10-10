using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using MuClip.Models;

namespace MuClip.Interop;

/// <summary>
/// 栈上分配的零拷贝 Little-Endian 二进制游标读取器，
/// 直接基于 Rust 内存池裸指针包装的 <see cref="ReadOnlySpan{T}"/> 读取，无需拷贝到 byte[] 或解析 JSON。
/// </summary>
internal ref struct BinarySpanReader
{
    private readonly ReadOnlySpan<byte> _span;
    private int _pos;

    public BinarySpanReader(ReadOnlySpan<byte> span)
    {
        _span = span;
        _pos = 0;
    }

    public bool ReadBool() => _span[_pos++] != 0;

    public int ReadInt32()
    {
        int val = BinaryPrimitives.ReadInt32LittleEndian(_span.Slice(_pos, 4));
        _pos += 4;
        return val;
    }

    public uint ReadUInt32()
    {
        uint val = BinaryPrimitives.ReadUInt32LittleEndian(_span.Slice(_pos, 4));
        _pos += 4;
        return val;
    }

    public long ReadInt64()
    {
        long val = BinaryPrimitives.ReadInt64LittleEndian(_span.Slice(_pos, 8));
        _pos += 8;
        return val;
    }

    public string ReadString()
    {
        int len = ReadInt32();
        if (len <= 0) return string.Empty;
        string s = Encoding.UTF8.GetString(_span.Slice(_pos, len));
        _pos += len;
        return s;
    }
}

/// <summary>
/// 池化二进制帧解码器，遵循“谁分配，谁释放”的确定性生命周期管理，
/// 统一在 try { ... } finally { MuClipNative.PoolRelease(leaseId); } 中完成零拷贝解析并立即归还租约。
/// </summary>
internal static unsafe class MuClipBinaryDecoder
{
    public static List<HistoryRecord> ReadHistoryFromPooled(ulong leaseId, IntPtr ptr, nuint len)
    {
        if (leaseId == 0) return new List<HistoryRecord>();
        try
        {
            if (ptr == IntPtr.Zero || len == 0) return new List<HistoryRecord>();
            var reader = new BinarySpanReader(new ReadOnlySpan<byte>((void*)ptr, (int)len));
            uint count = reader.ReadUInt32();
            var list = new List<HistoryRecord>((int)count);
            for (uint i = 0; i < count; i++)
            {
                long id = reader.ReadInt64();
                int charCount = reader.ReadInt32();
                int lineCount = reader.ReadInt32();
                int byteCount = reader.ReadInt32();
                string content = reader.ReadString();
                string contentType = reader.ReadString();
                string preview = reader.ReadString();
                string createdAt = reader.ReadString();
                string lastUsedAt = reader.ReadString();
                list.Add(new HistoryRecord
                {
                    Id = id,
                    Content = content,
                    ContentType = string.IsNullOrEmpty(contentType) ? "plain_text" : contentType,
                    CharCount = charCount,
                    LineCount = lineCount,
                    ByteCount = byteCount,
                    Preview = preview,
                    CreatedAt = createdAt,
                    LastUsedAt = lastUsedAt
                });
            }
            return list;
        }
        finally
        {
            MuClipNative.PoolRelease(leaseId);
        }
    }

    public static List<PhraseRecord> ReadPhrasesFromPooled(ulong leaseId, IntPtr ptr, nuint len)
    {
        if (leaseId == 0) return new List<PhraseRecord>();
        try
        {
            if (ptr == IntPtr.Zero || len == 0) return new List<PhraseRecord>();
            var reader = new BinarySpanReader(new ReadOnlySpan<byte>((void*)ptr, (int)len));
            uint count = reader.ReadUInt32();
            var list = new List<PhraseRecord>((int)count);
            for (uint i = 0; i < count; i++)
            {
                long id = reader.ReadInt64();
                long sortOrder = reader.ReadInt64();
                int charCount = reader.ReadInt32();
                int lineCount = reader.ReadInt32();
                int byteCount = reader.ReadInt32();
                string content = reader.ReadString();
                string contentType = reader.ReadString();
                string preview = reader.ReadString();
                string createdAt = reader.ReadString();
                string lastUsedAt = reader.ReadString();
                list.Add(new PhraseRecord
                {
                    Id = id,
                    SortOrder = sortOrder,
                    Content = content,
                    ContentType = string.IsNullOrEmpty(contentType) ? "plain_text" : contentType,
                    CharCount = charCount,
                    LineCount = lineCount,
                    ByteCount = byteCount,
                    Preview = preview,
                    CreatedAt = createdAt,
                    LastUsedAt = lastUsedAt
                });
            }
            return list;
        }
        finally
        {
            MuClipNative.PoolRelease(leaseId);
        }
    }

    public static ContentAnalysisResult ReadContentAnalysisFromPooled(ulong leaseId, IntPtr ptr, nuint len, string fallbackText = "")
    {
        if (leaseId == 0)
        {
            return new ContentAnalysisResult
            {
                ContentType = "plain_text",
                CharCount = fallbackText.Length,
                LineCount = string.IsNullOrEmpty(fallbackText) ? 0 : 1,
                ByteCount = Encoding.UTF8.GetByteCount(fallbackText),
                Preview = fallbackText
            };
        }
        try
        {
            if (ptr == IntPtr.Zero || len == 0)
            {
                return new ContentAnalysisResult
                {
                    ContentType = "plain_text",
                    CharCount = fallbackText.Length,
                    LineCount = string.IsNullOrEmpty(fallbackText) ? 0 : 1,
                    ByteCount = Encoding.UTF8.GetByteCount(fallbackText),
                    Preview = fallbackText
                };
            }
            var reader = new BinarySpanReader(new ReadOnlySpan<byte>((void*)ptr, (int)len));
            int charCount = reader.ReadInt32();
            int lineCount = reader.ReadInt32();
            int byteCount = reader.ReadInt32();
            string contentType = reader.ReadString();
            string preview = reader.ReadString();
            return new ContentAnalysisResult
            {
                ContentType = string.IsNullOrEmpty(contentType) ? "plain_text" : contentType,
                CharCount = charCount,
                LineCount = lineCount,
                ByteCount = byteCount,
                Preview = preview
            };
        }
        finally
        {
            MuClipNative.PoolRelease(leaseId);
        }
    }

    public static List<PeerDeviceInfo> ReadPeersFromPooled(ulong leaseId, IntPtr ptr, nuint len)
    {
        if (leaseId == 0) return new List<PeerDeviceInfo>();
        try
        {
            if (ptr == IntPtr.Zero || len == 0) return new List<PeerDeviceInfo>();
            var reader = new BinarySpanReader(new ReadOnlySpan<byte>((void*)ptr, (int)len));
            uint count = reader.ReadUInt32();
            var list = new List<PeerDeviceInfo>((int)count);
            for (uint i = 0; i < count; i++)
            {
                int syncPort = reader.ReadInt32();
                long lastSeenMs = reader.ReadInt64();
                bool isOnline = reader.ReadBool();
                bool isPaired = reader.ReadBool();
                bool isManual = reader.ReadBool();
                string deviceId = reader.ReadString();
                string deviceName = reader.ReadString();
                string deviceType = reader.ReadString();
                string ip = reader.ReadString();
                string fingerprint = reader.ReadString();
                list.Add(new PeerDeviceInfo
                {
                    DeviceId = deviceId,
                    DeviceName = deviceName,
                    DeviceType = deviceType,
                    Ip = ip,
                    SyncPort = syncPort,
                    LastSeenMs = lastSeenMs,
                    IsOnline = isOnline,
                    Fingerprint = fingerprint,
                    IsPaired = isPaired,
                    IsManual = isManual
                });
            }
            return list;
        }
        finally
        {
            MuClipNative.PoolRelease(leaseId);
        }
    }

    public static List<string> ReadStringListFromPooled(ulong leaseId, IntPtr ptr, nuint len)
    {
        if (leaseId == 0) return new List<string>();
        try
        {
            if (ptr == IntPtr.Zero || len == 0) return new List<string>();
            var reader = new BinarySpanReader(new ReadOnlySpan<byte>((void*)ptr, (int)len));
            uint count = reader.ReadUInt32();
            var list = new List<string>((int)count);
            for (uint i = 0; i < count; i++)
            {
                list.Add(reader.ReadString());
            }
            return list;
        }
        finally
        {
            MuClipNative.PoolRelease(leaseId);
        }
    }
}
