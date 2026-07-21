namespace Apage.Core.Models;

/// <summary>
/// 应用设置。默认值即「隐私默认全开」基线（docs/product-design.md §5.2）。
/// 反序列化时 JSON 缺失的字段回落到此处默认值。
/// </summary>
public sealed record AppSettings
{
    /// <summary>搜索引擎标识（bing / google / duckduckgo 等）。</summary>
    public string SearchEngine { get; set; } = "bing";

    /// <summary>缓存位置（R7）：false = 宿主机 %TEMP%（默认，护 U 盘）；true = 纯便携，缓存随数据目录走。</summary>
    public bool PortableCache { get; set; }

    /// <summary>主题：system / light / dark。</summary>
    public string Theme { get; set; } = "system";

    /// <summary>拦截第三方 Cookie（默认开）。</summary>
    public bool BlockThirdPartyCookies { get; set; } = true;

    /// <summary>发送 DNT: 1 请求头（默认开，声明性，多数站点忽略）。</summary>
    public bool SendDoNotTrack { get; set; } = true;

    /// <summary>发送 Sec-GPC: 1 请求头（默认开，部分法域有法律效力）。</summary>
    public bool SendGPC { get; set; } = true;

    /// <summary>WebRTC IP 泄露防护（默认开，防泄露但不断连会议类网站）。</summary>
    public bool WebRTCIPProtection { get; set; } = true;

    /// <summary>联网搜索建议（默认关：开启会把击键发给搜索引擎）。</summary>
    public bool OnlineSearchSuggestions { get; set; }

    /// <summary>退出时清理隐私数据（默认关）。</summary>
    public bool ClearOnExit { get; set; }
}
