using System;
using Apage.Core.Models;
using Xunit;

namespace Apage.Core.Tests;

/// <summary>
/// PrivacyStatsState 周口径（docs/design/2026-10-23-privacy-stats-ntp.md §3.1 / §五）：
/// 周一 00:00 本地时间为一周边界；覆盖周日 / 周一 / 周中 / 周六 / 跨周日边界，
/// 以及 RolledOver 的归零与保留。纯函数，无 IO。
/// 固定用 2026-10-19（周一）所在周做断言：Mon 10-19 … Sun 10-25，次周 Mon 10-26。
/// </summary>
public sealed class PrivacyStatsStateTests
{
    [Fact]
    public void ComputeWeekStart_MidWeek_Wednesday_ReturnsMonday()
    {
        // 2026-10-21 是周三 → 当周周一 2026-10-19
        var monday = PrivacyStatsState.ComputeWeekStart(new DateTime(2026, 10, 21, 15, 30, 0));

        Assert.Equal(new DateTime(2026, 10, 19), monday);
        Assert.Equal(TimeSpan.Zero, monday.TimeOfDay); // 必须落在 00:00
    }

    [Fact]
    public void ComputeWeekStart_MondayEarly_ReturnsSameDay()
    {
        // 周一 00:00 与 23:59:59 都归属本周一
        Assert.Equal(new DateTime(2026, 10, 19),
            PrivacyStatsState.ComputeWeekStart(new DateTime(2026, 10, 19, 0, 0, 0)));
        Assert.Equal(new DateTime(2026, 10, 19),
            PrivacyStatsState.ComputeWeekStart(new DateTime(2026, 10, 19, 23, 59, 59)));
    }

    [Fact]
    public void ComputeWeekStart_Saturday_ReturnsSameWeekMonday()
    {
        // 2026-10-24 是周六 → 仍属 10-19 当周
        Assert.Equal(new DateTime(2026, 10, 19),
            PrivacyStatsState.ComputeWeekStart(new DateTime(2026, 10, 24, 12, 0, 0)));
    }

    [Fact]
    public void ComputeWeekStart_Sunday_ReturnsPreviousMonday()
    {
        // 周日是一周之末：2026-10-25（周日）→ 周一 2026-10-19（距周一 6 天）
        Assert.Equal(new DateTime(2026, 10, 19),
            PrivacyStatsState.ComputeWeekStart(new DateTime(2026, 10, 25, 23, 59, 59)));
    }

    [Fact]
    public void ComputeWeekStart_SundayToMonday_IsWeekBoundary()
    {
        // 周日 23:59:59 与下周一 00:00 分属两周（跨周边界）
        var sundayWeek = PrivacyStatsState.ComputeWeekStart(new DateTime(2026, 10, 25, 23, 59, 59));
        var mondayWeek = PrivacyStatsState.ComputeWeekStart(new DateTime(2026, 10, 26, 0, 0, 0));

        Assert.Equal(new DateTime(2026, 10, 19), sundayWeek);
        Assert.Equal(new DateTime(2026, 10, 26), mondayWeek);
        Assert.NotEqual(sundayWeek, mondayWeek);
    }

    [Fact]
    public void RolledOver_SameWeek_PreservesCounts_ReturnsSameInstance()
    {
        var state = new PrivacyStatsState { WeekStart = new DateTime(2026, 10, 19), AdsBlocked = 5, TrackersBlocked = 3 };

        // 同周（周三）滚存：计数原样保留，返回同一实例（纯函数，无副作用）
        var rolled = state.RolledOver(new DateTime(2026, 10, 21, 9, 0, 0));

        Assert.Same(state, rolled);
        Assert.Equal(5, rolled.AdsBlocked);
        Assert.Equal(3, rolled.TrackersBlocked);
        Assert.Equal(new DateTime(2026, 10, 19), rolled.WeekStart);
    }

    [Fact]
    public void RolledOver_CrossWeek_ZeroesCounts_UpdatesWeekStart()
    {
        var state = new PrivacyStatsState { WeekStart = new DateTime(2026, 10, 19), AdsBlocked = 42, TrackersBlocked = 7 };

        // 次周（2026-10-27 周二）滚存：计数归零，WeekStart 更新为次周周一 10-26
        var rolled = state.RolledOver(new DateTime(2026, 10, 27, 9, 0, 0));

        Assert.NotSame(state, rolled);
        Assert.Equal(0, rolled.AdsBlocked);
        Assert.Equal(0, rolled.TrackersBlocked);
        Assert.Equal(new DateTime(2026, 10, 26), rolled.WeekStart);
    }

    [Fact]
    public void RolledOver_UninitializedState_AdoptsCurrentWeek()
    {
        // 全新状态（WeekStart 为默认值）滚存后归位到当周，计数仍为 0
        var rolled = new PrivacyStatsState().RolledOver(new DateTime(2026, 10, 21, 9, 0, 0));

        Assert.Equal(new DateTime(2026, 10, 19), rolled.WeekStart);
        Assert.Equal(0, rolled.AdsBlocked);
        Assert.Equal(0, rolled.TrackersBlocked);
    }
}
