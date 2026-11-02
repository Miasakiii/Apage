#nullable enable
using Apage.Core.Services;

namespace Apage.Core.Models;

/// <summary>
/// 单个标签页的依赖注入包（docs/design/2026-10-23-privacy-stats-ntp.md §3.4）。
/// 把 <see cref="Settings"/> / <see cref="Stats"/> / <see cref="AdBlock"/> / <see cref="Lifecycle"/>
/// 收敛为一个对象，取代 BrowserTabView 现有的 Lifecycle/AdBlock 双属性
/// （魔法赋值顺序收敛为一个对象；须在 Loaded 前赋值的约束不变）。
/// Portable 层装配后经 MainWindow → TabManager → 各 BrowserTabView 下发。
/// </summary>
public sealed class BrowserTabContext
{
    /// <summary>应用设置（隐私开关等）。默认新实例，避免 null 向下传播。</summary>
    public AppSettings Settings { get; set; } = new();

    /// <summary>隐私拦截统计服务（记录广告 / 追踪拦截数）。未装配时为 <see langword="null"/>。</summary>
    public PrivacyStatsService? Stats { get; set; }

    /// <summary>广告拦截规则引擎。未装配时为 <see langword="null"/>。</summary>
    public AdBlockRuleEngine? AdBlock { get; set; }

    /// <summary>
    /// 规则引擎共享持有者（A3）：手动更新后热替换，已在浏览的标签无需重建即可用新规则。
    /// 设置后优先于 <see cref="AdBlock"/>。
    /// </summary>
    public AdBlockRuleProvider? AdBlockProvider { get; set; }

    /// <summary>基于本地 PSL 的第三方判定；未装配时调用方回退到末两标签安全近似。</summary>
    public ThirdPartyDetector? ThirdParty { get; set; }

    /// <summary>
    /// 历史记录仓储；未装配（SQLite 初始化失败）时不写历史，浏览不受影响。
    /// 隐私标签与本地页面由标签视图在调用前排除。
    /// </summary>
    public Data.HistoryRepository? History { get; set; }

    /// <summary>浏览器内核生命周期服务（创建 / 持有 WebView2 Environment）。未装配时为 <see langword="null"/>。</summary>
    public BrowserLifecycleService? Lifecycle { get; set; }

    /// <summary>
    /// 站点图标本地缓存（新标签页快捷方式复用已访问站点的 favicon）。
    /// 未装配时不写盘，标签图标仍可正常显示，只是新标签页回落字标。
    /// </summary>
    public FaviconStore? Favicons { get; set; }
}
