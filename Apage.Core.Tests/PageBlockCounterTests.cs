using System;
using System.Threading;
using Apage.Core.Services;
using Xunit;

namespace Apage.Core.Tests;

/// <summary>
/// PageBlockCounter：页级拦截计数（R6）。
/// 把守两条：① 计数随 Reset 归零且事件收敛（徽章不残留旧页面计数）；
/// ② BadgeText 只在计数 > 0 时非空（没有拦截就不显示徽章——数字只能真实或没有）。
/// </summary>
public sealed class PageBlockCounterTests
{
    [Fact]
    public void Initial_IsZeroWithEmptyBadge()
    {
        var counter = new PageBlockCounter();

        Assert.Equal(0, counter.Count);
        Assert.Equal(string.Empty, counter.BadgeText);
    }

    [Fact]
    public void Increment_AccumulatesAndUpdatesBadge()
    {
        var counter = new PageBlockCounter();

        counter.Increment();
        counter.Increment(2);

        Assert.Equal(3, counter.Count);
        Assert.Equal("3", counter.BadgeText);
    }

    [Fact]
    public void Increment_NonPositive_Ignored()
    {
        var counter = new PageBlockCounter();
        counter.Increment(5);

        counter.Increment(0);
        counter.Increment(-3);

        Assert.Equal(5, counter.Count);
    }

    [Fact]
    public void Reset_ClearsCountAndBadge()
    {
        var counter = new PageBlockCounter();
        counter.Increment(7);

        counter.Reset();

        Assert.Equal(0, counter.Count);
        Assert.Equal(string.Empty, counter.BadgeText);
    }

    [Fact]
    public void Reset_OnZero_StillFiresChanged()
    {
        // 徽章状态收敛：即使已为 0，Reset 也必须通知宿主（防上一页残留）
        var counter = new PageBlockCounter();
        var fired = 0;
        counter.Changed += (s, e) => Interlocked.Increment(ref fired);

        counter.Reset();

        Assert.True(fired > 0);
    }

    [Fact]
    public void Increment_FiresChanged()
    {
        var counter = new PageBlockCounter();
        var fired = 0;
        counter.Changed += (s, e) => Interlocked.Increment(ref fired);

        counter.Increment();
        counter.Increment();

        Assert.True(fired >= 2);
    }

    [Fact]
    public void Increment_Concurrent_TotalIsExact()
    {
        var counter = new PageBlockCounter();
        const int threads = 4;
        const int perThread = 500;

        var workers = new System.Threading.Tasks.Task[threads];
        for (int t = 0; t < threads; t++)
        {
            workers[t] = System.Threading.Tasks.Task.Run(() =>
            {
                for (int i = 0; i < perThread; i++)
                    counter.Increment();
            });
        }
        System.Threading.Tasks.Task.WaitAll(workers);

        Assert.Equal(threads * perThread, counter.Count);
    }
}
