using System;
using System.Collections.Generic;
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
    void CopyToSystemClipboard(string text);
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
