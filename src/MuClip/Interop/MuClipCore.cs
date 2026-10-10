using System.Text;
using MuClip.Models;

namespace MuClip.Interop;

/// <summary>
/// Rust 极速剪贴板核心引擎（基于手动租借内存池二进制零拷贝解码与纯内存 LRU + SQLite 零碎片存储）
/// </summary>
public sealed class MuClipCore : IDisposable
{
    private IntPtr _handle;

    public IntPtr Handle => _handle;

    public unsafe MuClipCore(string dbPath)
    {
        byte[] pathBytes = Encoding.UTF8.GetBytes(dbPath + "\0");
        fixed (byte* p = pathBytes)
        {
            _handle = MuClipNative.CoreNew(p);
        }

        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"无法初始化 MuClip 核心数据库，路径: {dbPath}");
        }
    }

    private MuClipCore(IntPtr handle)
    {
        _handle = handle;
    }

    /// <summary>
    /// 创建内存数据库实例（不落地磁盘，适合无痕模式）
    /// </summary>
    public static MuClipCore CreateInMemory()
    {
        IntPtr h = MuClipNative.CoreNewInMemory();
        if (h == IntPtr.Zero)
        {
            throw new InvalidOperationException("无法初始化 MuClip 内存数据库引擎！");
        }
        return new MuClipCore(h);
    }

    /// <summary>
    /// 添加一条剪贴板历史记录（使用手动租借内存池高效传输，自动去重并更新热缓存）
    /// </summary>
    public unsafe long AddHistory(string content)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(content)) return -1;

        int byteCount = Encoding.UTF8.GetByteCount(content);
        if (byteCount < 1024)
        {
            // 小字符串直接使用栈上内存
            byte* pStack = stackalloc byte[byteCount];
            fixed (char* pChars = content)
            {
                Encoding.UTF8.GetBytes(pChars, content.Length, pStack, byteCount);
            }
            return MuClipNative.CoreAddHistoryBuffer(_handle, pStack, (nuint)byteCount);
        }
        else
        {
            // 大文本通过手动租借内存池租用原生缓冲区，避免托管堆大对象 LOH 分配
            using var lease = MuClipMemoryPool.Rent(byteCount);
            fixed (char* pChars = content)
            {
                fixed (byte* pBuf = lease.WritableSpan)
                {
                    Encoding.UTF8.GetBytes(pChars, content.Length, pBuf, byteCount);
                }
            }
            return MuClipNative.CoreAddHistoryBuffer(_handle, (byte*)lease.Pointer, (nuint)byteCount);
        }
    }

    /// <summary>
    /// 获取当前纯内存热缓存中的前 100 条记录（基于 ReadOnlySpan&lt;byte&gt; + BinaryPrimitives 零拷贝二进制解析）
    /// </summary>
    public List<HistoryRecord> GetCachedItems()
    {
        ThrowIfDisposed();
        ulong leaseId = MuClipNative.CoreGetCachedItemsPooled(_handle, out IntPtr ptr, out nuint len);
        return MuClipBinaryDecoder.ReadHistoryFromPooled(leaseId, ptr, len);
    }

    /// <summary>
    /// 查询历史记录（从 SQLite 查询，基于 ReadOnlySpan&lt;byte&gt; + BinaryPrimitives 零拷贝二进制解析）
    /// </summary>
    public List<HistoryRecord> QueryHistory(int limit = 100)
    {
        ThrowIfDisposed();
        if (limit <= 0) return new List<HistoryRecord>();
        ulong leaseId = MuClipNative.CoreQueryHistoryPooled(_handle, (nuint)limit, out IntPtr ptr, out nuint len);
        return MuClipBinaryDecoder.ReadHistoryFromPooled(leaseId, ptr, len);
    }

    /// <summary>
    /// 分页查询历史记录（优先命中前 100 条纯内存热缓存，超出部分由 SQLite 按需分页加载）
    /// </summary>
    public List<HistoryRecord> QueryHistoryPage(int offset, int limit = 100)
    {
        ThrowIfDisposed();
        if (offset < 0 || limit <= 0) return new List<HistoryRecord>();
        ulong leaseId = MuClipNative.CoreQueryHistoryPagePooled(_handle, (nuint)offset, (nuint)limit, out IntPtr ptr, out nuint len);
        return MuClipBinaryDecoder.ReadHistoryFromPooled(leaseId, ptr, len);
    }

    /// <summary>
    /// 获取 SQLite 中持久化的历史记录总条数
    /// </summary>
    public int GetHistoryCount()
    {
        ThrowIfDisposed();
        long count = MuClipNative.CoreCountHistory(_handle);
        return count > 0 ? (int)count : 0;
    }

    /// <summary>
    /// 模糊搜索历史记录（全量检索 SQLite 持久化记录，基于 ReadOnlySpan&lt;byte&gt; + BinaryPrimitives 零拷贝二进制解析）
    /// </summary>
    public unsafe List<HistoryRecord> SearchHistory(string query, int limit = 500)
    {
        ThrowIfDisposed();
        byte[] qBytes = Encoding.UTF8.GetBytes(query + "\0");
        fixed (byte* pQuery = qBytes)
        {
            ulong leaseId = MuClipNative.CoreSearchHistoryPooled(_handle, pQuery, (nuint)limit, out IntPtr ptr, out nuint len);
            return MuClipBinaryDecoder.ReadHistoryFromPooled(leaseId, ptr, len);
        }
    }

    /// <summary>
    /// 删除指定历史记录
    /// </summary>
    public bool DeleteHistory(long id)
    {
        ThrowIfDisposed();
        return MuClipNative.CoreDeleteHistory(_handle, id) == 0;
    }

    /// <summary>
    /// 清空所有历史记录
    /// </summary>
    public bool ClearHistory()
    {
        ThrowIfDisposed();
        return MuClipNative.CoreClearHistory(_handle) == 0;
    }

    /// <summary>
    /// 查询所有常用语（基于 ReadOnlySpan&lt;byte&gt; + BinaryPrimitives 零拷贝二进制解析）
    /// </summary>
    public List<PhraseRecord> QueryPhrases()
    {
        ThrowIfDisposed();
        ulong leaseId = MuClipNative.CoreQueryPhrasesPooled(_handle, out IntPtr ptr, out nuint len);
        return MuClipBinaryDecoder.ReadPhrasesFromPooled(leaseId, ptr, len);
    }

    /// <summary>
    /// 模糊搜索常用语（基于 ReadOnlySpan&lt;byte&gt; + BinaryPrimitives 零拷贝二进制解析）
    /// </summary>
    public unsafe List<PhraseRecord> SearchPhrases(string query)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(query)) return QueryPhrases();

        byte[] qBytes = Encoding.UTF8.GetBytes(query.Trim() + "\0");
        fixed (byte* pQuery = qBytes)
        {
            ulong leaseId = MuClipNative.CoreSearchPhrasesPooled(_handle, pQuery, out IntPtr ptr, out nuint len);
            return MuClipBinaryDecoder.ReadPhrasesFromPooled(leaseId, ptr, len);
        }
    }

    /// <summary>
    /// 添加一条常用语
    /// </summary>
    public unsafe long AddPhrase(string content)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(content)) return -1;

        byte[] bytes = Encoding.UTF8.GetBytes(content.Trim() + "\0");
        fixed (byte* p = bytes)
        {
            return MuClipNative.CoreAddPhrase(_handle, p);
        }
    }

    /// <summary>
    /// 修改指定常用语
    /// </summary>
    public unsafe bool EditPhrase(long id, string newContent)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(newContent)) return false;

        byte[] bytes = Encoding.UTF8.GetBytes(newContent.Trim() + "\0");
        fixed (byte* p = bytes)
        {
            return MuClipNative.CoreEditPhrase(_handle, id, p) == 0;
        }
    }

    /// <summary>
    /// 删除指定常用语
    /// </summary>
    public bool DeletePhrase(long id)
    {
        ThrowIfDisposed();
        return MuClipNative.CoreDeletePhrase(_handle, id) == 0;
    }

    /// <summary>
    /// 更新常用语的使用时间（热度提升）
    /// </summary>
    public bool UpdatePhraseUsed(long id)
    {
        ThrowIfDisposed();
        return MuClipNative.CoreUpdatePhraseUsed(_handle, id) == 0;
    }

    /// <summary>
    /// 设置持久化配置项（SQLite secure_config 表）
    /// </summary>
    public unsafe bool SetConfig(string key, string value)
    {
        ThrowIfDisposed();
        byte[] keyBytes = Encoding.UTF8.GetBytes(key + "\0");
        byte[] valBytes = Encoding.UTF8.GetBytes(value + "\0");
        fixed (byte* pKey = keyBytes)
        fixed (byte* pVal = valBytes)
        {
            return MuClipNative.CoreSetConfig(_handle, pKey, pVal) == 0;
        }
    }

    /// <summary>
    /// 获取持久化配置项（SQLite secure_config 表）
    /// </summary>
    public unsafe string? GetConfig(string key)
    {
        ThrowIfDisposed();
        byte[] keyBytes = Encoding.UTF8.GetBytes(key + "\0");
        fixed (byte* pKey = keyBytes)
        {
            IntPtr ptr = MuClipNative.CoreGetConfig(_handle, pKey);
            if (ptr == IntPtr.Zero) return null;
            try
            {
                return System.Runtime.InteropServices.Marshal.PtrToStringUTF8(ptr);
            }
            finally
            {
                MuClipNative.FreeString(ptr);
            }
        }
    }

    /// <summary>
    /// 分析内容特征（识别类型：URL/JSON/颜色/代码/纯文本，提取行数与预览，基于零拷贝二进制解码）
    /// </summary>
    public static unsafe ContentAnalysisResult AnalyzeContent(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text + "\0");
        fixed (byte* p = bytes)
        {
            ulong leaseId = MuClipNative.CoreAnalyzeContentPooled(p, out IntPtr ptr, out nuint len);
            return MuClipBinaryDecoder.ReadContentAnalysisFromPooled(leaseId, ptr, len, text);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_handle == IntPtr.Zero) throw new ObjectDisposedException(nameof(MuClipCore));
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            MuClipNative.CoreFree(_handle);
            _handle = IntPtr.Zero;
        }
        GC.SuppressFinalize(this);
    }

    ~MuClipCore() => Dispose();
}
