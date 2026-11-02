#nullable enable
using System;
using System.Globalization;

namespace Apage.Core.Services;

/// <summary>
/// 被拦请求的空资源策略（R4，借鉴 adblock-rust 的 resource substitution）：
/// 命中拦截规则时不裸 403，而是按资源类型返回透明空资源（image → 1x1 透明 GIF，
/// script / stylesheet / font → 空文本流），其余类型维持取消请求语义（403）。
/// 直接失败请求会让页面脚本抛未捕获异常导致白屏/破面；返回可解析的空资源能显著
/// 降低破面率，且统计语义（仍计一次拦截）与顶层主文档豁免均不变。
/// 壳层（BrowserTabView）只负责把 <see cref="Kind"/> 映射为
/// <c>CreateWebResourceResponse</c>；本类为纯逻辑，按决策 #18 优先单测。
/// </summary>
public static class BlockedResponse
{
    /// <summary>空资源响应形态。</summary>
    public enum Kind
    {
        /// <summary>1x1 透明 GIF（图片类）。</summary>
        TransparentGif,

        /// <summary>空文本流（脚本 / 样式 / 字体）。</summary>
        EmptyText,

        /// <summary>取消请求（空内容 + 403；未知/其余类型 fail-closed 维持原语义）。</summary>
        Deny
    }

    /// <summary>经典 1x1 透明 GIF 常量（GIF89a 魔数开头）。</summary>
    public static readonly byte[] TransparentGif = Convert.FromBase64String(
        "R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");

    private static readonly byte[] Empty = Array.Empty<byte>();

    /// <summary>
    /// 按 WebView2 资源上下文名（<c>CoreWebView2WebResourceContext</c> 的字符串形式，
    /// 大小写不敏感）选择响应形态；未知类型（含 null）一律 <see cref="Kind.Deny"/>——
    /// fail-closed：拿不准就不改变既有拦截语义。
    /// </summary>
    public static Kind For(string? resourceContext)
    {
        switch (resourceContext?.Trim().ToLowerInvariant())
        {
            case "image": return Kind.TransparentGif;
            case "script": return Kind.EmptyText;
            case "stylesheet": return Kind.EmptyText;
            case "font": return Kind.EmptyText;
            default: return Kind.Deny;
        }
    }

    /// <summary>响应体字节（<see cref="Kind.Deny"/> 返回空数组，壳层应传 null 流）。</summary>
    public static byte[] Payload(Kind kind) => kind == Kind.TransparentGif ? TransparentGif : Empty;

    /// <summary>Content-Type（<see cref="Kind.Deny"/> 为空串）。</summary>
    public static string ContentType(Kind kind) => kind switch
    {
        Kind.TransparentGif => "image/gif",
        Kind.EmptyText => "text/plain; charset=utf-8",
        _ => string.Empty,
    };

    /// <summary>HTTP 状态码与原因短语（Deny = 403 Blocked；其余 = 200 OK）。</summary>
    public static (int StatusCode, string Reason) Status(Kind kind) =>
        kind == Kind.Deny ? (403, "Blocked") : (200, "OK");

    /// <summary>
    /// 壳层直接透传的响应头（Content-Type + Content-Length，与 <see cref="Payload"/> 长度一致；
    /// <see cref="Kind.Deny"/> 返回空串）。Content-Length 必须正确，否则 WebView2 可能报协议错误。
    /// </summary>
    public static string Headers(Kind kind)
    {
        if (kind == Kind.Deny) return string.Empty;
        return "Content-Type: " + ContentType(kind)
            + "\r\nContent-Length: " + Payload(kind).Length.ToString(CultureInfo.InvariantCulture);
    }
}
