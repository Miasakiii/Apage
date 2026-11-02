using Apage.Core.Services;
using Xunit;

namespace Apage.Core.Tests;

/// <summary>
/// FindBarState 状态机（S3 页内查找）：开关生命周期、查询变化、命中计数收敛、
/// 状态文案（空 / 待回报 / 无结果 / 当前-总数）与按键语义。纯逻辑，无 IO 无 WPF。
/// </summary>
public sealed class FindBarStateTests
{
    private static FindBarState OpenedWith(string? query = null)
    {
        var state = new FindBarState();
        state.Open();
        if (query != null)
            state.SetQuery(query);
        return state;
    }

    [Fact]
    public void NewState_IsClosedAndSilent()
    {
        var state = new FindBarState();

        Assert.False(state.IsOpen);
        Assert.False(state.HasQuery);
        Assert.Equal(string.Empty, state.Query);
        Assert.Equal(string.Empty, state.StatusText);
    }

    [Fact]
    public void Open_EmptyQuery_ShowsNothing()
    {
        var state = OpenedWith();

        Assert.True(state.IsOpen);
        Assert.Equal(string.Empty, state.StatusText);
    }

    [Fact]
    public void SetQuery_NonEmpty_RequestsSearch()
    {
        var state = OpenedWith();

        var intent = state.SetQuery("apage");

        Assert.Equal(FindBarIntent.Search, intent);
        Assert.Equal("apage", state.Query);
        Assert.True(state.HasQuery);
    }

    [Fact]
    public void SetQuery_WhitespaceOnly_IsStillASearch()
    {
        // 空格是合法查找词（有些页面用空格排版），不做 Trim
        var state = OpenedWith();

        Assert.Equal(FindBarIntent.Search, state.SetQuery(" "));
        Assert.Equal(" ", state.Query);
    }

    [Fact]
    public void SetQuery_Clear_StopsSessionAndResetsCounts()
    {
        var state = OpenedWith("apage");
        state.ApplyResult(5, 2);

        var intent = state.SetQuery(string.Empty);

        Assert.Equal(FindBarIntent.None, intent);
        Assert.False(state.HasQuery);
        Assert.Equal(0, state.MatchCount);
        Assert.Equal(0, state.ActiveMatchIndex);
        Assert.Equal(string.Empty, state.StatusText);
    }

    [Fact]
    public void SetQuery_Closed_IsIgnored()
    {
        var state = new FindBarState();

        Assert.Equal(FindBarIntent.None, state.SetQuery("apage"));
        Assert.False(state.HasQuery);
    }

    [Fact]
    public void StatusText_BeforeAnyResult_StaysEmpty()
    {
        // 刚敲下第一键、内核还没回报命中数：不能闪出「无结果」
        var state = OpenedWith("a");

        Assert.Equal(string.Empty, state.StatusText);
    }

    [Fact]
    public void StatusText_ZeroMatches_ReportsNoResults()
    {
        var state = OpenedWith("zzz");

        state.ApplyResult(0, 0);

        Assert.Equal(FindBarState.NoResultsText, state.StatusText);
        Assert.Equal(0, state.ActiveMatchIndex);
    }

    [Fact]
    public void StatusText_Matches_FormatsActiveOverTotal()
    {
        var state = OpenedWith("apage");

        state.ApplyResult(7, 3);

        Assert.Equal("3 / 7", state.StatusText);
        Assert.Equal(7, state.MatchCount);
        Assert.Equal(3, state.ActiveMatchIndex);
    }

    [Fact]
    public void StatusText_UnknownActiveIndex_ShowsTotalOnly()
    {
        // 内核在会话刚建立时可能给 -1：只报总数，不假装停在第一条
        var state = OpenedWith("apage");

        state.ApplyResult(4, -1);

        Assert.Equal("4 项", state.StatusText);
        Assert.Equal(0, state.ActiveMatchIndex);
    }

    [Fact]
    public void ApplyResult_ClampsOutOfRangeIndex()
    {
        var state = OpenedWith("apage");

        state.ApplyResult(3, 9);

        Assert.Equal(3, state.ActiveMatchIndex);
        Assert.Equal("3 / 3", state.StatusText);
    }

    [Fact]
    public void ApplyResult_NegativeCount_TreatedAsZero()
    {
        var state = OpenedWith("apage");

        state.ApplyResult(-2, 1);

        Assert.Equal(0, state.MatchCount);
        Assert.Equal(FindBarState.NoResultsText, state.StatusText);
    }

    [Fact]
    public void ApplyResult_AfterNewQuery_DropsStaleCounts()
    {
        var state = OpenedWith("apage");
        state.ApplyResult(12, 4);

        state.SetQuery("apagex"); // 新查询：旧计数不再代表当前输入

        Assert.Equal(string.Empty, state.StatusText);

        state.ApplyResult(2, 1);
        Assert.Equal("1 / 2", state.StatusText);
    }

    [Fact]
    public void ApplyResult_AfterClose_DoesNotResurrectState()
    {
        // 关闭后内核仍可能补发一次命中事件，不得重新显示计数
        var state = OpenedWith("apage");
        state.Close();

        state.ApplyResult(8, 1);

        Assert.Equal(0, state.MatchCount);
        Assert.Equal(string.Empty, state.StatusText);
    }

    [Fact]
    public void Close_ResetsQuerySoReopenStartsFresh()
    {
        var state = OpenedWith("apage");
        state.ApplyResult(3, 1);

        state.Close();
        state.Open();

        Assert.True(state.IsOpen);
        Assert.False(state.HasQuery);
        Assert.Equal(string.Empty, state.StatusText);
    }

    [Fact]
    public void MapKey_Enter_GoesToNextMatch()
    {
        var state = OpenedWith("apage");

        Assert.Equal(FindBarIntent.Next, state.MapKey(FindKey.Enter, shift: false));
    }

    [Fact]
    public void MapKey_ShiftEnter_GoesToPreviousMatch()
    {
        var state = OpenedWith("apage");

        Assert.Equal(FindBarIntent.Previous, state.MapKey(FindKey.Enter, shift: true));
    }

    [Fact]
    public void MapKey_EnterWithoutQuery_DoesNothing()
    {
        var state = OpenedWith();

        Assert.Equal(FindBarIntent.None, state.MapKey(FindKey.Enter, shift: false));
        Assert.Equal(FindBarIntent.None, state.MapKey(FindKey.Enter, shift: true));
    }

    [Fact]
    public void MapKey_Escape_ClosesOnlyWhenOpen()
    {
        var open = OpenedWith("apage");
        var closed = new FindBarState();

        Assert.Equal(FindBarIntent.Close, open.MapKey(FindKey.Escape, shift: false));
        Assert.Equal(FindBarIntent.None, closed.MapKey(FindKey.Escape, shift: false));
    }

    [Fact]
    public void MapKey_OtherKeys_AreIgnored()
    {
        var state = OpenedWith("apage");

        Assert.Equal(FindBarIntent.None, state.MapKey(FindKey.Other, shift: false));
        Assert.Equal(FindBarIntent.None, state.MapKey(FindKey.Other, shift: true));
    }
}
