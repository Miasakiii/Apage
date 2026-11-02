using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Apage.Core.Services;
using Xunit;

namespace Apage.Core.Tests;

/// <summary>
/// FaviconStore：本地图标缓存。重点是主机归一化（决定文件名与查询键）与
/// “缺失即回落”的读取语义——缓存坏了不能让新标签页报错。
/// </summary>
public sealed class FaviconStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apage-favicon-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData("Example.COM", "example.com")]
    [InlineData("  news.ycombinator.com  ", "news.ycombinator.com")]
    [InlineData("example.com:8080", "example.com")]
    [InlineData("example.com.", "example.com")]
    [InlineData("xn--fiqs8s.example", "xn--fiqs8s.example")]
    public void NormalizeHost_Canonicalizes(string input, string expected)
    {
        Assert.Equal(expected, FaviconStore.NormalizeHost(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad host")]
    [InlineData("evil/../path")]
    [InlineData("a:b:c")]
    public void NormalizeHost_RejectsUnusable(string? input)
    {
        Assert.Null(FaviconStore.NormalizeHost(input));
    }

    [Fact]
    public async Task Save_ThenRead_ReturnsSameBytes()
    {
        var store = new FaviconStore(_dir);
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };

        await store.SaveAsync("example.com", png);
        var icons = await store.ReadAsync(new[] { "EXAMPLE.com" });

        Assert.Single(icons);
        Assert.Equal(png, icons["example.com"]);
    }

    [Fact]
    public async Task Read_UnknownHost_ReturnsEmptyWithoutCreatingDirectory()
    {
        var store = new FaviconStore(_dir);

        var icons = await store.ReadAsync(new[] { "never-visited.example" });

        Assert.Empty(icons);
        Assert.False(Directory.Exists(_dir)); // 只读不建目录
    }

    [Fact]
    public async Task Save_SameHostTwice_OverwritesInsteadOfDuplicating()
    {
        var store = new FaviconStore(_dir);

        await store.SaveAsync("example.com", new byte[] { 1, 2, 3 });
        await store.SaveAsync("example.com", new byte[] { 9, 9 });

        var icons = await store.ReadAsync(new[] { "example.com" });
        Assert.Equal(new byte[] { 9, 9 }, icons["example.com"]);
        Assert.Single(Directory.GetFiles(_dir, "*.png"));
    }

    [Fact]
    public async Task Save_EmptyBytes_IsNoOp()
    {
        var store = new FaviconStore(_dir);

        await store.SaveAsync("example.com", Array.Empty<byte>());
        await store.SaveAsync("example.com", null);
        await store.SaveAsync(null, new byte[] { 1 });

        Assert.Empty(await store.ReadAsync(new[] { "example.com" }));
    }

    [Fact]
    public async Task Read_DeduplicatesAndIgnoresInvalidHosts()
    {
        var store = new FaviconStore(_dir);
        await store.SaveAsync("example.com", new byte[] { 7 });

        var icons = await store.ReadAsync(new List<string>
        {
            "example.com", "EXAMPLE.COM", "bad host", "", "example.com.",
        });

        Assert.Single(icons);
    }

    [Fact]
    public void FileNameFor_IsDeterministicAndHostUnique()
    {
        var a = FaviconStore.FileNameFor("example.com");
        var b = FaviconStore.FileNameFor("example.com");
        var c = FaviconStore.FileNameFor("example.org");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.EndsWith(".png", a);
        Assert.DoesNotContain("/", a); // 主机名不能变成路径
    }

    [Fact]
    public void ToDataUrl_ProducesPngDataUrl()
    {
        var url = FaviconStore.ToDataUrl(new byte[] { 1, 2, 3 });

        Assert.StartsWith("data:image/png;base64,", url);
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3 }), url.Substring("data:image/png;base64,".Length));
    }

    [Fact]
    public async Task Save_PrunesOldestBeyondLimit()
    {
        var store = new FaviconStore(_dir);
        var total = FaviconStore.MaxEntries + 7;

        for (var i = 0; i < total; i++)
            await store.SaveAsync("host" + i + ".example", new byte[] { 1 });

        var files = Directory.GetFiles(_dir, "*.png");
        Assert.Equal(FaviconStore.MaxEntries, files.Length);
        // 最早写入的那批应当已被清掉
        var oldest = FaviconStore.FileNameFor("host0.example");
        Assert.DoesNotContain(Path.Combine(_dir, oldest), files);
    }
}
