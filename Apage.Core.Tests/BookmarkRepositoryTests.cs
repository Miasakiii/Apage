using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Apage.Core.Data;
using Xunit;

namespace Apage.Core.Tests;

/// <summary>
/// B2 书签仓储：URL 归一化去重、标题更新、删除、批量导入与并发写入。
/// </summary>
public sealed class BookmarkRepositoryTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteDatabase _database;
    private readonly BookmarkRepository _bookmarks;

    public BookmarkRepositoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ApageTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        SqliteProvider.EnsureInitialized();
        _database = new SqliteDatabase(Path.Combine(_dir, "apage.db"));
        _bookmarks = new BookmarkRepository(_database);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* 清理失败不影响测试结论 */ }
    }

    [Theory]
    [InlineData("https://Example.COM/Path", "https://example.com/Path")]
    [InlineData("https://example.com:443/a#frag", "https://example.com/a")]
    [InlineData("http://example.com:80", "http://example.com/")]
    [InlineData("https://例子.测试", "https://xn--fsqu00a.xn--0zwm56d/")]
    public void UrlKey_NormalizesSchemeHostPortAndFragment(string input, string expected)
    {
        Assert.True(UrlKey.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("file:///C:/Apage/webui/index.html")]
    [InlineData("about:blank")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hi")]
    [InlineData("not a url")]
    [InlineData("ftp://example.com/file")]
    public void UrlKey_RejectsNonWebOrMalformed(string? input)
    {
        Assert.False(UrlKey.TryNormalize(input, out var normalized));
        Assert.Equal(string.Empty, normalized);
    }

    [Fact]
    public async Task Save_SameUrlDifferentCase_KeepsSingleRowAndUpdatesTitle()
    {
        var first = await _bookmarks.SaveAsync("https://example.com/Page", "标题一");
        var second = await _bookmarks.SaveAsync("https://EXAMPLE.com/Page#top", "标题二");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("标题二", second.Title);

        var all = await _bookmarks.ListAsync();
        Assert.Single(all);
        Assert.Equal("https://example.com/Page", all[0].Url);
    }

    [Fact]
    public async Task Save_EmptyTitle_KeepsPreviousTitle()
    {
        await _bookmarks.SaveAsync("https://example.com/a", "保留我");
        var updated = await _bookmarks.SaveAsync("https://example.com/a", "   ");

        Assert.Equal("保留我", updated.Title);
    }

    [Fact]
    public async Task Save_RejectsNonWebUrl()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _bookmarks.SaveAsync("file:///C:/secret.txt", "本地"));
    }

    [Fact]
    public async Task Contains_FollowsNormalization()
    {
        await _bookmarks.SaveAsync("https://example.com/x?q=1", "查询");

        Assert.True(await _bookmarks.ContainsAsync("https://EXAMPLE.com/x?q=1#frag"));
        Assert.False(await _bookmarks.ContainsAsync("https://example.com/x?q=2"));
        Assert.False(await _bookmarks.ContainsAsync("file:///C:/x"));
    }

    [Fact]
    public async Task Remove_ByIdAndByUrl_ReportsHit()
    {
        var entry = await _bookmarks.SaveAsync("https://example.com/remove", "删除我");

        Assert.True(await _bookmarks.RemoveAsync(entry.Id));
        Assert.False(await _bookmarks.RemoveAsync(entry.Id));

        await _bookmarks.SaveAsync("https://example.com/remove2", "再删");
        Assert.True(await _bookmarks.RemoveByUrlAsync("https://example.com/remove2"));
        Assert.Empty(await _bookmarks.ListAsync());
    }

    [Fact]
    public async Task List_OrdersByCreatedAtDescending()
    {
        await _bookmarks.SaveAsync("https://example.com/1", "一");
        await Task.Delay(5);
        await _bookmarks.SaveAsync("https://example.com/2", "二");
        await Task.Delay(5);
        await _bookmarks.SaveAsync("https://example.com/3", "三");

        var all = await _bookmarks.ListAsync();
        Assert.Equal(new[] { "三", "二", "一" }, all.Select(b => b.Title).ToArray());
    }

    [Fact]
    public async Task Import_AddsUpdatesAndSkipsDuplicateInsideBatch()
    {
        await _bookmarks.SaveAsync("https://example.com/existing", "旧标题");

        var outcome = await _bookmarks.ImportAsync(new[]
        {
            new ImportedBookmark("https://example.com/existing", "新标题"),
            new ImportedBookmark("https://example.com/new", "新增"),
            new ImportedBookmark("https://EXAMPLE.com/new#frag", "同批重复"),
            new ImportedBookmark("javascript:void(0)", "脚本"),
            new ImportedBookmark("file:///C:/x", "本地"),
        });

        Assert.Equal(1, outcome.Added);
        Assert.Equal(1, outcome.Updated);
        Assert.Equal(1, outcome.SkippedDuplicate);
        Assert.Equal(2, outcome.SkippedInvalid);

        var all = await _bookmarks.ListAsync();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, b => b.Url == "https://example.com/existing" && b.Title == "新标题");
    }

    [Fact]
    public async Task ConcurrentSaves_ProduceExactRowCount()
    {
        var tasks = Enumerable.Range(0, 24)
            .Select(i => _bookmarks.SaveAsync($"https://example.com/item/{i}", "并发" + i))
            .ToArray();

        await Task.WhenAll(tasks);

        var all = await _bookmarks.ListAsync();
        Assert.Equal(24, all.Count);
        Assert.Equal(24, all.Select(b => b.Url).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task ConcurrentSaves_ToSameUrl_DoNotThrow()
    {
        var tasks = Enumerable.Range(0, 12)
            .Select(i => _bookmarks.SaveAsync("https://example.com/same", "标题" + i))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal("https://example.com/same", r.Url));
        Assert.Single(await _bookmarks.ListAsync());
    }
}
