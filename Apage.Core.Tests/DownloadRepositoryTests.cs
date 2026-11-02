using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Apage.Core.Data;
using Xunit;

namespace Apage.Core.Tests;

/// <summary>
/// B2 下载仓储：开始/进度/终态、异常退出残留处理与清理。
/// </summary>
public sealed class DownloadRepositoryTests : IDisposable
{
    private readonly string _dir;
    private readonly DownloadRepository _downloads;

    public DownloadRepositoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ApageTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        SqliteProvider.EnsureInitialized();
        _downloads = new DownloadRepository(new SqliteDatabase(Path.Combine(_dir, "apage.db")));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* 清理失败不影响测试结论 */ }
    }

    [Fact]
    public async Task Start_RejectsNonWebUrl()
    {
        Assert.False(await _downloads.StartAsync("id-1", "blob:https://example.com/abc", "C:\\x", 10));
        Assert.Empty(await _downloads.ListRecentAsync());
    }

    [Fact]
    public async Task Start_RecordsInProgressRow()
    {
        Assert.True(await _downloads.StartAsync("id-1", "https://example.com/file.zip#frag", "C:\\Downloads\\file.zip", 1024));

        var entry = await _downloads.FindAsync("id-1");
        Assert.NotNull(entry);
        Assert.Equal("https://example.com/file.zip", entry!.Url);
        Assert.Equal(DownloadStates.InProgress, entry.State);
        Assert.Equal(1024, entry.TotalBytes);
        Assert.Equal(0, entry.ReceivedBytes);
        Assert.True(entry.IsActive);
    }

    [Fact]
    public async Task Progress_UpdatesBytesWithoutChangingState()
    {
        await _downloads.StartAsync("id-1", "https://example.com/f", "C:\\f", 2048);

        await _downloads.UpdateProgressAsync("id-1", 512, 2048);
        await _downloads.UpdateProgressAsync("id-1", 1536, 0); // total 未知时保留旧值

        var entry = await _downloads.FindAsync("id-1");
        Assert.Equal(1536, entry!.ReceivedBytes);
        Assert.Equal(2048, entry.TotalBytes);
        Assert.Equal(DownloadStates.InProgress, entry.State);
    }

    [Fact]
    public async Task Completed_SetsFinalStateAndFullBytes()
    {
        await _downloads.StartAsync("id-1", "https://example.com/f", "C:\\f", 4096);
        await _downloads.UpdateProgressAsync("id-1", 1000, 4096);

        await _downloads.MarkCompletedAsync("id-1");

        var entry = await _downloads.FindAsync("id-1");
        Assert.Equal(DownloadStates.Completed, entry!.State);
        Assert.Equal(4096, entry.ReceivedBytes);
        Assert.NotNull(entry.FinishedAt);
        Assert.Null(entry.Error);
        Assert.False(entry.IsActive);
    }

    [Fact]
    public async Task Canceled_AndFailed_RecordReason()
    {
        await _downloads.StartAsync("id-c", "https://example.com/a", "C:\\a", 10);
        await _downloads.MarkCanceledAsync("id-c");
        Assert.Equal(DownloadStates.Canceled, (await _downloads.FindAsync("id-c"))!.State);

        await _downloads.StartAsync("id-f", "https://example.com/b", "C:\\b", 10);
        await _downloads.MarkFailedAsync("id-f", "SERVER_UNREACHABLE");
        var failed = await _downloads.FindAsync("id-f");
        Assert.Equal(DownloadStates.Failed, failed!.State);
        Assert.Equal("SERVER_UNREACHABLE", failed.Error);
    }

    [Fact]
    public async Task MarkOrphaned_OnlyTouchesInProgressRows()
    {
        await _downloads.StartAsync("id-live", "https://example.com/live", "C:\\live", 10);
        await _downloads.StartAsync("id-done", "https://example.com/done", "C:\\done", 10);
        await _downloads.MarkCompletedAsync("id-done");

        // 模拟上次进程被强杀后重启：同一数据库上的新实例
        var reopened = new DownloadRepository(new SqliteDatabase(Path.Combine(_dir, "apage.db")));
        var marked = await reopened.MarkOrphanedAsync(DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.Equal(1, marked);
        var live = await reopened.FindAsync("id-live");
        Assert.Equal(DownloadStates.Failed, live!.State);
        Assert.Equal("应用退出时下载未完成", live.Error);
        Assert.Equal(DownloadStates.Completed, (await reopened.FindAsync("id-done"))!.State);
    }

    [Fact]
    public async Task MarkOrphanedById_OnlyTouchesRequestedRow()
    {
        await _downloads.StartAsync("id-a", "https://example.com/a", "C:\\a", 10);
        await _downloads.StartAsync("id-b", "https://example.com/b", "C:\\b", 10);

        var marked = await _downloads.MarkOrphanedAsync("id-a");

        Assert.True(marked);
        Assert.Equal(DownloadStates.Failed, (await _downloads.FindAsync("id-a"))!.State);
        Assert.Equal(DownloadStates.InProgress, (await _downloads.FindAsync("id-b"))!.State);
    }

    [Fact]
    public async Task MarkOrphanedByCutoff_DoesNotTouchDownloadsStartedAfterProcessStart()
    {
        var processStartedAt = DateTimeOffset.UtcNow;
        await _downloads.StartAsync("id-new", "https://example.com/new", "C:\\new", 10);

        var marked = await _downloads.MarkOrphanedAsync(processStartedAt);

        Assert.Equal(0, marked);
        Assert.Equal(DownloadStates.InProgress, (await _downloads.FindAsync("id-new"))!.State);
    }

    [Fact]
    public async Task TerminalState_CannotBeOverwrittenByLateEvents()
    {
        await _downloads.StartAsync("id-final", "https://example.com/f", "C:\\f", 100);
        await _downloads.MarkCompletedAsync("id-final");

        await _downloads.MarkCanceledAsync("id-final");
        await _downloads.UpdateProgressAsync("id-final", 1, 100);

        var entry = await _downloads.FindAsync("id-final");
        Assert.Equal(DownloadStates.Completed, entry!.State);
        Assert.Equal(100, entry.ReceivedBytes);
        Assert.NotNull(entry.FinishedAt);
    }

    [Fact]
    public async Task ClearFinished_KeepsActiveRows()
    {
        await _downloads.StartAsync("id-live", "https://example.com/live", "C:\\live", 10);
        await _downloads.StartAsync("id-done", "https://example.com/done", "C:\\done", 10);
        await _downloads.MarkCompletedAsync("id-done");

        var removed = await _downloads.ClearFinishedAsync();

        Assert.Equal(1, removed);
        var rows = await _downloads.ListRecentAsync();
        Assert.Single(rows);
        Assert.Equal("id-live", rows[0].Id);
    }

    [Fact]
    public async Task ListRecent_OrdersByStartedAtDescending()
    {
        await _downloads.StartAsync("id-1", "https://example.com/1", "C:\\1", 1);
        await Task.Delay(5);
        await _downloads.StartAsync("id-2", "https://example.com/2", "C:\\2", 2);

        var rows = await _downloads.ListRecentAsync();
        Assert.Equal(new[] { "id-2", "id-1" }, rows.Select(r => r.Id).ToArray());
    }
}
