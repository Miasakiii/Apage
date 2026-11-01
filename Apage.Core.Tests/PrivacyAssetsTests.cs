using System;
using System.Linq;
using Apage.Core.Models;
using Apage.Core.Services;
using Xunit;

namespace Apage.Core.Tests;

/// <summary>
/// A2 随包隐私资产：清单、哈希校验、PSL 加载、规则快照可执行性、来源/许可/跳过统计。
/// </summary>
public sealed class PrivacyAssetsTests
{
    private static readonly Lazy<PrivacyAssets> Assets = new Lazy<PrivacyAssets>(
        PrivacyAssets.LoadEmbedded);

    [Fact]
    public void Manifest_RecordsSourcesLicensesAndAccounting()
    {
        var manifest = Assets.Value.Manifest;

        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal("MPL-2.0", manifest.Psl.License);
        Assert.Equal("CC BY-SA 3.0", manifest.Rules.License);
        Assert.Equal(2, manifest.Rules.Sources.Count);
        Assert.All(manifest.Rules.Sources, source =>
        {
            Assert.Equal(64, source.Sha256.Length);
            Assert.True(source.TotalLines > 0);
            Assert.True(source.AcceptedRules > 0);
            Assert.Equal(source.TotalLines, source.AcceptedRules + source.SkippedRules);
            Assert.Contains("easylist", source.Url, StringComparison.OrdinalIgnoreCase);
        });

        Assert.Equal(
            manifest.Rules.Sources.Sum(s => s.TotalLines),
            manifest.Rules.AcceptedRules + manifest.Rules.SkippedRules);
        Assert.True(manifest.Rules.AcceptedRules > 90_000);
        Assert.True(manifest.Rules.SkipReasons["unsupportedModifier"] > 0);
        Assert.True(manifest.Rules.SkipReasons["cosmetic"] > 0);
        Assert.True(manifest.Rules.SkipReasons["unsupportedRegex"] > 0);
        Assert.Equal(manifest.Rules.SkipReasons.Values.Sum(), manifest.Rules.SkippedRules);
    }

    [Fact]
    public void CreateRuleEngine_LoadsOnlyExecutableRules()
    {
        var engine = Assets.Value.CreateRuleEngine();

        Assert.Equal(Assets.Value.Manifest.Rules.AcceptedRules, engine.BlockCount + engine.ExceptionCount);
        Assert.Equal(0, engine.UnsupportedModifierRuleCount);
        Assert.Equal(0, engine.CosmeticCount);
        Assert.True(engine.BlockCount > 0);

        // 抽样验证快照不是“只计数不生效”：EasyList 的常见域锚定规则应真实命中。
        Assert.True(engine.IsBlocked("https://doubleclick.com/ad.js"));
        Assert.True(engine.IsBlocked("https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js"));
    }

    [Fact]
    public void LoadEmbedded_ParsesPslAndFindsRegistrableDomain()
    {
        var psl = Assets.Value.PublicSuffix;

        Assert.Equal(Assets.Value.Manifest.Psl.Version, psl.Version);
        Assert.Equal("example.co.uk", psl.GetRegistrableDomain("a.b.example.co.uk"));
    }

    [Fact]
    public void EmbeddedResources_IncludeLegalNoticesAndLicenseTexts()
    {
        var names = typeof(PrivacyAssets).Assembly.GetManifestResourceNames();
        Assert.Contains(names, n => n.EndsWith("THIRD-PARTY-NOTICES.txt", StringComparison.Ordinal));
        Assert.Contains(names, n => n.EndsWith("LICENSE-MIT.txt", StringComparison.Ordinal));
        Assert.Contains(names, n => n.EndsWith("LICENSE-MPL-2.0.txt", StringComparison.Ordinal));
        Assert.Contains(names, n => n.EndsWith("LICENSE-CC-BY-SA-3.0.txt", StringComparison.Ordinal));
    }
}
