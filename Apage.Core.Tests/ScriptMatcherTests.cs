using System;
using Apage.Core.Services.ScriptManager;
using Xunit;

namespace Apage.Core.Tests;

/// <summary>
/// ScriptMatcher 核心不变量（docs/script-manager.md §4，决策 #18）：
/// §4.1 match pattern 示例表逐条锁定、§4.3 排除优先级、非法模式安全丢弃。
/// </summary>
public class ScriptMatcherTests
{
    // —— §4.1 示例表：三组「匹配 / 不匹配」样例逐条锁定 ——

    [Theory]
    [InlineData("https://*.example.com/*", "https://www.example.com/page", true)]
    [InlineData("https://*.example.com/*", "http://www.example.com/page", false)]  // scheme 不符
    [InlineData("*://example.com/*", "https://example.com/", true)]
    [InlineData("*://example.com/*", "https://sub.example.com/", false)]           // 无 *. 前缀不匹配子域
    [InlineData("https://example.com/path*", "https://example.com/path/to/page", true)]
    [InlineData("https://example.com/path*", "https://example.com/other", false)]
    public void Matches_MatchPattern_FollowsDesignExamples(string pattern, string url, bool expected)
    {
        var matcher = new ScriptMatcher(new[] { pattern });

        Assert.Equal(expected, matcher.Matches(url));
    }

    [Fact]
    public void Matches_WildcardHost_AlsoMatchesBareDomain()
    {
        // Chrome match pattern 语义：*.example.com 同时命中 example.com 本身
        var matcher = new ScriptMatcher(new[] { "https://*.example.com/*" });

        Assert.True(matcher.Matches("https://example.com/page"));
        Assert.True(matcher.Matches("https://a.b.example.com/page"));
    }

    [Fact]
    public void Matches_SchemeWildcard_OnlyCoversHttpAndHttps()
    {
        var matcher = new ScriptMatcher(new[] { "*://example.com/*" });

        Assert.True(matcher.Matches("http://example.com/"));
        Assert.False(matcher.Matches("ftp://example.com/"));
        Assert.False(matcher.Matches("file:///example.com/"));
    }

    // —— §4.2 @include：/正则/ 与 * 通配符 ——

    [Fact]
    public void Matches_IncludeRegex_Works()
    {
        var matcher = new ScriptMatcher(
            matches: Array.Empty<string>(),
            includes: new[] { @"/^https?://.*\.test\.com/" });

        Assert.True(matcher.Matches("http://foo.test.com/page"));
        Assert.True(matcher.Matches("https://bar.test.com/"));
        Assert.False(matcher.Matches("https://test.org/"));
    }

    [Fact]
    public void Matches_IncludeWildcard_Works()
    {
        var matcher = new ScriptMatcher(
            matches: Array.Empty<string>(),
            includes: new[] { "https://*.example.com/*" });

        Assert.True(matcher.Matches("https://www.example.com/page"));
        Assert.False(matcher.Matches("https://other.org/"));
    }

    // —— §4.3 匹配优先级：排除永远压过匹配 ——

    [Fact]
    public void Matches_ExcludeBeatsMatch()
    {
        var matcher = new ScriptMatcher(
            matches: new[] { "https://example.com/*" },
            excludes: new[] { "https://example.com/admin*" });

        Assert.True(matcher.Matches("https://example.com/page"));
        Assert.False(matcher.Matches("https://example.com/admin/users"));
    }

    [Fact]
    public void Matches_ExcludeMatchBeatsInclude()
    {
        var matcher = new ScriptMatcher(
            matches: Array.Empty<string>(),
            includes: new[] { "https://example.com/*" },
            excludeMatches: new[] { "https://example.com/private/*" });

        Assert.True(matcher.Matches("https://example.com/public"));
        Assert.False(matcher.Matches("https://example.com/private/data"));
    }

    // —— 边界与健壮性 ——

    [Fact]
    public void Matches_NoPatterns_NeverMatches()
    {
        var matcher = new ScriptMatcher(Array.Empty<string>());

        Assert.False(matcher.Matches("https://example.com/"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Matches_BlankUrl_ReturnsFalse(string url)
    {
        var matcher = new ScriptMatcher(new[] { "*://example.com/*" });

        Assert.False(matcher.Matches(url));
    }

    [Theory]
    [InlineData("not-a-pattern")]              // 无 scheme
    [InlineData("chrome://settings/*")]        // 不支持的 scheme
    [InlineData("https://exam*ple.com/*")]     // host 中段 * 非法
    [InlineData("https://example.com")]        // 缺 path
    public void Matches_InvalidMatchPattern_IsDiscardedSilently(string pattern)
    {
        // 非法模式不抛异常、不误匹配（§4.4：坏模式丢弃）
        var matcher = new ScriptMatcher(new[] { pattern });

        Assert.False(matcher.Matches("https://example.com/"));
    }

    [Fact]
    public void Matches_InvalidIncludeRegex_IsDiscardedSilently()
    {
        var matcher = new ScriptMatcher(
            matches: Array.Empty<string>(),
            includes: new[] { "/[未闭合的(正则/" });

        Assert.False(matcher.Matches("https://example.com/"));
    }

    [Fact]
    public void Matches_UriOverload_AgreesWithStringOverload()
    {
        var matcher = new ScriptMatcher(new[] { "https://*.example.com/*" });

        Assert.True(matcher.Matches(new Uri("https://www.example.com/page")));
        Assert.False(matcher.Matches(new Uri("https://other.org/page")));
    }

    [Fact]
    public void Ctor_FromMetadata_UsesAllFourPatternLists()
    {
        var parsed = ScriptMetadataParser.Parse("""
            // ==UserScript==
            // @name 组合脚本
            // @match https://example.com/*
            // @exclude https://example.com/admin*
            // ==/UserScript==
            """);
        var matcher = new ScriptMatcher(parsed.Metadata!);

        Assert.True(matcher.Matches("https://example.com/page"));
        Assert.False(matcher.Matches("https://example.com/admin/panel"));
    }
}
