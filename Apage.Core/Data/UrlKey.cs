#nullable enable
using System;

namespace Apage.Core.Data;

/// <summary>
/// URL 归一化（书签去重键、历史与下载的合法性判定）。
/// 只接受 http/https：file://（含本地新标签页）、javascript:、about:、data: 一律拒绝，
/// 避免把本地页面或脚本地址写进历史/书签。
/// 归一规则：scheme 与 punycode 主机小写、去默认端口、去片段；路径与查询保留原样
/// （路径大小写敏感，不能整体小写）。
/// </summary>
public static class UrlKey
{
    /// <summary>单条 URL 长度上限：超长视为脏数据，不写库（防止页面构造超长地址撑爆存储）。</summary>
    public const int MaxUrlLength = 2048;

    /// <summary>归一化成功返回 true；<paramref name="normalized"/> 为去重键与展示地址。</summary>
    public static bool TryNormalize(string? raw, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var trimmed = raw!.Trim();
        if (trimmed.Length > MaxUrlLength)
            return false;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return false;

        if (!IsWebScheme(uri.Scheme) || string.IsNullOrEmpty(uri.Host))
            return false;

        try
        {
            var builder = new UriBuilder(uri)
            {
                Scheme = uri.Scheme.ToLowerInvariant(),
                Host = NormalizeHost(uri),
                Fragment = string.Empty,
                // 浏览器地址中的 userinfo 可能包含账号/密码：绝不写入历史、书签、
                // 下载记录或地址栏建议。需要认证的站点应由页面自身完成登录流程。
                UserName = string.Empty,
                Password = string.Empty,
            };

            if ((builder.Scheme == "http" && builder.Port == 80) ||
                (builder.Scheme == "https" && builder.Port == 443))
            {
                builder.Port = -1; // 默认端口不参与去重
            }

            normalized = builder.Uri.AbsoluteUri;
            return normalized.Length > 0;
        }
        catch (UriFormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            // 非法主机名（如含控制字符）由 UriBuilder 抛出，按不可用处理
            return false;
        }
    }

    /// <summary>是否为可入库的 http/https 地址。</summary>
    public static bool IsWebUrl(string? raw) => TryNormalize(raw, out _);

    /// <summary>提取主机名（用于书签标题兜底与展示）；失败返回空串。</summary>
    public static string GetHost(string? raw)
    {
        if (!Uri.TryCreate(raw?.Trim(), UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return string.Empty;
        return NormalizeHost(uri);
    }

    private static string NormalizeHost(Uri uri)
    {
        var host = uri.IdnHost;
        if (host.Length > 1 && host[host.Length - 1] == '.')
            host = host.Substring(0, host.Length - 1); // 尾点是 DNS 绝对名写法，去重时归一
        return host.ToLowerInvariant();
    }

    private static bool IsWebScheme(string scheme) =>
        string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase);
}
