using System.IO;
using Apage.Core.Services;
using Xunit;

namespace Apage.Core.Tests;

/// <summary>
/// Public Suffix List 解析与 eTLD+1（A2 / S0 §4.1）：
/// 普通、通配、例外规则；IDN、大小写、尾点；IP/localhost/非法值安全回退。
/// </summary>
public sealed class PublicSuffixListTests
{
    private static readonly PublicSuffixList Fixture = PublicSuffixList.Parse(new StringReader(
        "// VERSION: test-fixture\n" +
        "// COMMIT: abc123\n" +
        "com\n" +
        "uk\n" +
        "co.uk\n" +
        "jp\n" +
        "kawasaki.jp\n" +
        "*.ck\n" +
        "!www.ck\n" +
        "!city.kawasaki.jp\n" +
        "食狮.com.cn\n"));

    [Theory]
    [InlineData("www.example.com", "example.com")]
    [InlineData("a.b.example.com", "example.com")]
    [InlineData("a.b.example.co.uk", "example.co.uk")]
    [InlineData("WWW.EXAMPLE.COM.", "example.com")]
    [InlineData("www.foo.ck", "www.foo.ck")]                 // *.ck 使 foo.ck 成为公共后缀
    [InlineData("www.ck", "www.ck")]                       // 例外规则覆盖 *.ck
    [InlineData("city.kawasaki.jp", "city.kawasaki.jp")]    // 例外规则
    [InlineData("sub.city.kawasaki.jp", "city.kawasaki.jp")]
    [InlineData("foo.internal", "foo.internal")]            // 未知 TLD 按默认 * 规则
    public void GetRegistrableDomain_HandlesRulesAndNormalization(string host, string expected)
    {
        Assert.Equal(expected, Fixture.GetRegistrableDomain(host));
    }

    [Theory]
    [InlineData("foo.ck")]          // *.ck 使 foo.ck 本身成为公共后缀
    [InlineData("ck")]
    [InlineData("co.uk")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("example.com:8080")]
    [InlineData("")]
    [InlineData(null)]
    public void GetRegistrableDomain_UnreliableInputs_ReturnNull(string? host)
    {
        Assert.Null(Fixture.GetRegistrableDomain(host));
    }

    [Theory]
    [InlineData("foo.ck", "foo.ck")]        // 通配规则使第三级成为公共后缀
    [InlineData("www.ck", "ck")]            // 例外规则回退一级
    [InlineData("a.b.example.co.uk", "co.uk")]
    public void GetPublicSuffix_HandlesWildcardAndException(string host, string expected)
    {
        Assert.Equal(expected, Fixture.GetPublicSuffix(host));
    }

    [Fact]
    public void GetRegistrableDomain_IdnUnicodeAndPunycode_AreEquivalent()
    {
        var unicode = Fixture.GetRegistrableDomain("www.食狮.com.cn");
        var punycode = Fixture.GetRegistrableDomain("www.xn--85x722f.com.cn");

        Assert.NotNull(unicode);
        Assert.Equal(punycode, unicode);
    }

    [Fact]
    public void Parse_TracksVersionAndRuleCount()
    {
        Assert.Equal("test-fixture", Fixture.Version);
        Assert.Equal("abc123", Fixture.Commit);
        Assert.Equal(9, Fixture.RuleCount);
    }

    [Fact]
    public void LoadEmbedded_ContainsCurrentPslAndKnownSuffixes()
    {
        var psl = PublicSuffixList.LoadEmbedded();

        Assert.NotNull(psl.Version);
        Assert.StartsWith("2026-", psl.Version);
        Assert.True(psl.RuleCount > 9000);
        Assert.Equal("example.co.uk", psl.GetRegistrableDomain("a.b.example.co.uk"));
        Assert.Equal("example.com", psl.GetRegistrableDomain("www.example.com"));
    }
}
