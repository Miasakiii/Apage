using System;
using System.IO;
using System.Threading.Tasks;
using Apage.Core.Services;
using Xunit;

namespace Apage.Core.Tests;

/// <summary>
/// SettingsService 持久化：原子写、缺失文件回落默认、损坏 JSON 回落默认
/// （便携场景宁可重置设置，也不能启动失败）。用临时目录隔离，测后清理。
/// </summary>
public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public SettingsServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ApageTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "settings.json");
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
        Assert.Throws<ArgumentException>(() => new SettingsService(path!));
    }

    [Fact]
    public async Task LoadAsync_MissingFile_ReturnsPrivacyDefaults()
    {
        var svc = new SettingsService(_file);

        var loaded = await svc.LoadAsync();

        Assert.True(loaded.BlockThirdPartyCookies);
        Assert.Equal("bing", loaded.SearchEngine);
        Assert.False(File.Exists(_file)); // 加载缺失文件不应写盘
    }

    [Fact]
    public async Task Save_Then_Load_RoundTrips()
    {
        var svc = new SettingsService(_file);
        svc.Current.SearchEngine = "duckduckgo";
        svc.Current.Theme = "dark";
        svc.Current.ClearOnExit = true;

        await svc.SaveAsync();
        Assert.True(File.Exists(_file));

        var reloaded = await new SettingsService(_file).LoadAsync();
        Assert.Equal("duckduckgo", reloaded.SearchEngine);
        Assert.Equal("dark", reloaded.Theme);
        Assert.True(reloaded.ClearOnExit);
    }

    [Fact]
    public async Task LoadAsync_CorruptedJson_FallsBackToDefaults()
    {
        await File.WriteAllTextAsync(_file, "{ this is not valid json ]");

        var loaded = await new SettingsService(_file).LoadAsync();

        // 损坏文件不得导致抛异常，回落默认（隐私基线）
        Assert.True(loaded.BlockThirdPartyCookies);
        Assert.Equal("bing", loaded.SearchEngine);
    }

    [Fact]
    public async Task SaveAsync_CreatesFile_AndLeavesNoTempResidue()
    {
        var svc = new SettingsService(_file);

        await svc.SaveAsync();

        Assert.True(File.Exists(_file));
        Assert.False(File.Exists(_file + ".tmp")); // 原子写后不应残留 .tmp
    }
}
