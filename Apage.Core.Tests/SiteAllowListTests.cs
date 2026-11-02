using System;
using System.IO;
using System.Text;
using Apage.Core.Services;
using Xunit;

namespace Apage.Core.Tests;

/// <summary>
/// SiteAllowList：便携站点允许清单（R7）。
/// 把守三条：① 通配/点前缀/精确匹配语义正确；② 文件缺失/损坏回落空清单不抛；
/// ③ 非法行与模糊模式（*example.com）拒绝——清单是用户可审计的明文配置，
/// 语义必须可预测，不做隐藏后门。
/// </summary>
public sealed class SiteAllowListTests : IDisposable
{
    private readonly string _dir;

    public SiteAllowListTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "apage-allowlist-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private SiteAllowList WriteList(string content)
    {
        var path = Path.Combine(_dir, "allowlist.txt");
        File.WriteAllText(path, content, Encoding.UTF8);
        return new SiteAllowList(path);
    }

    [Fact]
    public void MissingFile_FallsBackToEmpty()
    {
        var list = new SiteAllowList(Path.Combine(_dir, "nope.txt"));

        Assert.False(list.HasEntries);
        Assert.False(list.IsShieldsDisabled("anything.example"));
    }

    [Fact]
    public void EmptyAndCommentOnlyFile_IsEmpty()
    {
        var list = WriteList("# 只是注释\r\n\r\n   \r\n# 又一行注释");

        Assert.False(list.HasEntries);
        Assert.False(list.IsShieldsDisabled("example.com"));
    }

    [Fact]
    public void ExactHost_Matches()
    {
        var list = WriteList("example.com");

        Assert.True(list.IsShieldsDisabled("example.com"));
        Assert.True(list.IsShieldsDisabled("EXAMPLE.com"));
        Assert.True(list.IsShieldsDisabled("example.com:443"));
        Assert.True(list.IsShieldsDisabled("example.com."));
        Assert.False(list.IsShieldsDisabled("www.example.com"));
        Assert.False(list.IsShieldsDisabled("notexample.com"));
    }

    [Fact]
    public void StarPrefix_MatchesSubdomainsOnly()
    {
        var list = WriteList("*.example.com");

        Assert.True(list.IsShieldsDisabled("www.example.com"));
        Assert.True(list.IsShieldsDisabled("a.b.example.com"));
        Assert.False(list.IsShieldsDisabled("example.com")); // 不含 apex 本身
    }

    [Fact]
    public void DotPrefix_IsEquivalentToStar()
    {
        var list = WriteList(".example.com");

        Assert.True(list.IsShieldsDisabled("www.example.com"));
        Assert.False(list.IsShieldsDisabled("example.com"));
    }

    [Fact]
    public void InlineComment_IsStripped()
    {
        var list = WriteList("example.com # 企内网误伤\r\n*.internal.lan # 离线机");

        Assert.True(list.IsShieldsDisabled("example.com"));
        Assert.True(list.IsShieldsDisabled("host.internal.lan"));
    }

    [Fact]
    public void IllegalLines_AreSkipped()
    {
        var list = WriteList("has space.com\r\n../evil\r\n*ambiguous.com\r\ngood.com");

        Assert.True(list.IsShieldsDisabled("good.com"));
        Assert.False(list.IsShieldsDisabled("has space.com"));
        Assert.False(list.IsShieldsDisabled("ambiguous.com"));
        Assert.False(list.IsShieldsDisabled("www.ambiguous.com"));
    }

    [Fact]
    public void NullAndIllegalHost_ReturnsFalse()
    {
        var list = WriteList("example.com");

        Assert.False(list.IsShieldsDisabled(null));
        Assert.False(list.IsShieldsDisabled(""));
        Assert.False(list.IsShieldsDisabled("bad host"));
    }

    [Fact]
    public void Snapshot_NormalizesPatterns()
    {
        var list = WriteList("b.example\r\n*.a.example\r\n# 注释");

        var snap = list.Snapshot();

        Assert.Contains("b.example", snap);
        Assert.Contains("*.a.example", snap);
    }

    [Fact]
    public void Load_IsIdempotent()
    {
        var list = WriteList("example.com");
        Assert.True(list.IsShieldsDisabled("example.com"));

        list.Load(); // 二次加载不得清空

        Assert.True(list.IsShieldsDisabled("example.com"));
    }
}
