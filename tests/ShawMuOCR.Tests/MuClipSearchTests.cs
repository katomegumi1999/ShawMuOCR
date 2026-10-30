using System;
using System.IO;
using System.Linq;
using MuClip.Interop;
using Xunit;

namespace ShawMuOCR.Tests;

/// <summary>
/// 剪贴板搜索的端到端测试（C# → FFI → Rust → SQLite）。
///
/// 这里覆盖的是**跨语言边界**才可能出问题的地方：
/// 1. 结构化请求的 JSON 字段名/类型必须与 Rust 侧 serde 定义一致（不一致不会编译报错，只会静默返回空）；
/// 2. 类型筛选必须在 SQL 侧生效，而不是取一页再在内存里过滤（旧实现正是后者，会漏结果）；
/// 3. LIKE 元字符必须按字面量匹配（搜 `%` 不能命中全库）。
/// </summary>
public class MuClipSearchTests : IDisposable
{
    private readonly string _dbPath;
    private readonly MuClipCore _core;

    public MuClipSearchTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"muclip-search-{Guid.NewGuid():N}.db");
        _core = new MuClipCore(_dbPath);
    }

    public void Dispose()
    {
        _core.Dispose();
        try
        {
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
            foreach (string suffix in new[] { "-wal", "-shm" })
            {
                if (File.Exists(_dbPath + suffix)) File.Delete(_dbPath + suffix);
            }
        }
        catch (IOException)
        {
            // 临时文件清理失败不影响测试结论
        }
    }

    /// <summary>内容类型编号（与 Rust 侧 ContentType::to_db_i64 一致）。</summary>
    private const int TypePlainText = 0;
    private const int TypeUrl = 1;

    private void Seed()
    {
        for (int i = 0; i < 30; i++)
        {
            _core.AddHistory($"普通文本条目 {i}");
        }
        _core.AddHistory("https://example.com/alpha");
        _core.AddHistory("https://example.com/beta");
        _core.AddHistory("折扣 50% 起");
    }

    [Fact]
    public void SearchPage_FiltersByTypeInSql_NotAfterPaging()
    {
        Seed();

        // 30 条文本按时间排在前面。若类型过滤发生在内存里，limit=10 时拿不到任何 URL。
        var urls = _core.SearchHistoryPage(string.Empty, new[] { TypeUrl }, 0, 10);

        Assert.Equal(2, urls.Count);
        Assert.All(urls, r => Assert.Equal("url", r.ContentType));
        Assert.Equal(2, _core.CountSearch(string.Empty, new[] { TypeUrl }));
    }

    [Fact]
    public void SearchPage_TreatsLikeWildcardsAsLiterals()
    {
        Seed();

        // 只有一条内容含字面量 %
        Assert.Equal(1, _core.CountSearch("%", null));
        var hits = _core.SearchHistoryPage("%", null, 0, 50);
        Assert.Single(hits);
        Assert.Contains("50%", hits[0].Content, StringComparison.Ordinal);

        // 下划线同理，不能退化成「任意单字符」
        Assert.Equal(0, _core.CountSearch("_", null));
    }

    [Fact]
    public void SearchPage_PagesWithoutOverlapAndCountIsStable()
    {
        Seed();
        int total = _core.CountSearch(string.Empty, null);
        Assert.Equal(33, total);

        var page1 = _core.SearchHistoryPage(string.Empty, null, 0, 10);
        var page2 = _core.SearchHistoryPage(string.Empty, null, 10, 10);
        Assert.Equal(10, page1.Count);
        Assert.Equal(10, page2.Count);

        var ids = page1.Select(r => r.Id).Concat(page2.Select(r => r.Id)).ToHashSet();
        Assert.Equal(20, ids.Count);
    }

    [Fact]
    public void SearchPage_EmptyResultIsNotAnError()
    {
        Seed();

        Assert.Equal(0, _core.CountSearch("绝不可能出现的关键词zzz", null));
        Assert.Empty(_core.SearchHistoryPage("绝不可能出现的关键词zzz", null, 0, 50));
    }

    [Fact]
    public void SearchPage_TypeFilterCombinesWithKeyword()
    {
        Seed();

        Assert.Equal(1, _core.CountSearch("beta", new[] { TypeUrl }));
        Assert.Equal(0, _core.CountSearch("beta", new[] { TypePlainText }));
    }
}
