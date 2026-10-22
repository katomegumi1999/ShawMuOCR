using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MuClip.Models;

namespace MuClip.Interop;

/// <summary>
/// Rust 极速剪贴板核心引擎（基于手动租借内存池二进制零拷贝解码与纯内存 LRU + SQLite 零碎片存储）
/// </summary>
public sealed class MuClipCore : IDisposable
{
    private IntPtr _handle;

    public IntPtr Handle => _handle;

    public unsafe MuClipCore(string dbPath, string? encryptionKey = null)
    {
        byte[] pathBytes = Encoding.UTF8.GetBytes(dbPath + "\0");
        fixed (byte* p = pathBytes)
        {
            if (string.IsNullOrEmpty(encryptionKey))
            {
                _handle = MuClipNative.CoreNew(p);
            }
            else
            {
                byte[] keyBytes = Encoding.UTF8.GetBytes(encryptionKey + "\0");
                fixed (byte* pKey = keyBytes)
                {
                    _handle = MuClipNative.CoreNewWithKey(p, pKey);
                }
            }
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
    /// 批量按传入的 ID 顺序更新 sort_order (0, 1, 2...)
    /// </summary>
    public unsafe bool ReorderPhrases(IReadOnlyList<long> orderedIds)
    {
        ThrowIfDisposed();
        if (orderedIds == null || orderedIds.Count == 0) return false;
        long[] idArray = orderedIds.ToArray();
        fixed (long* p = idArray)
        {
            return MuClipNative.CoreReorderPhrases(_handle, p, (nuint)idArray.Length) == 0;
        }
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
    /// 添加或更新时段规则（写入 SQLite phrase_rules 表）
    /// </summary>
    public unsafe string? AddPhraseRule(PhraseConditionalRule rule)
    {
        ThrowIfDisposed();
        if (rule == null) return null;
        string json = System.Text.Json.JsonSerializer.Serialize(rule, PhraseRuleJsonContext.Default.PhraseConditionalRule);
        byte[] bytes = Encoding.UTF8.GetBytes(json + "\0");
        fixed (byte* p = bytes)
        {
            IntPtr ptr = MuClipNative.CoreAddPhraseRule(_handle, p);
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
    /// 删除时段规则（从 SQLite phrase_rules 表删除）
    /// </summary>
    public unsafe bool DeletePhraseRule(string ruleId)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(ruleId)) return false;
        byte[] bytes = Encoding.UTF8.GetBytes(ruleId + "\0");
        fixed (byte* p = bytes)
        {
            return MuClipNative.CoreDeletePhraseRule(_handle, p) == 0;
        }
    }

    /// <summary>
    /// 查询指定常用语的时段规则列表（phrase_id <= 0 查询全部）
    /// </summary>
    public unsafe List<PhraseConditionalRule> QueryPhraseRules(long phraseId = 0)
    {
        ThrowIfDisposed();
        IntPtr ptr = MuClipNative.CoreQueryPhraseRules(_handle, phraseId);
        if (ptr == IntPtr.Zero) return new List<PhraseConditionalRule>();
        try
        {
            string? json = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(ptr);
            if (string.IsNullOrWhiteSpace(json)) return new List<PhraseConditionalRule>();
            return System.Text.Json.JsonSerializer.Deserialize(json, PhraseRuleJsonContext.Default.ListPhraseConditionalRule) ?? new List<PhraseConditionalRule>();
        }
        catch
        {
            return new List<PhraseConditionalRule>();
        }
        finally
        {
            MuClipNative.FreeString(ptr);
        }
    }

    /// <summary>
    /// 保存或更新规则模板（写入 SQLite phrase_rule_templates 表）
    /// </summary>
    public unsafe string? SaveRuleTemplate(PhraseRuleTemplate template)
    {
        ThrowIfDisposed();
        if (template == null) return null;
        string json = System.Text.Json.JsonSerializer.Serialize(template, PhraseRuleJsonContext.Default.PhraseRuleTemplate);
        byte[] bytes = Encoding.UTF8.GetBytes(json + "\0");
        fixed (byte* p = bytes)
        {
            IntPtr ptr = MuClipNative.CoreSaveTemplate(_handle, p);
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
    /// 删除规则模板（从 SQLite phrase_rule_templates 表删除）
    /// </summary>
    public unsafe bool DeleteRuleTemplate(string templateId)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(templateId)) return false;
        byte[] bytes = Encoding.UTF8.GetBytes(templateId + "\0");
        fixed (byte* p = bytes)
        {
            return MuClipNative.CoreDeleteTemplate(_handle, p) == 0;
        }
    }

    /// <summary>
    /// 查询所有规则模板列表
    /// </summary>
    public unsafe List<PhraseRuleTemplate> QueryRuleTemplates()
    {
        ThrowIfDisposed();
        IntPtr ptr = MuClipNative.CoreQueryTemplates(_handle);
        if (ptr == IntPtr.Zero) return new List<PhraseRuleTemplate>();
        try
        {
            string? json = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(ptr);
            if (string.IsNullOrWhiteSpace(json)) return new List<PhraseRuleTemplate>();
            return System.Text.Json.JsonSerializer.Deserialize(json, PhraseRuleJsonContext.Default.ListPhraseRuleTemplate) ?? new List<PhraseRuleTemplate>();
        }
        catch
        {
            return new List<PhraseRuleTemplate>();
        }
        finally
        {
            MuClipNative.FreeString(ptr);
        }
    }

    /// <summary>
    /// 导出全量加密数据库备份文件 (.mubc)
    /// </summary>
    /// <param name="exportPath">导出的备份文件目标路径</param>
    /// <param name="password">加密密码（由用户输入的独立主口令）</param>
    /// <returns>备份统计信息对象，若失败则返回 null</returns>
    public unsafe BackupSummary? ExportEncryptedBackup(string exportPath, string password)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(exportPath)) throw new ArgumentException("导出路径不能为空", nameof(exportPath));
        if (string.IsNullOrWhiteSpace(password)) throw new ArgumentException("备份密码不能为空", nameof(password));

        byte[] pathBytes = Encoding.UTF8.GetBytes(exportPath + "\0");
        byte[] pwdBytes = Encoding.UTF8.GetBytes(password + "\0");
        fixed (byte* pPath = pathBytes)
        fixed (byte* pPwd = pwdBytes)
        {
            IntPtr ptr = MuClipNative.CoreExportDb(_handle, pPath, pPwd);
            if (ptr == IntPtr.Zero) return null;
            try
            {
                string? json = Marshal.PtrToStringUTF8(ptr);
                if (string.IsNullOrWhiteSpace(json)) return null;
                return JsonSerializer.Deserialize(json, PhraseRuleJsonContext.Default.BackupSummary);
            }
            finally
            {
                MuClipNative.FreeString(ptr);
            }
        }
    }

    /// <summary>
    /// 导入全量加密数据库备份文件 (.mubc)
    /// </summary>
    /// <param name="importPath">备份文件路径</param>
    /// <param name="password">解密密码</param>
    /// <param name="replaceAll">true 为完全覆盖当前库；false 为增量合并去重</param>
    /// <returns>恢复统计信息对象，若密码错误或损坏则返回 null</returns>
    public unsafe BackupSummary? ImportEncryptedBackup(string importPath, string password, bool replaceAll)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(importPath)) throw new ArgumentException("导入路径不能为空", nameof(importPath));
        if (string.IsNullOrWhiteSpace(password)) throw new ArgumentException("解密密码不能为空", nameof(password));

        byte[] pathBytes = Encoding.UTF8.GetBytes(importPath + "\0");
        byte[] pwdBytes = Encoding.UTF8.GetBytes(password + "\0");
        fixed (byte* pPath = pathBytes)
        fixed (byte* pPwd = pwdBytes)
        {
            IntPtr ptr = MuClipNative.CoreImportDb(_handle, pPath, pPwd, replaceAll ? 1 : 0);
            if (ptr == IntPtr.Zero) return null;
            try
            {
                string? json = Marshal.PtrToStringUTF8(ptr);
                if (string.IsNullOrWhiteSpace(json)) return null;
                return JsonSerializer.Deserialize(json, PhraseRuleJsonContext.Default.BackupSummary);
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
