using System;
using System.IO;
using System.Threading.Tasks;
using Apage.Core.Models;
using Apage.Core.Services;
using Xunit;

namespace Apage.Core.Tests;

/// <summary>
/// SessionService：原子写、缺失/损坏回落空会话、URL 可恢复过滤、索引收敛（纯逻辑）。
/// 用临时目录隔离，测后清理。
/// </summary>
public sealed class SessionServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public SessionServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ApageTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "session.json");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* 清理失败不影响测试结论 */ }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsEmptyPath(string? path)
    {
        Assert.Throws<ArgumentException>(() => new SessionService(path!));
    }

    [Fact]
    public async Task LoadAsync_MissingFile_ReturnsEmptySession()
    {
        var svc = new SessionService(_file);

        var loaded = await svc.LoadAsync();

        Assert.Empty(loaded.Tabs);
        Assert.Equal(0, loaded.ActiveIndex);
        Assert.False(File.Exists(_file)); // 读缺失文件不应写盘
    }

    [Fact]
    public async Task Save_Then_Load_RoundTrips()
    {
        var svc = new SessionService(_file);
        var state = new SessionState { ActiveIndex = 1 };
        state.Tabs.Add(new SessionTab { Url = "https://a.example/", Title = "A" });
        state.Tabs.Add(new SessionTab { Url = "https://b.example/", Title = "B" });

        await svc.SaveAsync(state);
        Assert.True(File.Exists(_file));

        var reloaded = await new SessionService(_file).LoadAsync();
        Assert.Equal(2, reloaded.Tabs.Count);
        Assert.Equal("https://b.example/", reloaded.Tabs[1].Url);
        Assert.Equal("B", reloaded.Tabs[1].Title);
        Assert.Equal(1, reloaded.ActiveIndex);
    }

    [Fact]
    public async Task LoadAsync_CorruptedJson_ReturnsEmptySession()
    {
        await File.WriteAllTextAsync(_file, "{ this is not valid json ]");

        var loaded = await new SessionService(_file).LoadAsync();

        Assert.Empty(loaded.Tabs); // 损坏不得抛异常，回落空会话
    }

    [Fact]
    public async Task SaveAsync_LeavesNoTempResidue()
    {
        var svc = new SessionService(_file);

        await svc.SaveAsync(new SessionState());

        Assert.True(File.Exists(_file));
        Assert.False(File.Exists(_file + ".tmp")); // 原子写后不应残留 .tmp
    }

    [Fact]
    public async Task SaveAsync_NullState_Throws()
    {
        var svc = new SessionService(_file);
        await Assert.ThrowsAsync<ArgumentNullException>(() => svc.SaveAsync(null!));
    }

    [Theory]
    [InlineData("https://ok.example/", true)]
    [InlineData("http://ok.example/", true)]
    [InlineData("file:///c:/x.html", true)]
    [InlineData("about:blank", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void IsRestorableUrl_FiltersNonHttp(string? url, bool expected)
    {
        Assert.Equal(expected, SessionService.IsRestorableUrl(url));
    }

    [Fact]
    public void Normalize_DropsUnrestorableTabs_AndClampsIndex()
    {
        var state = new SessionState { ActiveIndex = 9 };
        state.Tabs.Add(new SessionTab { Url = "about:blank", Title = "new" });
        state.Tabs.Add(new SessionTab { Url = "https://keep.example/", Title = "keep" });
        state.Tabs.Add(new SessionTab { Url = "", Title = "empty" });

        var norm = SessionService.Normalize(state);

        Assert.Single(norm.Tabs);
        Assert.Equal("https://keep.example/", norm.Tabs[0].Url);
        Assert.Equal(0, norm.ActiveIndex); // 9 夹到 [0,0]
    }

    [Fact]
    public void Normalize_EmptyOrNull_ReturnsEmpty()
    {
        Assert.Empty(SessionService.Normalize(null).Tabs);
        Assert.Empty(SessionService.Normalize(new SessionState()).Tabs);
    }
}
