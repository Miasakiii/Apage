using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Apage.Spikes.SingleFile
{
    /// <summary>
    /// 单文件场景的原生库引导：把嵌入的 x64 原生库释放到内容哈希隔离的缓存目录，
    /// 并在首次 P/Invoke 前按完整路径加载。缓存按哈希分目录，文件损坏会被重写；
    /// 程序目录保持只读也能运行。加载失败直接抛错，不静默降级。
    /// </summary>
    internal static class NativeBootstrap
    {
        private const string ResourcePrefix = "Apage.Native.win-x64.";
        private const string CacheRootName = "ApageNativeSpike";
        private const string WebView2Loader = "WebView2Loader.dll";
        private const string ESqlite3 = "e_sqlite3.dll";

        private static IntPtr _webView2LoaderHandle;
        private static IntPtr _eSqlite3Handle;

        public static void Initialize()
        {
            var cacheRoot = Path.Combine(Path.GetTempPath(), CacheRootName);
            _webView2LoaderHandle = LoadNative(cacheRoot, WebView2Loader);
            _eSqlite3Handle = LoadNative(cacheRoot, ESqlite3);
        }

        private static IntPtr LoadNative(string cacheRoot, string fileName)
        {
            var bytes = ReadResource(ResourcePrefix + fileName);
            var hash = ComputeHash(bytes);
            var cacheDirectory = Path.Combine(cacheRoot, hash.Substring(0, 16));
            Directory.CreateDirectory(cacheDirectory);

            var cachedPath = Path.Combine(cacheDirectory, fileName);
            if (!IsCacheValid(cachedPath, bytes.Length, hash))
                WriteCacheAtomically(cachedPath, bytes, hash);

            var handle = LoadLibraryW(cachedPath);
            if (handle == IntPtr.Zero)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "无法加载原生库：" + cachedPath);
            }

            return handle;
        }

        private static byte[] ReadResource(string resourceName)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                    throw new InvalidOperationException("缺少嵌入的原生库资源：" + resourceName);

                using (var buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    return buffer.ToArray();
                }
            }
        }

        private static string ComputeHash(byte[] bytes)
        {
            using (var sha256 = SHA256.Create())
            {
                return ToHex(sha256.ComputeHash(bytes));
            }
        }

        private static bool IsCacheValid(string path, int expectedLength, string expectedHash)
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length != expectedLength)
                    return false;

                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sha256 = SHA256.Create())
                {
                    return string.Equals(ToHex(sha256.ComputeHash(stream)), expectedHash, StringComparison.OrdinalIgnoreCase);
                }
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
                        // 另一端已完成写入则直接复用；否则清理损坏/残留文件后重试。
                        if (IsCacheValid(path, bytes.Length, expectedHash))
                            return;

                        try { File.Delete(path); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }

                        Thread.Sleep(50);
                    }
                }

                throw new IOException("无法写入原生库缓存：" + path);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch (IOException)
                {
                }
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
}
