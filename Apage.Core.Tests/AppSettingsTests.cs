using Apage.Core.Models;
using Xunit;

namespace Apage.Core.Tests;

/// <summary>
/// AppSettings 默认值即「隐私默认全开」基线（docs/product-design.md §5.2，决策 #18）。
/// 这些默认值是产品红线，改动会改变 Apage 的隐私定位——用测试锁定。
/// </summary>
public class AppSettingsTests
{
    [Fact]
    public void Defaults_PrivacyProtections_AreOn()
    {
        var s = new AppSettings();

        Assert.True(s.BlockThirdPartyCookies);
        Assert.True(s.SendDoNotTrack);
        Assert.True(s.SendGPC);
        Assert.True(s.WebRTCIPProtection);
    }

    [Fact]
    public void Defaults_TelemetryLeakingOptions_AreOff()
    {
        var s = new AppSettings();

        // 联网搜索建议默认关：开启会把击键发给搜索引擎
        Assert.False(s.OnlineSearchSuggestions);
        // 退出清理默认关
        Assert.False(s.ClearOnExit);
        // 缓存默认写宿主机 %TEMP%（护 U 盘），非纯便携
        Assert.False(s.PortableCache);
    }

    [Fact]
    public void Defaults_SearchAndTheme_HaveExpectedValues()
    {
        var s = new AppSettings();

        Assert.Equal("bing", s.SearchEngine);
        Assert.Equal("system", s.Theme);
    }
}
