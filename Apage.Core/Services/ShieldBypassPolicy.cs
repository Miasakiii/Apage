#nullable enable
namespace Apage.Core.Services;

/// <summary>
/// 站点级防护放行判定（R5 / R7）。
/// <para>
/// 语义边界：覆盖键始终是**顶层页面主机**，不是被请求资源的主机。
/// 例如用户在 <c>news.example</c> 关闭防护后，该页面加载的第三方广告资源也应放行；
/// 若按资源主机查询，页面内第三方资源仍会被拦截，开关实际只放行页面自身的请求。
/// </para>
/// <para>
/// 空/非法顶层主机一律返回 false（fail-closed）：无法确认页面身份时不扩大放行范围。
/// </para>
/// </summary>
public static class ShieldBypassPolicy
{
    /// <summary>
    /// 当前顶层页面是否应跳过广告拦截与计数。站点覆盖与便携允许清单任一命中即放行；
    /// DNT/GPC 等声明性头不在此范围，仍由请求管线发送。
    /// </summary>
    /// <param name="topLevelHost">当前顶层页面主机（不是资源请求主机）。</param>
    /// <param name="shields">站点覆盖存储；未装配时为 null。</param>
    /// <param name="allowList">便携允许清单；未装配时为 null。</param>
    public static bool IsBypassed(
        string? topLevelHost,
        SiteShieldStore? shields,
        SiteAllowList? allowList)
    {
        if (string.IsNullOrWhiteSpace(topLevelHost))
            return false;

        return shields?.IsDisabled(topLevelHost) == true ||
               allowList?.IsShieldsDisabled(topLevelHost) == true;
    }
}
