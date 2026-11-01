using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Apage.Core.Models;
using Apage.Core.Services;
using Xunit;

namespace Apage.Core.Tests;

/// <summary>
/// PrivacyStatsService 持久化（docs/design/2026-10-23-privacy-stats-ntp.md §3.2 / §五）：
/// 损坏回落、原子写往返、跨周加载自动归零、并发自增。用临时目录隔离，测后清理。
/// </summary>
public sealed class PrivacyStatsServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public PrivacyStatsServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ApageTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "privacy-stats.json");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* 清理失败不影响测试结论 */ }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsEmptyPath(string? path)
    {
        Assert.Throws<ArgumentException>(() => new PrivacyStatsService(path!));
    }

    [Fact]
    public async Task LoadAsync_MissingFile_FreshState_DoesNotWrite()
    {
        var svc = new PrivacyStatsService(_file);

        await svc.LoadAsync();

        Assert.Equal(0L, svc.Snapshot.Ads);
        Assert.Equal(0L, svc.Snapshot.Trackers);
        Assert.False(File.Exists(_file)); // 加载缺失文件不应写盘
    }

    [Fact]
    public async Task LoadAsync_CorruptedJson_FallsBackToFresh()
    {
        await File.WriteAllTextAsync(_file, "{ this is not valid json ]");

        var svc = new PrivacyStatsService(_file);
        await svc.LoadAsync(); // 不得抛异常

        Assert.Equal(0L, svc.Snapshot.Ads);
        Assert.Equal(0L, svc.Snapshot.Trackers);
    }

    [Fact]
    public async Task Save_Then_Load_RoundTrips_SameWeek()
    {
        var svc = new PrivacyStatsService(_file);
        svc.RecordAdBlocked(3);
        svc.RecordTrackerBlocked(2);

        await svc.SaveAsync();
        Assert.True(File.Exists(_file));

        var reloaded = new PrivacyStatsService(_file);
        await reloaded.LoadAsync();
        Assert.Equal(3L, reloaded.Snapshot.Ads);
        Assert.Equal(2L, reloaded.Snapshot.Trackers);
    }

    [Fact]
    public async Task LoadAsync_CrossWeek_ZeroesCounts()
    {
        // 预置一份「上周」的统计（非零），加载后跨周应自动归零
        var lastWeek = PrivacyStatsState.ComputeWeekStart(DateTime.Now).AddDays(-7);
        var stale = new PrivacyStatsState { WeekStart = lastWeek, AdsBlocked = 42, TrackersBlocked = 7 };
        await File.WriteAllTextAsync(_file, JsonSerializer.Serialize(stale));

        var svc = new PrivacyStatsService(_file);
        await svc.LoadAsync();

        Assert.Equal(0L, svc.Snapshot.Ads);
        Assert.Equal(0L, svc.Snapshot.Trackers);
    }

    [Fact]
    public async Task SaveAsync_CreatesFile_AndLeavesNoTempResidue()
    {
        var svc = new PrivacyStatsService(_file);

        await svc.SaveAsync();

        Assert.True(File.Exists(_file));
        Assert.False(File.Exists(_file + ".tmp")); // 原子写后不应残留 .tmp
    }

    [Fact]
    public void RecordAdBlocked_Increments()
    {
        var svc = new PrivacyStatsService(_file);

        svc.RecordAdBlocked();
        Assert.Equal(1L, svc.Snapshot.Ads);
        Assert.Equal(0L, svc.Snapshot.Trackers); // 两者独立

        svc.RecordAdBlocked(4);
        Assert.Equal(5L, svc.Snapshot.Ads);
    }

    [Fact]
    public void RecordTrackerBlocked_Increments()
    {
        var svc = new PrivacyStatsService(_file);

        svc.RecordTrackerBlocked();
        Assert.Equal(1L, svc.Snapshot.Trackers);
        Assert.Equal(0L, svc.Snapshot.Ads); // 两者独立

        svc.RecordTrackerBlocked(4);
        Assert.Equal(5L, svc.Snapshot.Trackers);
    }

    [Fact]
    public async Task AutoSave_FlushesDirtyCounts_WithoutExplicitSave()
    {
        using var svc = new PrivacyStatsService(_file, TimeSpan.FromMilliseconds(40));
        svc.RecordAdBlocked(3);
        svc.RecordTrackerBlocked(2);

        await WaitUntilAsync(() => File.Exists(_file) && !svc.HasPendingWrites, TimeSpan.FromSeconds(2));

        var reloaded = new PrivacyStatsService(_file);
        await reloaded.LoadAsync();
        Assert.Equal(3L, reloaded.Snapshot.Ads);
        Assert.Equal(2L, reloaded.Snapshot.Trackers);
    }

    [Fact]
    public async Task ConcurrentSaveAsync_SerializesTemporaryFile_NoResidue()
    {
        using var svc = new PrivacyStatsService(_file);
        svc.RecordAdBlocked(7);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => svc.SaveAsync()));

        Assert.True(File.Exists(_file));
        Assert.False(File.Exists(_file + ".tmp"));
        var reloaded = new PrivacyStatsService(_file);
        await reloaded.LoadAsync();
        Assert.Equal(7L, reloaded.Snapshot.Ads);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("等待自动保存超时。");
            await Task.Delay(20);
        }
    }

    [Fact]
    public void Record_Concurrent_TotalsAreExact()
    {
        // 锁内自增：并发下两个计数都应精确累加，无丢失更新
        var svc = new PrivacyStatsService(_file);
        const int n = 1000;

        Parallel.Invoke(
            () => { for (int i = 0; i < n; i++) svc.RecordAdBlocked(); },
            () => { for (int i = 0; i < n; i++) svc.RecordTrackerBlocked(); });

        Assert.Equal(n, svc.Snapshot.Ads);
        Assert.Equal(n, svc.Snapshot.Trackers);
    }
}
