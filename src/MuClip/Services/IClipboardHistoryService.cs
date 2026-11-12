using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MuClip.Models;

namespace MuClip.Services;

/// <summary>
/// 剪贴板历史记录服务契约
/// 职责：提供剪贴板历史条目的增、删、查、改、时间段批量清理及搜索分页
/// </summary>
public interface IClipboardHistoryService
{
    IReadOnlyList<MuClipItem> HistoryItems { get; }
    int TotalHistoryCount { get; }
    event Action? HistoryChanged;

    void AddRecord(string content, bool broadcast = true);

    /// <summary>
    /// 把文本写进系统剪贴板（异步：真正的 I/O 在线程池上执行，UI 线程只 await）。
    /// 返回 false 表示**没写进去**：要么剪贴板被其他程序占着超过重试窗口，
    /// 要么这期间已有更新的内容落地而主动让位。调用方必须如实反映，不要宣称"已复制"。
    /// </summary>
    Task<bool> CopyToSystemClipboardAsync(string text);
    void DeleteRecord(long id);
    void ClearRecords();
    int DeleteHistoryByTimeRange(DateTime? startTime, DateTime? endTime);
    List<MuClipItem> QueryHistoryPage(int offset, int limit = 100);
    List<MuClipItem> Search(string query, int limit = 500);

    /// <summary>
    /// 结构化分页搜索：关键词 + 内容类型白名单 + 分页全部在 SQL 侧生效。
    /// </summary>
    List<MuClipItem> SearchPage(string query, int[]? typeIds, int offset, int limit);

    /// <summary>命中总数（不受分页影响）。</summary>
    int CountSearch(string query, int[]? typeIds);

    void RefreshHistory();
}
