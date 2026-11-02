#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;

namespace Apage.Core.Models;

/// <summary>
/// 按站点隐私覆盖（R5，Brave Shields 的「全局默认 → 站点覆盖 → 彻底关闭」模型）。
/// <para>
/// 解决的产品缺陷：Apage 此前只有全局隐私默认值，用户遇到被误伤的站点只能整体降级隐私，
/// 与「隐私默认全开是产品红线」冲突。引入站点覆盖后，误伤修复不再回写全局默认值。
/// </para>
/// <para>
/// 硬约束（单测把守）：站点级关闭<b>绝不回写</b>全局 <see cref="AppSettings"/>；
/// 该结构不得进入日志导出（LocalLogService 脱敏清单同步）。
/// </para>
/// <para>
/// 诚实口径：当前引擎（AdBlockRuleEngine）不区分广告/跟踪规则（单一 IsBlocked），
/// 因此本期只提供「彻底关闭」整轴开关；按广告/跟踪分轴需要规则来源分类，属后续工作。
/// </para>
/// </summary>
public sealed class SiteShieldState
{
    /// <summary>规范化主机名（小写、无端口、无尾点；非法主机拒绝入表）。</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>该站点是否彻底关闭防护（shields down：跳过拦截管线与计数）。</summary>
    public bool Disabled { get; set; }

    /// <summary>用户备注（可选；设置页展示用，不参与逻辑）。</summary>
    public string? Note { get; set; }

    /// <summary>
    /// 主机名归一化：与 <see cref="Configuration.AppPaths"/> 无关，独立实现以保持 Models 层零依赖。
    /// 规则与 FaviconStore.NormalizeHost 同构：小写、剥数字端口、去尾点、字符白名单、≤255。
    /// IP 字面量与 localhost 允许作为覆盖键（用户可能要为内网站点关防护）。
    /// </summary>
    public static string? NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        var h = host.Trim().ToLowerInvariant();

        // 剥端口（仅当冒号后全为数字，避免误伤 IPv6 字面量）
        var colon = h.IndexOf(':');
        if (colon > 0)
        {
            var tail = h.Substring(colon + 1);
            var allDigits = tail.Length > 0;
            foreach (var c in tail)
            {
                if (!char.IsDigit(c)) { allDigits = false; break; }
            }
            if (allDigits) h = h.Substring(0, colon);
        }

        // 去尾点
        while (h.EndsWith(".", StringComparison.Ordinal)) h = h.Substring(0, h.Length - 1);
        if (h.Length == 0 || h.Length > 255) return null;

        // 字符白名单：字母数字 + . - _（与 FaviconStore 一致；IPv6 含 ':' 已在上面处理）
        foreach (var c in h)
        {
            if (!(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_'))
                return null;
        }
        return h;
    }
}

/// <summary>站点覆盖表的 JSON 持久化形状（{数据目录}\site-shields.json）。</summary>
public sealed class SiteShieldFile
{
    /// <summary>格式版本；未来结构变更时迁移。</summary>
    public int Version { get; set; } = 1;

    /// <summary>按规范化主机名索引的覆盖条目。</summary>
    public Dictionary<string, SiteShieldEntry> Sites { get; set; } =
        new(StringComparer.Ordinal);
}

/// <summary>单个站点的覆盖条目（与 <see cref="SiteShieldState"/> 同字段，JSON 专用）。</summary>
public sealed class SiteShieldEntry
{
    /// <summary>是否彻底关闭防护。</summary>
    public bool Disabled { get; set; }

    /// <summary>用户备注。</summary>
    public string? Note { get; set; }

    /// <summary>最近一次变更的本地时间（ISO-8601；仅展示用）。</summary>
    public string? UpdatedAt { get; set; }

    /// <summary>序列化用的备用时间戳（JSON 属性名保持稳定，勿改名）。</summary>
    [JsonIgnore]
    public DateTime UpdatedAtLocal
    {
        get => DateTime.TryParse(UpdatedAt, CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.MinValue;
        set => UpdatedAt = value.ToString("o", CultureInfo.InvariantCulture);
    }
}
