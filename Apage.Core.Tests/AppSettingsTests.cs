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

        Assert.True(s.SendDoNotTrack);
        Assert.True(s.SendGPC);
        Assert.True(s.WebRTCIPProtection);
    }

    [Fact]
    public void Defaults_FetchEngineIcons_IsOn()
    {
        // 默认开：新标签页首屏四引擎图标一次到位。
        // 请求范围被 SearchEngineHosts 白名单锁死，且设置里可关闭（见 SearchEngineHostsTests）。
        var s = new AppSettings();

        Assert.True(s.FetchEngineIcons);
    }

    [Fact]
    public void Defaults_PortableCache_IsOff()
    {
        // 缓存默认写宿主机 %TEMP%（护 U 盘），非纯便携。
        var s = new AppSettings();

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
