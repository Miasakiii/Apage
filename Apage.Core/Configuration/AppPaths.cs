#nullable enable
using System;
using System.IO;

namespace Apage.Core.Configuration;

/// <summary>
/// 便携目录解析：data/、wallpapers/ 固定位于 exe 同级，数据跟程序走。
/// 缓存目录遵循风险 R7 策略：默认放宿主机 %TEMP%\Apage\cache（避免 WebView2 海量
/// 小文件写伤 U盘、拖慢速度）；纯便携模式（<see cref="PortableCache"/> = true）放 exe 同级 cache/。
/// </summary>
public static class AppPaths
{
    private static readonly Lazy<string> BaseDirectoryLazy = new Lazy<string>(ResolveBaseDirectory);

    /// <summary>程序根目录（便携 exe 所在目录）。</summary>
    public static string BaseDirectory => BaseDirectoryLazy.Value;

    /// <summary>用户数据目录（exe 同级 data/）。</summary>
    public static string DataDirectory => Path.Combine(BaseDirectory, "data");

    /// <summary>壁纸目录（exe 同级 wallpapers/）。</summary>
    public static string WallpapersDirectory => Path.Combine(BaseDirectory, "wallpapers");

    /// <summary>纯便携模式缓存目录（exe 同级 cache/）。</summary>
    public static string PortableCacheDirectory => Path.Combine(BaseDirectory, "cache");

    /// <summary>宿主机缓存目录（%TEMP%\Apage\cache，R7 默认）。</summary>
    public static string TempCacheDirectory => Path.Combine(Path.GetTempPath(), "Apage", "cache");

    /// <summary>
    /// 纯便携模式开关（对应设置项 PortableCache）。默认 false：缓存写宿主机 %TEMP%。
    /// 由设置服务加载后赋值。
    /// </summary>
    public static bool PortableCache { get; set; }

    /// <summary>当前生效的缓存目录（R7：默认宿主机 %TEMP%，纯便携模式为 exe 同级 cache/）。</summary>
    public static string CacheDirectory => PortableCache ? PortableCacheDirectory : TempCacheDirectory;

    /// <summary>确保 data/、wallpapers/ 及当前生效的缓存目录存在。</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(WallpapersDirectory);
        Directory.CreateDirectory(CacheDirectory);
    }

    private static string ResolveBaseDirectory()
    {
        // 优先入口程序集位置（便携 exe 同级）；单测等无入口程序集场景退化为 AppDomain.BaseDirectory
        try
        {
            var location = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrEmpty(location))
            {
                var dir = Path.GetDirectoryName(location);
                if (!string.IsNullOrEmpty(dir))
                    return dir;
            }
        }
        catch
        {
            // 忽略，走兜底
        }

        return AppDomain.CurrentDomain.BaseDirectory
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
