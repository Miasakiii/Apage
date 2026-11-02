#nullable enable
using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Apage.Portable;

/// <summary>
/// 单文件发布的原生库引导（S1 已验证方案的正式接入）：
/// 将嵌入的 x64 WebView2Loader/e_sqlite3 释放到 %TEMP%\ApageNative\{内容哈希}\，
/// 在首次 P/Invoke 前以完整路径 LoadLibraryW 预加载。缓存损坏会重写；
/// 程序目录只读也可运行。旧哈希目录在本次加载后清理，失败不影响启动。
/// </summary>
internal static class NativeBootstrap
{
    private const string ResourcePrefix = "Apage.Native.win-x64.";
    private const string CacheRootName = "ApageNative";
    private const string WebView2Loader = "WebView2Loader.dll";
    private const string ESqlite3 = "e_sqlite3.dll";

    private static readonly object Gate = new object();
    private static bool _initialized;
    private static IntPtr _webView2LoaderHandle;
    private static IntPtr _eSqlite3Handle;

    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized)
                return;
            if (!Environment.Is64BitProcess)
                throw new PlatformNotSupportedException("Apage 仅支持 x64 单文件发布。");

            var cacheRoot = Path.Combine(Path.GetTempPath(), CacheRootName);
            Directory.CreateDirectory(cacheRoot);
            var webViewDirectory = LoadNative(cacheRoot, WebView2Loader, out _webView2LoaderHandle);
            var sqliteDirectory = LoadNative(cacheRoot, ESqlite3, out _eSqlite3Handle);
            CleanupOldCaches(cacheRoot, webViewDirectory, sqliteDirectory);
            _initialized = true;
        }
    }

    private static string LoadNative(string cacheRoot, string fileName, out IntPtr handle)
    {
        var bytes = ReadResource(ResourcePrefix + fileName);
        var hash = ComputeHash(bytes);
        var cacheDirectory = Path.Combine(cacheRoot, hash.Substring(0, 16));
        Directory.CreateDirectory(cacheDirectory);

        var cachedPath = Path.Combine(cacheDirectory, fileName);
        if (!IsCacheValid(cachedPath, bytes.Length, hash))
            WriteCacheAtomically(cachedPath, bytes, hash);

        handle = LoadLibraryW(cachedPath);
        if (handle == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "无法加载内置原生库：" + fileName);
        }
        return cacheDirectory;
    }

    private static byte[] ReadResource(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream == null)
            throw new InvalidOperationException("缺少嵌入的原生库资源：" + resourceName);

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string ComputeHash(byte[] bytes)
    {
        using var sha256 = SHA256.Create();
        return ToHex(sha256.ComputeHash(bytes));
    }

    private static bool IsCacheValid(string path, int expectedLength, string expectedHash)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length != expectedLength)
                return false;

            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sha256 = SHA256.Create();
            return string.Equals(ToHex(sha256.ComputeHash(stream)), expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void WriteCacheAtomically(string path, byte[] bytes, string expectedHash)
    {
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(tempPath, bytes);
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    File.Move(tempPath, path);
                    return;
                }
                catch (IOException)
                {
                    if (IsCacheValid(path, bytes.Length, expectedHash))
                        return;

                    try { File.Delete(path); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    Thread.Sleep(50);
                }
            }

            throw new IOException("无法写入原生库缓存。");
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch (IOException) { }
        }
    }

    private static void CleanupOldCaches(string cacheRoot, params string[] currentDirectories)
    {
        try
        {
            foreach (var directory in Directory.GetDirectories(cacheRoot))
            {
                var name = Path.GetFileName(directory);
                if (name.Length != 16 || Array.IndexOf(currentDirectories, directory) >= 0)
                    continue;

                try { Directory.Delete(directory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            // 清理失败只影响磁盘占用，不影响本次运行。
        }
    }

    private static string ToHex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (var value in bytes)
            builder.Append(value.ToString("x2"));
        return builder.ToString();
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string fileName);
}
