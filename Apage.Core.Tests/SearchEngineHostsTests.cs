using System.Linq;
using Apage.Core.Services;
using Xunit;

namespace Apage.Core.Tests;

/// <summary>
/// SearchEngineHosts：引擎图标抓取的主机白名单。
/// 这个白名单是「不把浏览器变成通用取图代理」的唯一保证，必须锁死：
/// 只有四个引擎的别名主机可用，其余一律拒绝。
/// </summary>
public sealed class SearchEngineHostsTests
{
    [Theory]
    [InlineData("www.baidu.com")]
    [InlineData("baidu.com")]
    [InlineData("www.bing.com")]
    [InlineData("cn.bing.com")]
    [InlineData("bing.com")]
    [InlineData("www.google.com")]
    [InlineData("google.com")]
    [InlineData("duckduckgo.com")]
    public void IsAllowed_EngineHosts_AreAllowed(string host)
    {
        Assert.True(SearchEngineHosts.IsAllowed(host));
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("evil.baidu.com")]
    [InlineData("baidu.com.evil.com")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("")]
    [InlineData(null)]
    public void IsAllowed_OtherHosts_AreRejected(string? host)
    {
        Assert.False(SearchEngineHosts.IsAllowed(host));
    }

    [Fact]
    public void IsAllowed_NormalizesCasePortAndTrailingDot()
    {
        Assert.True(SearchEngineHosts.IsAllowed("WWW.Bing.com"));
        Assert.True(SearchEngineHosts.IsAllowed("www.bing.com:443"));
        Assert.True(SearchEngineHosts.IsAllowed("www.bing.com."));
    }

    [Theory]
    [InlineData("baidu", "www.baidu.com")]
    [InlineData("bing", "www.bing.com")]
    [InlineData("google", "www.google.com")]
    [InlineData("duckduckgo", "duckduckgo.com")]
    [InlineData("BING", "www.bing.com")]
    public void PrimaryHostFor_KnownEngines_ReturnsFirstAlias(string engineId, string expected)
    {
        Assert.Equal(expected, SearchEngineHosts.PrimaryHostFor(engineId));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("yandex")]
    public void PrimaryHostFor_UnknownEngines_ReturnsNull(string? engineId)
    {
        Assert.Null(SearchEngineHosts.PrimaryHostFor(engineId));
    }

    [Fact]
    public void All_ContainsOnlyEngineAliases()
    {
        Assert.NotEmpty(SearchEngineHosts.All);
        Assert.Equal(SearchEngineHosts.All.Count, SearchEngineHosts.All.Distinct().Count());
        foreach (var host in SearchEngineHosts.All)
        {
            Assert.Equal(FaviconStore.NormalizeHost(host), host); // 已归一化
            Assert.True(host.EndsWith(".com"), host);
        }
    }
}
