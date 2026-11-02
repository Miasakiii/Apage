#nullable enable
namespace Apage.Core.Services;

/// <summary>查找条要执行的动作（由状态机产出，UI 层只负责分发）。</summary>
public enum FindBarIntent
{
    /// <summary>不动作。</summary>
    None,

    /// <summary>以当前 <see cref="FindBarState.Query"/> 重开查找会话（输入变化时）。</summary>
    Search,

    /// <summary>跳到下一个命中（Enter）。</summary>
    Next,

    /// <summary>跳到上一个命中（Shift+Enter）。</summary>
    Previous,

    /// <summary>关闭查找条（Esc）。</summary>
    Close,
}

/// <summary>
/// 可供查找条消费的按键。Core 不引用 WPF（netstandard2.0），故用最小键集映射
/// <c>System.Windows.Input.Key</c>，让键位语义在纯逻辑层可测。
/// </summary>
public enum FindKey
{
    /// <summary>与查找无关的按键。</summary>
    Other,

    /// <summary>回车。</summary>
    Enter,

    /// <summary>Esc。</summary>
    Escape,
}

/// <summary>
/// 页内查找条状态机（S3）：只管「开关 / 查询 / 命中计数 / 状态文案 / 按键语义」，
/// 不持有 WebView2 也不碰 WPF，因此可完整单测。
/// <para>
/// 状态文案规则：查询为空 → 空文案；命中结果尚未回报 → 空文案（避免刚敲字就闪「无结果」）；
/// 已回报且 0 命中 → <see cref="NoResultsText"/>；有命中 → 「当前 / 总数」，若内核未给出活动索引则只显示总数。
/// </para>
/// </summary>
public sealed class FindBarState
{
    /// <summary>零命中时的提示文案。</summary>
    public const string NoResultsText = "无结果";

    private string _query = string.Empty;
    private bool _hasResult;

    /// <summary>查找条是否打开。</summary>
    public bool IsOpen { get; private set; }

    /// <summary>当前查询（空串表示尚未输入）。</summary>
    public string Query => _query;

    /// <summary>是否已输入非空查询。</summary>
    public bool HasQuery => _query.Length > 0;

    /// <summary>最近一次回报的命中总数。</summary>
    public int MatchCount { get; private set; }

    /// <summary>最近一次回报的活动命中序号（1 起；0 表示无活动命中）。</summary>
    public int ActiveMatchIndex { get; private set; }

    /// <summary>打开查找条（不改查询：再次 Ctrl+F 复用上次输入）。</summary>
    public void Open()
    {
        IsOpen = true;
    }

    /// <summary>
    /// 关闭查找条并清空查询与计数：与内核侧 StopFind 的语义一致，
    /// 重新打开是一次全新查找，不会残留上次的高亮计数。
    /// </summary>
    public void Close()
    {
        IsOpen = false;
        _query = string.Empty;
        MatchCount = 0;
        ActiveMatchIndex = 0;
        _hasResult = false;
    }

    /// <summary>
    /// 记录输入变化。返回 <see cref="FindBarIntent.Search"/> 表示需要以新查询重开查找会话；
    /// 查询被清空时返回 <see cref="FindBarIntent.None"/>（调用方应结束内核侧会话）。
    /// </summary>
    public FindBarIntent SetQuery(string? query)
    {
        if (!IsOpen)
            return FindBarIntent.None;

        var value = query ?? string.Empty;
        if (value.Length == 0)
        {
            _query = string.Empty;
            MatchCount = 0;
            ActiveMatchIndex = 0;
            _hasResult = false;
            return FindBarIntent.None;
        }

        _query = value;
        _hasResult = false; // 新查询的命中数尚未回报
        return FindBarIntent.Search;
    }

    /// <summary>
    /// 记录内核回报的命中结果。内核在会话刚建立时可能给出 -1 的活动索引，
    /// 这里统一收敛为「无活动命中」，并把序号夹取到 [1, 总数] 防止越界显示。
    /// 未打开或查询已被清空时忽略（异步回报不得复活已关闭的查找条）。
    /// </summary>
    public void ApplyResult(int matchCount, int activeMatchIndex)
    {
        if (!IsOpen || _query.Length == 0)
            return;

        MatchCount = matchCount < 0 ? 0 : matchCount;
        _hasResult = true;

        if (MatchCount == 0 || activeMatchIndex < 1)
        {
            ActiveMatchIndex = 0;
            return;
        }

        ActiveMatchIndex = activeMatchIndex > MatchCount ? MatchCount : activeMatchIndex;
    }

    /// <summary>状态文案（空串表示不显示）。</summary>
    public string StatusText
    {
        get
        {
            if (!IsOpen || _query.Length == 0 || !_hasResult)
                return string.Empty;

            if (MatchCount == 0)
                return NoResultsText;

            return ActiveMatchIndex >= 1
                ? ActiveMatchIndex + " / " + MatchCount
                : MatchCount + " 项";
        }
    }

    /// <summary>
    /// 按键语义：Enter → 下一个，Shift+Enter → 上一个，Esc → 关闭。
    /// 查询为空时 Enter 不动作（没有可跳转的会话）。
    /// </summary>
    public FindBarIntent MapKey(FindKey key, bool shift)
    {
        switch (key)
        {
            case FindKey.Escape:
                return IsOpen ? FindBarIntent.Close : FindBarIntent.None;

            case FindKey.Enter:
                if (!IsOpen || _query.Length == 0)
                    return FindBarIntent.None;
                return shift ? FindBarIntent.Previous : FindBarIntent.Next;

            default:
                return FindBarIntent.None;
        }
    }
}
