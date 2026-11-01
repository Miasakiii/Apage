using Apage.Core.Services;
using Xunit;

namespace Apage.Core.Tests;

/// <summary>
/// ThirdPartyDetector 第三方判定（A2 / S0 §4.1）：
/// 基于随包 PSL 比较 registrable domain；多级后缀、IDN、同域/子域、非法输入与安全回退。
/// </summary>
public sealed class ThirdPartyDetectorTests
{
    private static readonly ThirdPartyDetector Detector =
        new ThirdPartyDetector(PublicSuffixList.LoadEmbedded());

    [Theory]
    [InlineData("https://example.com/", "https://example.com/api", false)]
    [InlineData("https://www.example.com/", "https://example.com/", false)]
    [InlineData("https://EXAMPLE.COM/", "https://example.com/", false)]
    [InlineData("http://example.com/", "https://example.com/x", false)]
    [InlineData("https://example.com:8080/", "https://example.com:9090/", false)]
    [InlineData("https://a.example.com/", "https://b.example.com/res.js", false)]
    [InlineData("https://a.foo.co.uk/", "https://b.foo.co.uk/res.js", false)]
    [InlineData("https://example.com/", "https://other.com/", true)]
    [InlineData("https://example.com/", "https://tracker.other.org/", true)]
    [InlineData("https://foo.co.uk/", "https://bar.co.uk/", true)]                  // PSL 修正：不再是漏判
    [InlineData("https://example.com/", "https://sub.example.com.evil.com/", true)] // 伪装子域，实为 evil.com
    [InlineData("https://www.食狮.com.cn/", "https://api.食狮.com.cn/", false)]      // IDN 同 registrable domain
    public void IsThirdParty_ComparesPslRegistrableDomain(string pageUrl, string requestUrl, bool expected)
    {
        Assert.Equal(expected, Detector.IsThirdParty(pageUrl, requestUrl));
    }

    [Fact]
    public void Fallback_RemainsSafe_WhenPslUnavailable()
    {
        // 回退算法对 co.uk 仍会漏判（false），方向是漏剥不是误剥；生产启动会记录 PSL 加载失败。
        Assert.False(ThirdPartyDetector.Fallback.IsThirdParty(
            "https://foo.co.uk/", "https://bar.co.uk/"));
        Assert.True(ThirdPartyDetector.Fallback.IsThirdParty(
            "https://example.com/", "https://tracker.other.org/"));
    }

    [Theory]
    [InlineData(null, "https://example.com/")]
    [InlineData("https://example.com/", null)]
    [InlineData("", "")]
    [InlineData("   ", "https://example.com/")]
    [InlineData("not a url", "https://example.com/")]
    [InlineData("https://example.com/", "::::")]
    [InlineData("ftp://example.com/", "https://example.com/")]
    [InlineData("file:///C:/page.html", "https://example.com/")]
    [InlineData("https://127.0.0.1/", "https://127.0.0.1/x")]
    [InlineData("https://localhost/", "https://localhost/x")]
    public void IsThirdParty_UncomparableInputs_ReturnsFalse(string? pageUrl, string? requestUrl)
    {
        Assert.False(Detector.IsThirdParty(pageUrl, requestUrl));
    }
}
