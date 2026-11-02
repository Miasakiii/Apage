#nullable enable
using System;
using System.Globalization;
using System.Threading;

namespace Apage.Core.Services;

/// <summary>
/// 页级拦截计数（R6，Brave 盾牌徽章的本地等价物）。
/// <para>
/// 目的：用户改完设置或遇到可疑页面时，能<b>立即</b>看到防护在本页生效了多少次，
/// 而不必等到周末看周统计。计数随顶层导航归零（新页面重新累计）。
/// </para>
/// <para>
/// 诚实口径：引擎当前不区分广告/跟踪规则（<see cref="AdBlockRuleEngine.IsBlocked"/>
/// 是单一判定），因此只有一类计数；不做 Brave 盾牌面板里
/// fingerprinters blocked 那类 Apage 无法真实产出的类别——数字只能真实或没有。
/// </para>
/// </summary>
public sealed class PageBlockCounter
{
    private int _count;

    /// <summary>当前页累计拦截次数。</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>徽章文案：0 = 空串（宿主据此隐藏徽章）；否则为计数文本。</summary>
    public string BadgeText =>
        Count > 0 ? Count.ToString(CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>计数变化时触发（宿主刷新徽章；线程安全，事件在调用线程同步触发）。</summary>
    public event EventHandler? Changed;

    /// <summary>记录一次拦截（锁内自增；count &lt;= 0 时忽略）。</summary>
    public void Increment(int count = 1)
    {
        if (count <= 0)
            return;
        Interlocked.Add(ref _count, count);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>归零（顶层导航开始时调用）。计数已为 0 时也触发事件，保证徽章状态收敛。</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _count, 0);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
