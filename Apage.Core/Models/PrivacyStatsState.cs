#nullable enable
using System;

namespace Apage.Core.Models;

/// <summary>
/// 隐私拦截统计状态（docs/design/2026-10-23-privacy-stats-ntp.md §3.1）。
/// 按周统计：周一 00:00 本地时间为一周边界（与「本周」中文语义一致；不用 UTC，避免用户感知错位）。
/// 纯数据 + 纯函数（<see cref="ComputeWeekStart"/> / <see cref="RolledOver"/>），无 IO，便于单测（决策 #18）。
/// </summary>
public sealed record PrivacyStatsState
{
    /// <summary>本周一 00:00（本地时间）；跨周边界由 <see cref="RolledOver"/> 处理。默认值为 0001-01-01（尚未归位）。</summary>
    public DateTime WeekStart { get; init; }

    /// <summary>广告规则拦截次数（本周累计）。</summary>
    public long AdsBlocked { get; init; }

    /// <summary>第三方 Cookie 剥离次数（本周累计，即新标签页「已拦截追踪」数字）。</summary>
    public long TrackersBlocked { get; init; }

    /// <summary>
    /// 计算给定时刻所在自然周的「周一 00:00」本地时间。
    /// 口径：周一为一周之始；直接用入参的日期分量计算，返回 <see cref="DateTimeKind.Unspecified"/>
    /// 的本地墙上时间（避免 JSON 往返因时区偏移换算而错位）。
    /// </summary>
    public static DateTime ComputeWeekStart(DateTime now)
    {
        var date = now.Date;
        // DayOfWeek：Sunday=0 … Saturday=6。换算为「距周一的天数」：Monday→0 … Sunday→6。
        int daysSinceMonday = ((int)date.DayOfWeek + 6) % 7;
        return DateTime.SpecifyKind(date.AddDays(-daysSinceMonday), DateTimeKind.Unspecified);
    }

    /// <summary>
    /// 跨周滚存（纯函数，不修改原实例）：若 <see cref="WeekStart"/> 与 <see cref="ComputeWeekStart"/> 不同，
    /// 则返回计数归零、<see cref="WeekStart"/> 更新为本周一的新实例；否则原样返回。
    /// </summary>
    public PrivacyStatsState RolledOver(DateTime now)
    {
        var weekStart = ComputeWeekStart(now);
        if (WeekStart == weekStart)
            return this;
        return new PrivacyStatsState { WeekStart = weekStart, AdsBlocked = 0, TrackersBlocked = 0 };
    }
}
