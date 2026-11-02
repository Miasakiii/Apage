#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Apage.Core.Services;

/// <summary>
/// 本地 bangs 快捷搜索（R8，DuckDuckGo 风格 <c>!触发词 查询</c>，Helium 同款能力）。
/// 地址栏输入 <c>!gh wpf</c> 之类的指令即展开为对应站点搜索 URL，纯本地解析、零联网。
/// <para>
/// 边界（2026-11-02 经用户决策修正，原方案的 SearchEngineHosts 白名单约束已否决）：
/// ① <b>本地静态表</b>——表随编译期内置，无远程更新（同决策 #12「脚本更新仅本地导入」）；
/// ② <b>纯字符串模板展开</b>——Replace 占位符，无动态 URL 生成逻辑；
/// ③ <b>用户主动导航</b>——解析结果交由地址栏 → WebView2 导航，与用户直接输入网址无异；
///    红线 4「零后台请求」管的是应用自发请求（HTTP 客户端类），不管用户主动导航，
///    因此站点直达型（!gh/!w/!yt）不在引擎白名单内也成立；
/// ④ 不进 ABP 规则快照，不污染 94,646 条规则的许可与体积账本。
/// </para>
/// </summary>
public static class BangResolver
{
    /// <summary>查询占位符（所有模板必须包含，由单测把守）。</summary>
    public const string QueryPlaceholder = "{q}";

    /// <summary>
    /// 精选 bang 表（curated 口径：覆盖常用引擎/社区/工具/中文站点，数十条手工维护）。
    /// 全部为 https 目标（单测把守）；触发词大小写不敏感。
    /// </summary>
    public static IReadOnlyDictionary<string, string> Table { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // 搜索引擎
            ["g"] = "https://www.google.com/search?q=" + QueryPlaceholder,
            ["b"] = "https://www.bing.com/search?q=" + QueryPlaceholder,
            ["bd"] = "https://www.baidu.com/s?wd=" + QueryPlaceholder,
            ["ddg"] = "https://duckduckgo.com/?q=" + QueryPlaceholder,
            // 开发者
            ["gh"] = "https://github.com/search?q=" + QueryPlaceholder,
            ["so"] = "https://stackoverflow.com/search?q=" + QueryPlaceholder,
            ["sf"] = "https://segmentfault.com/search?q=" + QueryPlaceholder,
            ["npm"] = "https://www.npmjs.com/search?q=" + QueryPlaceholder,
            ["mdn"] = "https://developer.mozilla.org/search?q=" + QueryPlaceholder,
            // 知识 / 社区
            ["w"] = "https://zh.wikipedia.org/w/index.php?search=" + QueryPlaceholder,
            ["zh"] = "https://www.zhihu.com/search?type=content&q=" + QueryPlaceholder,
            ["r"] = "https://www.reddit.com/search?q=" + QueryPlaceholder,
            ["x"] = "https://x.com/search?q=" + QueryPlaceholder,
            // 中文日常
            ["tb"] = "https://s.taobao.com/search?q=" + QueryPlaceholder,
            ["jd"] = "https://search.jd.com/Search?keyword=" + QueryPlaceholder,
            ["bili"] = "https://search.bilibili.com/all?keyword=" + QueryPlaceholder,
            ["yt"] = "https://www.youtube.com/results?search_query=" + QueryPlaceholder,
            // 地图
            ["map"] = "https://www.openstreetmap.org/search?query=" + QueryPlaceholder,
        };

    /// <summary>
    /// 输入是否形如 bang：<c>!</c> 开头且触发词首字符非空白。
    /// <c>!</c> 单独、<c>! x</c>（触发词为空）、普通输入都不是 bang。
    /// </summary>
    public static bool IsBangInput(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return false;
        var text = input.TrimStart();
        if (!text.StartsWith("!", StringComparison.Ordinal))
            return false;
        if (text.Length < 2)
            return false;
        return !char.IsWhiteSpace(text[1]);
    }

    /// <summary>
    /// 解析 bang 输入为可导航 URL。非 bang、未知触发词、空查询一律返回 null
    /// （调用方回落普通搜索——空查询时用户多半还在输入，不抢导航）。
    /// 查询经 <see cref="Uri.EscapeDataString(string)"/> 转义后填入模板。
    /// </summary>
    public static string? TryResolve(string? input)
    {
        if (!IsBangInput(input))
            return null;

        var text = input!.TrimStart();
        var body = text.Substring(1); // 去掉 '!'
        var space = body.IndexOf(' ');
        string trigger;
        string query;
        if (space < 0)
        {
            trigger = body;
            query = string.Empty;
        }
        else
        {
            trigger = body.Substring(0, space);
            query = body.Substring(space + 1).Trim();
        }

        if (trigger.Length == 0 || !Table.TryGetValue(trigger, out var template))
            return null;
        if (query.Length == 0)
            return null;

        // netstandard2.0 无 Replace(string,string,StringComparison) 重载（2.1 起才有）；
        // 占位符与模板均为编译期常量，两参重载即按序号替换，语义等价。
        return template.Replace(QueryPlaceholder, Uri.EscapeDataString(query));
    }
}
