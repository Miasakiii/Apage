using System;
using Apage.Core.Services;
using Xunit;

namespace Apage.Core.Tests;

/// <summary>
/// AdBlockRuleEngine 核心不变量（docs/product-design.md §5.3，spikes/results/adblock.md，决策 #18）：
/// ABP 规则语义（|| 锚定、^ 分隔符、@@ 例外、## 化妆分桶）、注释/超长规则拒收、退化桶兜底。
/// </summary>
public class AdBlockRuleEngineTests
{
    // —— 网络拦截规则语义 ——

    [Fact]
    public void IsBlocked_DomainAnchorRule_BlocksDomainAndSubdomains()
    {
        var engine = new AdBlockRuleEngine();
        engine.AddRule("||ads.example.com^");

        Assert.True(engine.IsBlocked("https://ads.example.com/banner.js"));
        Assert.True(engine.IsBlocked("https://sub.ads.example.com/x.gif"));   // || 锚定包含子域
        Assert.False(engine.IsBlocked("https://example.com/page"));           // 主域不受牵连
        Assert.False(engine.IsBlocked("https://myads.example.org/x"));        // 域名边界不越界
    }

    [Fact]
    public void IsBlocked_IsCaseInsensitive()
    {
        var engine = new AdBlockRuleEngine();
        engine.AddRule("||ads.example.com^");

        Assert.True(engine.IsBlocked("HTTPS://ADS.EXAMPLE.COM/Banner.JS"));
    }

    [Fact]
    public void IsBlocked_SeparatorCaret_MatchesQueryAndEndOfUrl()
    {
        // spike 建议 #3 的坑位：^ 消费一个字符，也要能匹配 URL 末尾
        var engine = new AdBlockRuleEngine();
        engine.AddRule("||tracker42.net/adserver^");

        Assert.True(engine.IsBlocked("https://tracker42.net/adserver?zone=1"));  // ^ 匹配 ?
        Assert.True(engine.IsBlocked("https://tracker42.net/adserver"));         // ^ 匹配串尾
        Assert.False(engine.IsBlocked("https://tracker42.net/adserverextra"));   // 后接字母不是分隔符
    }

    [Fact]
    public void IsBlocked_PlainSubstringRule_MatchesAnywhere()
    {
        var engine = new AdBlockRuleEngine();
        engine.AddRule("shop99.com/ads/");

        Assert.True(engine.IsBlocked("https://www.shop99.com/ads/pop.html"));
        Assert.False(engine.IsBlocked("https://www.shop99.com/goods/1"));
    }

    [Fact]
    public void IsBlocked_WildcardAndPathRule_Works()
    {
        var engine = new AdBlockRuleEngine();
        engine.AddRule("||banner7.io/creatives/*");

        Assert.True(engine.IsBlocked("https://banner7.io/creatives/leaderboard.gif"));
        Assert.False(engine.IsBlocked("https://banner7.io/static/logo.png"));
    }

    [Fact]
    public void IsBlocked_ModifierRule_MatchesUrlPart()
    {
        // $ 修饰符当前只解析不求值（与 spike 一致），URL 部分照常拦截
        var engine = new AdBlockRuleEngine();
        engine.AddRule("||metrics8.net^$script,third-party");

        Assert.True(engine.IsBlocked("https://metrics8.net/probe.js"));
    }

    // —— @@ 例外规则 ——

    [Fact]
    public void IsBlocked_ExceptionRule_OverridesBlock()
    {
        var engine = new AdBlockRuleEngine();
        engine.AddRule("||adcdn5.com^");
        engine.AddRule("@@||adcdn5.com/whitelist.js");

        Assert.True(engine.IsBlocked("https://adcdn5.com/evil.js"));
        Assert.False(engine.IsBlocked("https://adcdn5.com/whitelist.js"));   // 例外豁免
        Assert.Equal(1, engine.BlockCount);
        Assert.Equal(1, engine.ExceptionCount);
    }

    // —— ## 化妆规则：不进网络索引，按域名分桶 ——

    [Fact]
    public void CosmeticRules_AreBucketedByDomain_AndNeverBlockUrls()
    {
        var engine = new AdBlockRuleEngine();
        engine.AddRule("##.ad-banner");
        engine.AddRule("example.com##.promo-box");

        Assert.Equal(2, engine.CosmeticCount);
        Assert.Equal(0, engine.BlockCount);
        // 化妆规则绝不参与 URL 拦截判定
        Assert.False(engine.IsBlocked("https://example.com/ad-banner"));

        Assert.Equal(new[] { ".ad-banner", ".promo-box" }, engine.GetCosmeticSelectors("example.com"));
        Assert.Equal(new[] { ".ad-banner" }, engine.GetCosmeticSelectors("other.org"));
    }

    // —— 不可信输入：注释、列表头、空行、超长规则 ——

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("! 这是注释行")]
    [InlineData("[Adblock Plus 2.0]")]
    public void AddRule_NonRuleLines_AreRejected(string? line)
    {
        var engine = new AdBlockRuleEngine();

        Assert.False(engine.AddRule(line));
        Assert.Equal(0, engine.BlockCount + engine.ExceptionCount + engine.CosmeticCount);
    }

    [Fact]
    public void AddRule_OversizedRule_IsRejected()
    {
        // §5.3 供应链安全：单条规则长度上限
        var engine = new AdBlockRuleEngine();
        var oversized = "||" + new string('a', AdBlockRuleEngine.MaxRuleLength) + ".com^";

        Assert.False(engine.AddRule(oversized));
        Assert.Equal(0, engine.BlockCount);
    }

    [Fact]
    public void AddRules_ReturnsOnlyAcceptedCount()
    {
        var engine = new AdBlockRuleEngine();
        var rules = new[] { "! comment", "||ads.example.com^", "", "##.ad", "@@||ok.example.com^" };

        Assert.Equal(3, engine.AddRules(rules));
    }

    // —— 退化桶：提取不到索引 key 的规则仍然生效 ——

    [Fact]
    public void IsBlocked_NoKeyRule_FallsBackToLinearScan()
    {
        // "ad." 无 3+ 字符字母数字段，进退化桶（spike 建议 #5），仍应命中
        var engine = new AdBlockRuleEngine();
        Assert.True(engine.AddRule("ad."));

        Assert.True(engine.IsBlocked("https://site.org/static/ad.js"));
        Assert.False(engine.IsBlocked("https://site.org/static/main.js"));
    }

    // —— 子串索引 key 提取 ——

    [Fact]
    public void ExtractKey_PrefersLongerAndPenalizesCommonTokens()
    {
        // "adserver"（8 字符）胜过通用 token "com"
        Assert.Equal("adserver", AdBlockRuleEngine.ExtractKey("||adserver.com^"));
    }

    [Fact]
    public void ExtractKey_NoAlphanumericRun_ReturnsNull()
    {
        Assert.Null(AdBlockRuleEngine.ExtractKey("ad."));
        Assert.Null(AdBlockRuleEngine.ExtractKey("^*|"));
    }

    // —— ToRegex 与防 ReDoS 基础设施 ——

    [Fact]
    public void ToRegex_AppliesUnifiedMatchTimeout()
    {
        // §5.3：所有规则正则统一 MatchTimeout ≤ 100ms，杜绝灾难性回溯卡死
        var re = AdBlockRuleEngine.ToRegex("||ads.example.com^");

        Assert.Equal(AdBlockRuleEngine.RegexTimeout, re.MatchTimeout);
        Assert.True(AdBlockRuleEngine.RegexTimeout <= TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void ToRegex_PipeAtEnd_AnchorsUrlTail()
    {
        var re = AdBlockRuleEngine.ToRegex("|https://exact5.com/ad.js|");

        Assert.Matches(re, "https://exact5.com/ad.js");
        Assert.DoesNotMatch(re, "https://exact5.com/ad.js?cache=1");
    }

    // —— 边界 ——

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsBlocked_BlankUrl_ReturnsFalse(string? url)
    {
        var engine = new AdBlockRuleEngine();
        engine.AddRule("||ads.example.com^");

        Assert.False(engine.IsBlocked(url));
    }

    [Fact]
    public void IsBlocked_EmptyEngine_BlocksNothing()
    {
        var engine = new AdBlockRuleEngine();

        Assert.False(engine.IsBlocked("https://ads.example.com/banner.js"));
        Assert.Equal(0, engine.TimeoutCount);
    }
}
