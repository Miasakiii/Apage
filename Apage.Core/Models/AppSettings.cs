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

    /// <summary>
    /// 旧版“第三方 Cookie 剥离”开关。2026-11-02 真机探针证实 WebView2 会在
    /// WebResourceRequested 之后重新附加 Cookie，该能力无法成立；字段保留仅为兼容旧
    /// settings.json，默认 false，UI 与请求管线均不再消费。
    /// </summary>
    public bool BlockThirdPartyCookies { get; set; }

    /// <summary>发送 DNT: 1 请求头（默认开，声明性，多数站点忽略）。</summary>
    public bool SendDoNotTrack { get; set; } = true;

    /// <summary>发送 Sec-GPC: 1 请求头（默认开，部分法域有法律效力）。</summary>
    public bool SendGPC { get; set; } = true;

    /// <summary>WebRTC IP 泄露防护（默认开，防泄露但不断连会议类网站）。</summary>
    public bool WebRTCIPProtection { get; set; } = true;

    /// <summary>联网搜索建议（默认关：开启会把击键发给搜索引擎）。</summary>
    public bool OnlineSearchSuggestions { get; set; }

    /// <summary>
    /// 为新标签页搜索框获取引擎图标（默认开）。
    /// 仅对白名单内的四个搜索引擎主机各抓一次 favicon.ico（HTTPS、限时限大小），
    /// 结果落盘后再不请求；关闭后不发任何请求，图标回落字标。
    /// 与「零后台请求」原则的偏差已在 docs/product-design.md 的网络请求清单中明示。
    /// </summary>
    public bool FetchEngineIcons { get; set; } = true;

    /// <summary>退出时清理隐私数据（默认关）。</summary>
    public bool ClearOnExit { get; set; }
}
