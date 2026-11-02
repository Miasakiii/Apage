#nullable enable
using System;
using System.Net;

namespace Apage.Core.Services;

/// <summary>
/// 第三方请求判定：页面与请求 host 分别经 PSL 求 registrable domain（eTLD+1），不等即第三方。
/// 安全方向：PSL 无法可靠判断（IP、localhost、非法 URL、仅公共后缀）时返回 false，宁可漏剥不可误剥。
/// 仅 http/https/file 参与比较；file:// 无 host 通常不参与。
/// PSL 加载失败时宿主可显式使用 <see cref="Fallback"/>（末两标签近似，同样保持漏判方向）。
/// </summary>
public sealed class ThirdPartyDetector
{
    private readonly IPublicSuffixResolver _resolver;

    public ThirdPartyDetector(IPublicSuffixResolver resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <summary>PSL 不可用时的安全回退：末两标签近似，多级后缀只会漏判不会误剥。</summary>
    public static ThirdPartyDetector Fallback { get; } = new ThirdPartyDetector(new LastTwoLabelResolver());

    /// <summary>
    /// 判断 <paramref name="requestUrl"/> 相对 <paramref name="pageUrl"/> 是否为第三方。
    /// 任一侧无法解析出 registrable domain → false。
    /// </summary>
    public bool IsThirdParty(string? pageUrl, string? requestUrl)
    {
        var pageHost = GetComparableHost(pageUrl);
        if (pageHost == null)
            return false;

        var requestHost = GetComparableHost(requestUrl);
        if (requestHost == null)
            return false;

        var pageDomain = _resolver.GetRegistrableDomain(pageHost);
        var requestDomain = _resolver.GetRegistrableDomain(requestHost);
        if (pageDomain == null || requestDomain == null)
            return false;

        return !string.Equals(pageDomain, requestDomain, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetComparableHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        if (!Uri.TryCreate(url!.Trim(), UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeFile)
            return null;

        var host = uri.Host; // 不含端口 / 用户信息；file:///… 无 authority 时为空
        return string.IsNullOrEmpty(host) ? null : host;
    }

    private sealed class LastTwoLabelResolver : IPublicSuffixResolver
    {
        public string? GetRegistrableDomain(string? host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return null;
            var value = host!.Trim().TrimEnd('.');
            if (value.Length == 0 || value.IndexOf(':') >= 0 ||
                IPAddress.TryParse(value, out _))
                return null;

            var labels = value.Split('.');
            if (labels.Length <= 1)
                return null;
            if (labels.Length == 2)
                return value;
            return labels[labels.Length - 2] + "." + labels[labels.Length - 1];
        }
    }
}
