#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Apage.Core.Services;

/// <summary>
/// 站点图标本地缓存（新标签页快捷方式与已访问站点复用）。
///
/// **数据来源只有 WebView2 自带的 favicon 管线**：用户真正访问某个页面时，内核本来就会取回该站图标，
/// 这里只是把同一份字节落盘复用。不请求 favicon.ico、不调用第三方图标服务、不做远程代理，
/// 因此「未访问过的站点没有图标」是设计结果，而不是缺陷。
///
/// 文件名 = SHA-1(host) 十六进制，避免主机名中的特殊字符进入文件系统；
/// 目录按最后写入时间裁剪，超出 <see cref="MaxEntries"/> 删最旧，避免无限增长。
/// 所有 IO 失败都被吞掉：图标是装饰性数据，坏一个不能让浏览或新标签页出错。
/// </summary>
public sealed class FaviconStore
{
    /// <summary>缓存条目上限（每条约 1–8KB，256 条约 1–2MB，位于数据目录不在包里）。</summary>
    public const int MaxEntries = 256;

    private readonly string _directory;
    private readonly object _gate = new();

    /// <param name="directory">图标目录（如 {数据目录}\favicons）。不存在时首次写入自动创建。</param>
    public FaviconStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("图标目录不能为空。", nameof(directory));
        _directory = directory;
    }

    /// <summary>
    /// 保存某个主机的图标（PNG 字节）。同一主机重复保存直接覆盖。
    /// 仅在字节非空且主机合法时写入。
    /// </summary>
    public Task SaveAsync(string? host, byte[]? png)
    {
        var key = NormalizeHost(host);
        if (key == null || png == null || png.Length == 0)
            return Task.CompletedTask;

        return Task.Run(() =>
        {
            try
            {
                lock (_gate)
                {
                    Directory.CreateDirectory(_directory);
                    var path = Path.Combine(_directory, FileNameFor(key));
                    File.WriteAllBytes(path, png);
                    PruneCore();
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is NotSupportedException || ex is ArgumentException)
            {
                // 图标是装饰性数据：写不进磁盘就不写，下一次访问还会再拿到
            }
        });
    }

    /// <summary>
    /// 批量读取指定主机的图标字节。缺失的主机不出现在结果里（调用方回落字标）。
    /// <paramref name="hosts"/> 会被归一化并去重。
    /// </summary>
    public Task<Dictionary<string, byte[]>> ReadAsync(IEnumerable<string>? hosts)
    {
        var keys = new List<string>();
        if (hosts != null)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var host in hosts)
            {
                var key = NormalizeHost(host);
                if (key != null && seen.Add(key))
                    keys.Add(key);
            }
        }

        return Task.Run(() =>
        {
            var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            if (keys.Count == 0)
                return result;

            try
            {
                lock (_gate)
                {
                    foreach (var key in keys)
                    {
                        var path = Path.Combine(_directory, FileNameFor(key));
                        if (!File.Exists(path))
                            continue;
                        var bytes = File.ReadAllBytes(path);
                        if (bytes.Length > 0)
                            result[key] = bytes;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is NotSupportedException || ex is ArgumentException)
            {
                // 读不到就当作没有图标
            }

            return result;
        });
    }

    /// <summary>把图标包装成 data URL（新标签页是 file:// 页面，避免跨 scheme 子资源策略问题）。</summary>
    public static string ToDataUrl(byte[] png) =>
        "data:image/png;base64," + Convert.ToBase64String(png);

    /// <summary>
    /// 主机归一化：小写、去掉端口与首尾空白；只接受常规主机名字符，
    /// 避免把 canary 之类奇怪输入写进文件名。
    /// </summary>
    public static string? NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return null;

        var value = host!.Trim().ToLowerInvariant();

        // 端口只在“冒号后全是数字”时才剥；否则视为非法主机名（如 "a:b:c"），
        // 避免把残缺前缀当成合法主机写进文件名。
        var colon = value.IndexOf(':');
        if (colon >= 0)
        {
            var port = value.Substring(colon + 1);
            if (port.Length == 0 || !IsAllDigits(port))
                return null;
            value = value.Substring(0, colon);
        }

        while (value.EndsWith(".", StringComparison.Ordinal))
            value = value.Substring(0, value.Length - 1);

        if (value.Length == 0 || value.Length > 255)
            return null;
        foreach (var c in value)
        {
            var ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '-' || c == '_';
            if (!ok)
                return null;
        }
        return value;
    }

    private static bool IsAllDigits(string text)
    {
        foreach (var c in text)
        {
            if (c < '0' || c > '9')
                return false;
        }
        return true;
    }

    /// <summary>主机 → 文件名（SHA-1 十六进制 + .png）。</summary>
    public static string FileNameFor(string normalizedHost)
    {
        using var sha1 = SHA1.Create();
        var hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(normalizedHost));
        var sb = new StringBuilder(hash.Length * 2 + 4);
        foreach (var b in hash)
            sb.Append(b.ToString("x2"));
        sb.Append(".png");
        return sb.ToString();
    }

    /// <summary>按最后写入时间裁剪；调用方须已持有 <see cref="_gate"/>。</summary>
    private void PruneCore()
    {
        var files = new DirectoryInfo(_directory).GetFiles("*.png");
        if (files.Length <= MaxEntries)
            return;

        Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
        var removeCount = files.Length - MaxEntries;
        for (var i = 0; i < removeCount; i++)
        {
            try { files[i].Delete(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // 被占用就留到下一轮
            }
        }
    }
}
