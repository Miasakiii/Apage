#nullable enable
using System;

namespace Apage.Core.Data;

/// <summary>一条书签。URL 已归一化；同一 <see cref="Url"/> 只保留一行。</summary>
public sealed class BookmarkEntry
{
    public long Id { get; set; }

    public string Url { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>一条历史访问记录（每次成功顶层导航一行）。</summary>
public sealed class HistoryEntry
{
    public long Id { get; set; }

    public string Url { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public DateTimeOffset VisitedAt { get; set; }
}

/// <summary>下载状态取值，与 schema 的 CHECK 约束一一对应。</summary>
public static class DownloadStates
{
    public const string InProgress = "in_progress";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Canceled = "canceled";
}

/// <summary>一条下载记录。<see cref="Id"/> 由宿主生成（WebView2 下载对象本身无 id）。</summary>
public sealed class DownloadEntry
{
    public string Id { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string TargetPath { get; set; } = string.Empty;

    public string State { get; set; } = DownloadStates.InProgress;

    public long ReceivedBytes { get; set; }

    public long TotalBytes { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public string? Error { get; set; }

    /// <summary>是否仍可取消（仅进行中）。</summary>
    public bool IsActive => string.Equals(State, DownloadStates.InProgress, StringComparison.Ordinal);
}

/// <summary>书签导入的批量写入结果。</summary>
public sealed class BookmarkImportOutcome
{
    public BookmarkImportOutcome(int added, int updated, int skippedInvalid, int skippedDuplicate)
    {
        Added = added;
        Updated = updated;
        SkippedInvalid = skippedInvalid;
        SkippedDuplicate = skippedDuplicate;
    }

    public int Added { get; }

    public int Updated { get; }

    public int SkippedInvalid { get; }

    public int SkippedDuplicate { get; }
}
