#nullable enable
using System;
using System.Collections.Generic;
using Apage.Core.Models;

namespace Apage.Core.Services;

/// <summary>
/// 内核命令行参数收口（R1，方法论借鉴 ungoogled-chromium 的「默认禁用、按需启用」清单化思路）。
/// <para>
/// 全部参数均为 stock Chromium/Edge 原生开关，经 WebView2 <c>AdditionalBrowserArguments</c> 原样透传。
/// 非 stock 开关（ungoogled 私有 chrome://flags、Bromite 补丁 flag、非 stock feature 名）会被内核
/// <b>静默忽略</b>，制造「配置看起来很硬、实际全被忽略」的虚假安全感——列入
/// <see cref="ForbiddenSwitches"/> 并由 <see cref="BrowserArgumentsTests"/> 把守，禁止传入。
/// </para>
/// <para>
/// 开关名随 Edge Runtime 大版本漂移（msSmartScreenProtection 等为版本相关名），
/// 每次 Runtime 大版本更新需复核本清单；冒烟只做软校验，真机生效证据见
/// docs/spikes/results/browser-arguments.md。
/// </para>
/// </summary>
public static class BrowserArguments
{
    /// <summary>
    /// 外部注入覆盖开关。微软文档：同时指定命令行开关与 AdditionalBrowserArguments 时，
    /// <c>--edge-webview-switches</c> 的值优先并被处理——宿主或外部注入会覆盖 Apage 的配置，
    /// 启动时检测到即记本地日志告警（<see cref="HasExternalOverride"/>）。
    /// </summary>
    public const string ExternalOverrideSwitch = "--edge-webview-switches";

    /// <summary>常驻 stock 开关：关闭内核自带的出站 / 后台 / 同步行为（不含任何产品隐私默认值，属技术开关）。</summary>
    private static readonly string[] AlwaysOnSwitches =
    {
        "--disable-background-networking",
        "--disable-component-update",
        "--disable-domain-reliability",
        "--disable-sync",
        "--no-default-browser-check",
        "--disable-client-side-phishing-detection",
        "--no-pings",
    };

    /// <summary>
    /// 关闭一组 Edge 侧 feature：msWebOOUI/msPdfOOUI（PDF 查看器 UI）、msSmartScreenProtection（信誉检查，
    /// 另见 AppSettings.DisableReputationChecking 的显式取舍，R12）、OptimizationGuideModelDownloading
    /// （会静默联网下载 ML 模型，与「不静默联网」红线正面冲突）、MediaRouter。
    /// </summary>
    private const string DisableFeatures =
        "--disable-features=msWebOOUI,msPdfOOUI,msSmartScreenProtection,OptimizationGuideModelDownloading,MediaRouter";

    /// <summary>固定 Accept-Language（微软 WebView2 文档明确支持）：服务「全本地化」定位并减少系统语言泄漏面。</summary>
    private const string AcceptLanguage = "--accept-lang=zh-CN";

    /// <summary>WebRTC IP 泄露防护（§5.2）：防本地/公网 IP 经 WebRTC 泄露，不断连会议类网站；按设置生效。</summary>
    private const string WebRtcIpProtection = "--force-webrtc-ip-handling-policy=disable_non_proxied_udp";

    /// <summary>
    /// 禁止传入清单（非 stock，传入会被静默忽略并造成虚假安全感）：
    /// ungoogled 私有 chrome://flags 条目、Bromite canvas 指纹噪声补丁 flag。
    /// 单测断言 <see cref="Build"/> 的输出不含其中任何一项。
    /// </summary>
    public static IReadOnlyList<string> ForbiddenSwitches { get; } = new[]
    {
        // ungoogled-chromium 私有 chrome://flags（非 stock Chromium 开关）
        "--disable-search-engine-collection",
        "--remove-tabsearch-button",
        "--popups-to-tabs",
        "--hide-extensions-menu",
        "--extension-mime-request-handling",
        "--disable-qr-generator",
        "--tab-hover-cards",
        "--clear-data-on-exit",
        // Bromite 源码补丁 flag（需内核补丁，非原生开关）
        "--fingerprinting-canvas-image-data-noise",
        "--fingerprinting-canvas-measuretext-noise",
    };

    /// <summary>
    /// 按设置拼装内核命令行参数（空格分隔，供 <c>CoreWebView2EnvironmentOptions.AdditionalBrowserArguments</c>）。
    /// </summary>
    /// <param name="settings">应用设置；仅读取 <see cref="AppSettings.WebRTCIPProtection"/>，不写回。</param>
    public static string Build(AppSettings settings)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));

        var parts = new List<string>(AlwaysOnSwitches.Length + 3);
        parts.AddRange(AlwaysOnSwitches);
        parts.Add(DisableFeatures);
        parts.Add(AcceptLanguage);
        if (settings.WebRTCIPProtection)
            parts.Add(WebRtcIpProtection);
        return string.Join(" ", parts);
    }

    /// <summary>
    /// 检测外部是否注入了 <see cref="ExternalOverrideSwitch"/>（宿主/外部注入会覆盖 Apage 参数）。
    /// 命中时调用方应记本地日志告警，不得静默继续。
    /// </summary>
    /// <param name="commandLineArgs">进程命令行参数（如 <c>Environment.GetCommandLineArgs()</c>）。</param>
    public static bool HasExternalOverride(IEnumerable<string>? commandLineArgs)
    {
        if (commandLineArgs is null) return false;
        foreach (var arg in commandLineArgs)
        {
            if (!string.IsNullOrWhiteSpace(arg) &&
                arg.StartsWith(ExternalOverrideSwitch, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
