using System;
using System.IO;
using System.Threading.Tasks;
using Apage.Core.Services;
using Xunit;

namespace Apage.Core.Tests;

/// <summary>
/// ShieldBypassPolicy：按站点放行必须以顶层页面主机为键，不能按被请求资源主机。
/// 该边界一旦倒退，用户关闭某站点防护后，页面内第三方广告仍会被拦截。
/// </summary>
public sealed class ShieldBypassPolicyTests : IDisposable
{
    private readonly string _dir;

    public ShieldBypassPolicyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "apage-shield-policy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void NoOverrides_DoesNotBypass()
    {
        Assert.False(ShieldBypassPolicy.IsBypassed("news.example", null, null));
    }

    [Fact]
    public async Task SiteOverride_BypassesTopLevelPage()
    {
        var shields = new SiteShieldStore(_dir);
        await shields.LoadAsync();
        await shields.SetAsync("news.example", disabled: true);

        Assert.True(ShieldBypassPolicy.IsBypassed("news.example", shields, null));
        Assert.True(ShieldBypassPolicy.IsBypassed("NEWS.EXAMPLE:443", shields, null));
    }

    [Fact]
    public async Task ResourceHostOverride_DoesNotBypassAnotherTopLevelPage()
    {
        // 用户在资源域 cdn.example 关防护，不代表访问 news.example 时应放行其广告。
        var shields = new SiteShieldStore(_dir);
        await shields.LoadAsync();
        await shields.SetAsync("cdn.example", disabled: true);

        Assert.False(ShieldBypassPolicy.IsBypassed("news.example", shields, null));
    }

    [Fact]
    public void AllowList_Wildcard_BypassesMatchingTopLevelSubdomain()
    {
        var path = Path.Combine(_dir, "allowlist.txt");
        File.WriteAllText(path, "*.internal.example");
        var allowList = new SiteAllowList(path);

        Assert.True(ShieldBypassPolicy.IsBypassed("app.internal.example", null, allowList));
        Assert.False(ShieldBypassPolicy.IsBypassed("public.example", null, allowList));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingTopLevelHost_NeverBypasses(string? host)
    {
        var allowList = new SiteAllowList(Path.Combine(_dir, "allowlist.txt"));
        File.WriteAllText(Path.Combine(_dir, "allowlist.txt"), "example.com");

        Assert.False(ShieldBypassPolicy.IsBypassed(host, null, allowList));
    }
}
