using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Apage.Core.Models;
using Apage.Core.Services;
using Xunit;

namespace Apage.Core.Tests;

/// <summary>
/// SiteShieldStore：按站点隐私覆盖（R5）。
/// 两个不变量由本测试把守：① 站点级关闭绝不回写全局设置（物理隔离 + 专测）；
/// ② 非法主机拒绝入表、损坏文件回落空表（覆盖是增强数据，丢失不阻断浏览）。
/// </summary>
public sealed class SiteShieldStoreTests : IDisposable
{
    private readonly string _dir;

    public SiteShieldStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "apage-site-shield-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private SiteShieldStore NewStore() => new SiteShieldStore(_dir);

    [Theory]
    [InlineData("Example.COM", "example.com")]
    [InlineData("example.com:443", "example.com")]
    [InlineData("example.com.", "example.com")]
    [InlineData(" mixed-case.net ", "mixed-case.net")]
    public void NormalizeHost_NormalizesCasePortAndTrailingDot(string input, string expected)
    {
        Assert.Equal(expected, SiteShieldState.NormalizeHost(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space.com")]
    [InlineData("a:b:c")]
    [InlineData("../evil")]
    public void NormalizeHost_RejectsIllegal(string? input)
    {
        Assert.Null(SiteShieldState.NormalizeHost(input));
    }

    [Fact]
    public async Task SetAndQuery_RoundTripsThroughFile()
    {
        var store = NewStore();
        await store.LoadAsync();

        Assert.True(await store.SetAsync("news.example.com", disabled: true, note: "误伤"));
        Assert.True(store.IsDisabled("news.example.com"));
        Assert.True(store.IsDisabled("NEWS.example.com:443"));
        Assert.Equal(1, store.Count);

        // 新实例从盘加载：持久化生效
        var reloaded = new SiteShieldStore(_dir);
        await reloaded.LoadAsync();
        Assert.True(reloaded.IsDisabled("news.example.com"));
    }

    [Fact]
    public async Task Set_DisabledFalse_RemovesOverride()
    {
        var store = NewStore();
        await store.LoadAsync();
        await store.SetAsync("a.example", disabled: true);

        await store.SetAsync("a.example", disabled: false);

        Assert.False(store.IsDisabled("a.example"));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task Set_IllegalHost_Rejected()
    {
        var store = NewStore();
        await store.LoadAsync();

        Assert.False(await store.SetAsync("bad host", disabled: true));
        Assert.False(await store.SetAsync("", disabled: true));
        Assert.False(await store.SetAsync(null, disabled: true));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task Set_NeverWritesGlobalSettingsFile()
    {
        // 红线专测：站点覆盖必须与 settings.json 物理隔离，
        // 覆盖任何主机都不得在数据目录产生或修改 settings.json
        var store = NewStore();
        await store.LoadAsync();
        await store.SetAsync("broken.example", disabled: true);

        Assert.False(File.Exists(Path.Combine(_dir, "settings.json")));
        Assert.True(File.Exists(store.FilePath));
    }

    [Fact]
    public async Task Load_CorruptedFile_FallsBackToEmpty()
    {
        File.WriteAllText(Path.Combine(_dir, "site-shields.json"), "{ this is not json !!!",
            Encoding.UTF8);

        var store = new SiteShieldStore(_dir);
        await store.LoadAsync(); // 不得抛

        Assert.Equal(0, store.Count);
        Assert.False(store.IsDisabled("anything.example"));
    }

    [Fact]
    public async Task Load_MissingFile_FallsBackToEmpty()
    {
        var store = new SiteShieldStore(_dir);
        await store.LoadAsync();

        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task Snapshot_IsOrderedByHost()
    {
        var store = NewStore();
        await store.LoadAsync();
        await store.SetAsync("zzz.example", disabled: true);
        await store.SetAsync("aaa.example", disabled: true);

        var snap = store.Snapshot();

        Assert.Equal("aaa.example", snap[0].Host);
        Assert.Equal("zzz.example", snap[1].Host);
    }

    [Fact]
    public void IsDisabled_UnknownHost_ReturnsFalse()
    {
        var store = NewStore();
        Assert.False(store.IsDisabled("never-seen.example"));
        Assert.False(store.IsDisabled(null));
        Assert.False(store.IsDisabled("bad host"));
    }

    [Fact]
    public async Task Load_IsIdempotent()
    {
        var store = NewStore();
        await store.LoadAsync();
        await store.SetAsync("once.example", disabled: true);
        await store.LoadAsync(); // 二次加载不得清空已加载状态

        Assert.True(store.IsDisabled("once.example"));
    }
}
