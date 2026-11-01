#nullable enable
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Apage.Core.Services;

namespace Apage.Portable.Services
{
    /// <summary>
    /// 规则下载客户端（A3）。**这是全仓唯一允许发起出站请求的文件**：
    /// 只在用户点击“立即更新规则”时被调用，没有定时器、没有启动时预取、没有遥测。
    /// 安全约束：仅 HTTPS（含重定向后的最终地址）、单源 8MB 上限、45 秒超时、
    /// 只读取文本、不发送任何本机信息（UA 仅含产品名与版本）。
    /// build/check-redlines.ps1 对本文件做精确豁免，其他任何文件出现网络类型都会失败。
    /// 实现用 HttpWebRequest（System.dll 内置），避免为一次手动操作额外引入 System.Net.Http 程序集。
    /// </summary>
    public sealed class RuleUpdateClient
    {
        public const int MaxBytesPerSource = 8 * 1024 * 1024;
        public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(45);

        /// <summary>下载一份规则文本。异常一律包装为可展示的中文失败原因。</summary>
        public Task<string> DownloadAsync(RuleUpdateSource source, CancellationToken cancellationToken)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            // 阻塞式 HTTP 放到线程池：手动操作，不占用 UI 线程，也不值得为它引入 HttpClient。
            return Task.Run(() => Download(source, cancellationToken), cancellationToken);
        }

        private static string Download(RuleUpdateSource source, CancellationToken cancellationToken)
        {
            if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("规则源必须是 HTTPS 地址。");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = "GET";
            request.UserAgent = "Apage/0.1 (manual rule update)";
            request.Accept = "text/plain";
            request.AllowAutoRedirect = true;
            request.MaximumAutomaticRedirections = 3;
            request.Timeout = (int)RequestTimeout.TotalMilliseconds;
            request.ReadWriteTimeout = (int)RequestTimeout.TotalMilliseconds;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            using var registration = cancellationToken.Register(() =>
            {
                try { request.Abort(); }
                catch { /* Abort 竞态可忽略 */ }
            });

            try
            {
                using var response = (HttpWebResponse)request.GetResponse();

                // 重定向后必须仍是 HTTPS，避免被降级到明文
                var finalUri = response.ResponseUri;
                if (finalUri == null ||
                    !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("规则源重定向到非 HTTPS 地址，已拒绝。");
                }

                if (response.ContentLength > MaxBytesPerSource)
                    throw new InvalidOperationException("规则文件超过大小上限。");

                using var stream = response.GetResponseStream()
                    ?? throw new InvalidOperationException("规则源返回空响应流。");
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                var total = 0;
                int read;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    total += read;
                    if (total > MaxBytesPerSource)
                        throw new InvalidOperationException("规则文件超过大小上限。");
                    buffer.Write(chunk, 0, read);
                }

                return Encoding.UTF8.GetString(buffer.ToArray());
            }
            catch (WebException ex) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("规则下载已取消。", ex, cancellationToken);
            }
            catch (WebException ex)
            {
                throw new InvalidOperationException("规则下载失败：" + ex.Status, ex);
            }
        }
    }
}
