using System;
using System.Collections.Generic;
using System.Linq;
using MuClip.Models;

namespace MuClip.Services;

public partial class MuClipService
{
    public IReadOnlyList<MuClipItem> HistoryItems
    {
        get
        {
            lock (_lock)
            {
                return _items.ToList();
            }
        }
    }

    public int TotalHistoryCount
    {
        get
        {
            lock (_lock)
            {
                return Math.Max(_items.Count, _totalHistoryCount);
            }
        }
    }

    public event Action? HistoryChanged;

    public void CopyToSystemClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        lock (_lock)
        {
            _lastCapturedText = text;
            _lastCapturedTick = Environment.TickCount64;
        }

        _clipboardService.SetText(text, suppressNotification: true);

        // 用户在 MuClip 界面中主动点击某项历史记录复制时，该项在历史中同样前移至首位
        _core?.AddHistory(text);
        RefreshHistory();
    }

    public void AddRecord(string content, bool broadcast = true)
    {
        if (string.IsNullOrWhiteSpace(content)) return;

        lock (_lock)
        {
            _lastCapturedText = content;
            _lastCapturedTick = Environment.TickCount64;
        }

        _core?.AddHistory(content);
        RefreshHistory();

        if (broadcast && _sync != null)
        {
            _sync.Broadcast(content, "text");
        }
    }

    public void DeleteRecord(long id)
    {
        if (_core == null) return;
        _core.DeleteHistory(id);
        RefreshHistory();
    }

    public void ClearRecords()
    {
        if (_core == null) return;
        _core.ClearHistory();
        RefreshHistory();
    }

    public int DeleteHistoryByTimeRange(DateTime? startTime, DateTime? endTime)
    {
        if (_core == null) return 0;

        // 若未提供任何时间范围限制，等同于一键清空全部历史
        if (startTime == null && endTime == null)
        {
            int countBefore = TotalHistoryCount;
            _core.ClearHistory();
            RefreshHistory();
            return countBefore;
        }

        int deletedCount = 0;
        int offset = 0;
        const int pageSize = 100;
        var matchedIds = new List<long>();

        // 分页扫描历史记录，比对时间区间（先搜集匹配 ID，避免删除导致游标偏移）
        while (true)
        {
            var pageRecords = _core.QueryHistoryPage(offset, pageSize);
            if (pageRecords == null || pageRecords.Count == 0)
            {
                break;
            }

            foreach (var rec in pageRecords)
            {
                DateTime? itemTime = ParseRecordDateTime(rec.CreatedAt);
                if (itemTime.HasValue)
                {
                    bool match = true;
                    if (startTime.HasValue && itemTime.Value < startTime.Value)
                    {
                        match = false;
                    }
                    if (endTime.HasValue && itemTime.Value > endTime.Value)
                    {
                        match = false;
                    }
                    if (match)
                    {
                        matchedIds.Add(rec.Id);
                    }
                }
            }

            offset += pageRecords.Count;
            if (pageRecords.Count < pageSize)
            {
                break;
            }
        }

        // 统一批量执行删除
        foreach (long id in matchedIds)
        {
            try
            {
                _core.DeleteHistory(id);
                deletedCount++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MuClip] 删除历史记录 #{id} 异常: {ex.Message}");
            }
        }

        if (deletedCount > 0)
        {
            RefreshHistory();
        }

        return deletedCount;
    }

    public static DateTime? ParseRecordDateTime(string? timeStr)
    {
        if (string.IsNullOrWhiteSpace(timeStr)) return null;

        if (DateTime.TryParse(timeStr, out var dt))
        {
            return dt;
        }

        // 兼容 Unix 时间戳（秒或毫秒）
        if (long.TryParse(timeStr, out long unixTime))
        {
            if (unixTime > 1000000000000L) // 毫秒级时间戳
                return DateTimeOffset.FromUnixTimeMilliseconds(unixTime).LocalDateTime;
            if (unixTime > 100000000L) // 秒级时间戳
                return DateTimeOffset.FromUnixTimeSeconds(unixTime).LocalDateTime;
        }

        return null;
    }

    public List<MuClipItem> QueryHistoryPage(int offset, int limit = 100)
    {
        if (_core == null) return new List<MuClipItem>();
        var records = _core.QueryHistoryPage(offset, limit);
        var result = new List<MuClipItem>(records.Count);
        foreach (var r in records)
        {
            result.Add(MuClipItem.FromRecord(r));
        }
        return result;
    }

    public List<MuClipItem> Search(string query, int limit = 500)
    {
        if (_core == null) return new List<MuClipItem>();
        if (string.IsNullOrWhiteSpace(query))
        {
            return HistoryItems.ToList();
        }
        var records = _core.SearchHistory(query, limit);
        return records.Select(r => MuClipItem.FromRecord(r)).ToList();
    }

    /// <summary>
    /// 结构化分页搜索：关键词与内容类型都下推到 SQLite，只回传当前页。
    /// </summary>
    /// <param name="query">字面量子串；空串表示不限关键词</param>
    /// <param name="typeIds">内容类型白名单（Rust 侧编号），null/空表示不限</param>
    public List<MuClipItem> SearchPage(string query, int[]? typeIds, int offset, int limit)
    {
        if (_core == null) return new List<MuClipItem>();
        var records = _core.SearchHistoryPage(query ?? string.Empty, typeIds, offset, limit);
        var result = new List<MuClipItem>(records.Count);
        foreach (var r in records)
        {
            result.Add(MuClipItem.FromRecord(r));
        }
        return result;
    }

    /// <summary>
    /// 命中总数（不受分页影响）。失败时返回 0，界面显示「0 条」比抛异常更安全。
    /// </summary>
    public int CountSearch(string query, int[]? typeIds)
    {
        if (_core == null) return 0;
        int n = _core.CountSearch(query ?? string.Empty, typeIds);
        return n < 0 ? 0 : n;
    }

    public void RefreshHistory()
    {
        if (_core == null) return;
        var cached = _core.GetCachedItems();
        int totalCount = _core.GetHistoryCount();
        lock (_lock)
        {
            _items.Clear();
            foreach (var rec in cached)
            {
                _items.Add(MuClipItem.FromRecord(rec));
            }
            _totalHistoryCount = Math.Max(_items.Count, totalCount);
        }
        HistoryChanged?.Invoke();
    }
}
