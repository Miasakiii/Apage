#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apage.Core.Data;

namespace Apage.Core.Services;

/// <summary>地址栏建议项。Kind 用于展示分组与图标（bookmark / history）。</summary>
public sealed class OmniboxSuggestion
{
    public OmniboxSuggestion(string title, string url, string kind)
    {
        Title = title;
        Url = url;
        Kind = kind;
    }

    public string Title { get; }

    public string Url { get; }

    public string Kind { get; }

    public bool IsBookmark => string.Equals(Kind, "bookmark", StringComparison.Ordinal);
}

/// <summary>
/// 地址栏本地建议（S3）：书签优先、历史次之，按 URL 去重，全程本地查询——
/// 没有网络、没有搜索词上报，也不需要开启任何联网建议开关。
/// </summary>
public sealed class OmniboxSuggestionService
{
    private const int MaxQueryLength = 128;

    private readonly BookmarkRepository _bookmarks;
    private readonly HistoryRepository _history;

    public OmniboxSuggestionService(BookmarkRepository bookmarks, HistoryRepository history)
    {
        _bookmarks = bookmarks ?? throw new ArgumentNullException(nameof(bookmarks));
        _history = history ?? throw new ArgumentNullException(nameof(history));
    }

    public async Task<IReadOnlyList<OmniboxSuggestion>> QueryAsync(
        string? input,
        int limit = 8,
        CancellationToken cancellationToken = default)
    {
        var keyword = input?.Trim();
        if (string.IsNullOrEmpty(keyword))
            return Array.Empty<OmniboxSuggestion>();

        if (keyword!.Length > MaxQueryLength)
            keyword = keyword.Substring(0, MaxQueryLength);

        // 纯 scheme 前缀（h?、ht?）没有建议价值，等用户再多输入一两个字符
        var safeLimit = Math.Max(1, Math.Min(limit, 20));
        var bookmarks = await _bookmarks.SearchAsync(keyword, safeLimit, cancellationToken).ConfigureAwait(false);
        var history = await _history.SearchAsync(keyword, safeLimit * 2, cancellationToken).ConfigureAwait(false);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<OmniboxSuggestion>(safeLimit);

        foreach (var bookmark in bookmarks)
        {
            if (!seen.Add(bookmark.Url))
                continue;
            results.Add(new OmniboxSuggestion(
                string.IsNullOrWhiteSpace(bookmark.Title) ? bookmark.Url : bookmark.Title,
                bookmark.Url,
                "bookmark"));
        }

        foreach (var entry in history)
        {
            if (results.Count >= safeLimit)
                break;
            if (!seen.Add(entry.Url))
                continue;
            results.Add(new OmniboxSuggestion(
                string.IsNullOrWhiteSpace(entry.Title) ? entry.Url : entry.Title,
                entry.Url,
                "history"));
        }

        return results.Take(safeLimit).ToArray();
    }
}
