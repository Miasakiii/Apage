#nullable enable
using System;
using System.Collections.Generic;

namespace Apage.Core.Services;

/// <summary>
/// 允许触发「一次性引擎图标抓取」的主机白名单。
///
/// 为什么需要白名单：新标签页要显示搜索引擎图标，但用户不一定访问过该引擎站点，
/// 本地缓存因此为空。为让首屏四个引擎图标一次到位，允许对**这里列出的主机**各抓一次
/// favicon.ico，抓取结果按 <see cref="FaviconStore"/> 落盘，之后不再请求。
///
/// 边界（不可放宽）：
/// - 只有本白名单内的主机可以被抓取；页面传来的任意主机一律拒绝，避免把浏览器
///   变成通用取图代理；
/// - 每个主机在一次会话内最多尝试一次，失败不再重试；
/// - 用户关闭「获取搜索引擎图标」后完全不发请求。
/// </summary>
public static class SearchEngineHosts
{
    /// <summary>引擎 id → 主机别名（按优先级，首个为抓取目标）。</summary>
    private static readonly Dictionary<string, string[]> ByEngine = new(StringComparer.OrdinalIgnoreCase)
    {
        ["baidu"] = new[] { "www.baidu.com", "baidu.com" },
        ["bing"] = new[] { "www.bing.com", "cn.bing.com", "bing.com" },
        ["google"] = new[] { "www.google.com", "google.com" },
        ["duckduckgo"] = new[] { "duckduckgo.com", "www.duckduckgo.com" },
    };

    private static readonly HashSet<string> AllowedHosts = BuildAll();

    /// <summary>全部允许抓取的主机（小写、已归一化）。</summary>
    public static IReadOnlyCollection<string> All => AllowedHosts;

    /// <summary>该主机是否在白名单内（入参会先归一化）。</summary>
    public static bool IsAllowed(string? host)
    {
        var normalized = FaviconStore.NormalizeHost(host);
        return normalized != null && AllowedHosts.Contains(normalized);
    }

    /// <summary>引擎 id 的首选抓取主机；未知引擎返回 null。</summary>
    public static string? PrimaryHostFor(string? engineId)
    {
        if (string.IsNullOrWhiteSpace(engineId))
            return null;
        return ByEngine.TryGetValue(engineId!.Trim(), out var hosts) && hosts.Length > 0
            ? hosts[0]
            : null;
    }

    private static HashSet<string> BuildAll()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hosts in ByEngine.Values)
        {
            foreach (var host in hosts)
            {
                var normalized = FaviconStore.NormalizeHost(host);
                if (normalized != null)
                    set.Add(normalized);
            }
        }
        return set;
    }
}
