using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Apage.Core.Data;
using Apage.Core.Services;
using Xunit;

namespace Apage.Core.Tests;

/// <summary>
/// S3 地址栏本地建议：书签优先、历史补充、按 URL 去重、条数上限与空输入。
/// </summary>
public sealed class OmniboxSuggestionServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly BookmarkRepository _bookmarks;
    private readonly HistoryRepository _history;
    private readonly OmniboxSuggestionService _suggestions;

    public OmniboxSuggestionServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ApageTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        SqliteProvider.EnsureInitialized();
        var database = new SqliteDatabase(Path.Combine(_dir, "apage.db"));
        _bookmarks = new BookmarkRepository(database);
        _history = new HistoryRepository(database);
        _suggestions = new OmniboxSuggestionService(_bookmarks, _history);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* 清理失败不影响测试结论 */ }
    }

    [Fact]
    public async Task Query_PrefersBookmarksAndDeduplicatesByUrl()
    {
        await _bookmarks.SaveAsync("https://example.com/docs", "文档中心");
        await _history.RecordAsync("https://example.com/docs", "文档中心（历史）", DateTimeOffset.UtcNow);
        await _history.RecordAsync("https://example.com/docs2", "文档二", DateTimeOffset.UtcNow);

        var items = await _suggestions.QueryAsync("文档");

        Assert.Equal(2, items.Count);
        Assert.True(items[0].IsBookmark);
        Assert.Equal("https://example.com/docs", items[0].Url);
        Assert.False(items[1].IsBookmark);
        Assert.Equal("https://example.com/docs2", items[1].Url);
    }

    [Fact]
    public async Task Query_MatchesUrlFragment()
    {
        await _history.RecordAsync("https://news.example.com/a", "新闻", DateTimeOffset.UtcNow);

        var items = await _suggestions.QueryAsync("news.example");

        var item = Assert.Single(items);
        Assert.Equal("https://news.example.com/a", item.Url);
    }

    [Fact]
    public async Task Query_HonorsLimit()
    {
        for (var i = 0; i < 12; i++)
            await _bookmarks.SaveAsync($"https://example.com/p/{i}", "批量" + i);

        var items = await _suggestions.QueryAsync("批量", limit: 5);

        Assert.Equal(5, items.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Query_EmptyInput_ReturnsNothing(string? input)
    {
        await _bookmarks.SaveAsync("https://example.com/x", "任意");

        Assert.Empty(await _suggestions.QueryAsync(input));
    }

    [Fact]
    public async Task Query_WildcardCharacters_AreLiteral()
    {
        await _bookmarks.SaveAsync("https://example.com/a%20b", "百分号");
        await _bookmarks.SaveAsync("https://example.com/plain", "普通");

        var items = await _suggestions.QueryAsync("%20");

        var item = Assert.Single(items);
        Assert.Equal("https://example.com/a%20b", item.Url);
    }
}
