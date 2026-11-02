using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Apage.Core.Data;
using Xunit;

namespace Apage.Core.Tests;

/// <summary>
/// B2 历史仓储：只收 http/https、时间倒序、搜索转义、保留期裁剪与清空。
/// </summary>
public sealed class HistoryRepositoryTests : IDisposable
{
    private readonly string _dir;
    private readonly HistoryRepository _history;

    public HistoryRepositoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ApageTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        SqliteProvider.EnsureInitialized();
        _history = new HistoryRepository(new SqliteDatabase(Path.Combine(_dir, "apage.db")));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* 清理失败不影响测试结论 */ }
    }

    [Fact]
    public async Task Record_RejectsLocalAndNonWebUrls()
    {
        Assert.False(await _history.RecordAsync("file:///C:/Apage/webui/index.html", "新标签页", DateTimeOffset.UtcNow));
        Assert.False(await _history.RecordAsync("about:blank", "空白", DateTimeOffset.UtcNow));
        Assert.False(await _history.RecordAsync("javascript:alert(1)", "脚本", DateTimeOffset.UtcNow));
        Assert.False(await _history.RecordAsync(null, null, DateTimeOffset.UtcNow));

        Assert.Empty(await _history.QueryRecentAsync());
    }

    [Fact]
    public async Task Record_StoresNormalizedUrlAndTitle()
    {
        var at = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.FromHours(8));
        Assert.True(await _history.RecordAsync("https://Example.com/A#frag", "示例", at));

        var entries = await _history.QueryRecentAsync();
        var entry = Assert.Single(entries);
        Assert.Equal("https://example.com/A", entry.Url);
        Assert.Equal("示例", entry.Title);
        Assert.Equal(at.ToUniversalTime(), entry.VisitedAt);
    }

    [Fact]
    public async Task QueryRecent_OrdersNewestFirstAndHonorsLimit()
    {
        await _history.RecordAsync("https://example.com/1", "一", DateTimeOffset.UtcNow.AddMinutes(-3));
        await _history.RecordAsync("https://example.com/2", "二", DateTimeOffset.UtcNow.AddMinutes(-2));
        await _history.RecordAsync("https://example.com/3", "三", DateTimeOffset.UtcNow.AddMinutes(-1));

        var recent = await _history.QueryRecentAsync(2);
        Assert.Equal(new[] { "三", "二" }, recent.Select(e => e.Title).ToArray());
    }

    [Fact]
    public async Task Search_MatchesUrlAndTitleAndEscapesWildcards()
    {
        await _history.RecordAsync("https://example.com/docs", "文档", DateTimeOffset.UtcNow);
        await _history.RecordAsync("https://other.test/a%20b", "百分号", DateTimeOffset.UtcNow);

        Assert.Single(await _history.SearchAsync("DOCS"));
        Assert.Single(await _history.SearchAsync("文档"));
        Assert.Single(await _history.SearchAsync("%20"));   // % 按字面量匹配，不能当通配符
        Assert.Empty(await _history.SearchAsync("_"));      // _ 同样按字面量匹配
    }

    [Fact]
    public async Task Prune_RemovesOnlyOlderThanCutoff()
    {
        var now = DateTimeOffset.UtcNow;
        await _history.RecordAsync("https://example.com/old", "旧", now.AddDays(-120));
        await _history.RecordAsync("https://example.com/new", "新", now);

        var removed = await _history.PruneAsync(now.AddDays(-90));

        Assert.Equal(1, removed);
        var left = Assert.Single(await _history.QueryRecentAsync());
        Assert.Equal("新", left.Title);
    }

    [Fact]
    public async Task Clear_RemovesEverything()
    {
        await _history.RecordAsync("https://example.com/1", "一", DateTimeOffset.UtcNow);
        await _history.RecordAsync("https://example.com/2", "二", DateTimeOffset.UtcNow);

        Assert.Equal(2, await _history.ClearAsync());
        Assert.Empty(await _history.QueryRecentAsync());
    }

    [Fact]
    public async Task ConcurrentRecords_AllPersist()
    {
        var tasks = Enumerable.Range(0, 20)
            .Select(i => _history.RecordAsync($"https://example.com/p/{i}", "并发" + i, DateTimeOffset.UtcNow))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(20, (await _history.QueryRecentAsync(100)).Count);
    }
}
