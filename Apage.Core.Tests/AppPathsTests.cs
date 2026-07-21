using System;
using System.IO;
using Apage.Core.Configuration;
using Xunit;

namespace Apage.Core.Tests;

/// <summary>
/// AppPaths 便携目录解析与 R7 缓存策略（默认宿主机 %TEMP%，纯便携才落 exe 同级 cache/）。
/// 只断言路径组合与开关切换，不触碰文件系统（不调用 EnsureCreated）。
/// </summary>
public class AppPathsTests
{
    [Fact]
    public void DataAndWallpapers_AreUnderBaseDirectory()
    {
        Assert.Equal(Path.Combine(AppPaths.BaseDirectory, "data"), AppPaths.DataDirectory);
        Assert.Equal(Path.Combine(AppPaths.BaseDirectory, "wallpapers"), AppPaths.WallpapersDirectory);
    }

    [Fact]
    public void TempCacheDirectory_LivesUnderSystemTemp()
    {
        var expected = Path.Combine(Path.GetTempPath(), "Apage", "cache");
        Assert.Equal(expected, AppPaths.TempCacheDirectory);
    }

    [Fact]
    public void CacheDirectory_FollowsPortableCacheToggle()
    {
        var original = AppPaths.PortableCache;
        try
        {
            // 默认（false）：R7 走宿主机 %TEMP%
            AppPaths.PortableCache = false;
            Assert.Equal(AppPaths.TempCacheDirectory, AppPaths.CacheDirectory);

            // 纯便携（true）：落 exe 同级 cache/
            AppPaths.PortableCache = true;
            Assert.Equal(AppPaths.PortableCacheDirectory, AppPaths.CacheDirectory);
            Assert.Equal(Path.Combine(AppPaths.BaseDirectory, "cache"), AppPaths.CacheDirectory);
        }
        finally
        {
            AppPaths.PortableCache = original;
        }
    }
}
