#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Apage.Core.Models;

namespace Apage.Core.Services;

/// <summary>一个待筛选的规则文本来源（随包生成与运行时更新共用）。</summary>
public sealed class RuleTextSource
{
    public RuleTextSource(string name, string url, string text)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Url = url ?? string.Empty;
        Text = text ?? string.Empty;
    }

    public string Name { get; }

    public string Url { get; }

    public string Text { get; }
}

/// <summary>一次规则快照构建的结果（字节 + 统计 + 来源清单）。</summary>
public sealed class RuleSnapshotBuildResult
{
    internal RuleSnapshotBuildResult(
        byte[] rulesBytes,
        string rulesSha256,
        int acceptedRules,
        int skippedRules,
        IReadOnlyDictionary<string, int> skipReasons,
        IReadOnlyList<RuleSnapshotSourceManifest> sources)
    {
        RulesBytes = rulesBytes;
        RulesSha256 = rulesSha256;
        AcceptedRules = acceptedRules;
        SkippedRules = skippedRules;
        SkipReasons = skipReasons;
        Sources = sources;
    }

    /// <summary>UTF-8、无 BOM、每行一条规则的快照字节。</summary>
    public byte[] RulesBytes { get; }

    public string RulesSha256 { get; }

    public int AcceptedRules { get; }

    public int SkippedRules { get; }

    public IReadOnlyDictionary<string, int> SkipReasons { get; }

    public IReadOnlyList<RuleSnapshotSourceManifest> Sources { get; }

    public bool IsEmpty => AcceptedRules == 0;
}

/// <summary>
/// 规则快照构建器：把若干列表文本筛成“当前引擎能够正确执行”的规则集。
/// 这是唯一筛选口径——随包生成工具与运行时手动更新都调用这里，
/// 避免两套分类逻辑产生不同结果（A3）。
/// </summary>
public static class RuleSnapshotBuilder
{
    /// <summary>可执行规则下限：低于此数视为列表被截断/被劫持，拒绝替换现有快照。</summary>
    public const int MinimumAcceptedRules = 1000;

    public static RuleSnapshotBuildResult Build(
        IEnumerable<RuleTextSource> sources,
        DateTimeOffset retrievedAtUtc)
    {
        if (sources == null)
            throw new ArgumentNullException(nameof(sources));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rules = new List<string>();
        var skipTotals = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["empty"] = 0,
            ["comment"] = 0,
            ["tooLong"] = 0,
            ["cosmetic"] = 0,
            ["cosmeticUnsupported"] = 0,
            ["unsupportedModifier"] = 0,
            ["unsupportedRegex"] = 0,
            ["invalid"] = 0,
            ["duplicate"] = 0,
        };
        var manifests = new List<RuleSnapshotSourceManifest>();
        var totalSkipped = 0;

        foreach (var source in sources)
        {
            var rawBytes = Encoding.UTF8.GetBytes(source.Text ?? string.Empty);
            var accepted = 0;
            var skipped = 0;
            var lines = 0;

            using (var reader = new System.IO.StringReader(source.Text ?? string.Empty))
            {
                string? raw;
                while ((raw = reader.ReadLine()) != null)
                {
                    lines++;
                    var reason = AdBlockRuleEngine.Classify(raw) switch
                    {
                        AdBlockRuleKind.Empty => "empty",
                        AdBlockRuleKind.Comment => "comment",
                        AdBlockRuleKind.TooLong => "tooLong",
                        AdBlockRuleKind.Cosmetic => "cosmetic",
                        AdBlockRuleKind.CosmeticUnsupported => "cosmeticUnsupported",
                        AdBlockRuleKind.UnsupportedModifier => "unsupportedModifier",
                        AdBlockRuleKind.UnsupportedRegex => "unsupportedRegex",
                        AdBlockRuleKind.Invalid => "invalid",
                        _ => null,
                    };

                    var rule = raw.Trim();
                    if (reason == null && seen.Contains(rule))
                        reason = "duplicate";

                    if (reason != null)
                    {
                        skipTotals[reason]++;
                        skipped++;
                        continue;
                    }

                    seen.Add(rule);
                    rules.Add(rule);
                    accepted++;
                }
            }

            totalSkipped += skipped;
            manifests.Add(new RuleSnapshotSourceManifest
            {
                Name = source.Name,
                Url = source.Url,
                RetrievedAtUtc = retrievedAtUtc,
                Sha256 = Sha256Hex(rawBytes),
                TotalLines = lines,
                AcceptedRules = accepted,
                SkippedRules = skipped,
            });
        }

        var text = rules.Count == 0 ? string.Empty : string.Join("\n", rules) + "\n";
        var bytes = Encoding.UTF8.GetBytes(text);
        return new RuleSnapshotBuildResult(
            bytes,
            Sha256Hex(bytes),
            rules.Count,
            totalSkipped,
            skipTotals,
            manifests);
    }

    internal static string Sha256Hex(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return ToHex(sha.ComputeHash(bytes));
    }

    internal static string ToHex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
            builder.Append(b.ToString("x2"));
        return builder.ToString();
    }
}
