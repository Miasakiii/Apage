#nullable enable
using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Apage.Portable.Services
{
    /// <summary>
    /// 一次性引擎图标抓取。这是除手动规则更新之外**唯一**被批准的出站路径
    /// （build\check-redlines.ps1 文件级豁免），调用方必须先通过
    /// <see cref="Apage.Core.Services.SearchEngineHosts"/> 白名单与用户开关校验。
    ///
    /// 约束：仅 HTTPS、不跟随重定向、单次 ≤64KB、6 秒超时、只接受图片魔数，
    /// 并且一律重编码为 32px PNG（页面侧统一按 png data URL 使用）。
    /// 任何失败都返回 null——图标是装饰性数据，不能影响浏览。
    /// </summary>
    internal static class FaviconFetcher
    {
        private const int MaxBytes = 64 * 1024;
        private const int TargetSize = 32;
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);

        /// <summary>抓取指定主机的 favicon.ico 并归一化为 PNG；失败返回 null。</summary>
        public static Task<byte[]?> FetchAsync(string host, CancellationToken cancellationToken) =>
            Task.Run<byte[]?>(() => Fetch(host, cancellationToken), cancellationToken);

        private static byte[]? Fetch(string host, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var request = (HttpWebRequest)WebRequest.Create(new Uri("https://" + host + "/favicon.ico"));
            request.Method = "GET";
            request.UserAgent = "Apage/0.1 (engine icon)";
            request.Accept = "image/*";
            request.AllowAutoRedirect = false; // 不跟随跳转：避免被引到明文或其他主机
            request.Timeout = (int)RequestTimeout.TotalMilliseconds;
            request.ReadWriteTimeout = (int)RequestTimeout.TotalMilliseconds;

            using var registration = cancellationToken.Register(() =>
            {
                try { request.Abort(); }
                catch { /* Abort 竞态可忽略 */ }
            });

            try
            {
                using var response = (HttpWebResponse)request.GetResponse();
                if (response.StatusCode != HttpStatusCode.OK)
                    return null;
                if (response.ContentLength > MaxBytes)
                    return null;

                using var stream = response.GetResponseStream();
                if (stream == null)
                    return null;

                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                var total = 0;
                int read;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    total += read;
                    if (total > MaxBytes)
                        return null;
                    buffer.Write(chunk, 0, read);
                }

                return NormalizeToPng(buffer.ToArray());
            }
            catch (WebException)
            {
                return null; // 404 / 超时 / 证书问题：都当“没有图标”
            }
            catch (IOException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        // 只认图片魔数：有些站点对任意路径都返回 200 + HTML，不能当图标存。
        private static bool LooksLikeImage(byte[] raw)
        {
            if (raw.Length < 4)
                return false;

            if (raw[0] == 0x00 && raw[1] == 0x00 && raw[2] == 0x01 && raw[3] == 0x00)
                return true; // ICO
            if (raw[0] == 0x89 && raw[1] == 0x50 && raw[2] == 0x4E && raw[3] == 0x47)
                return true; // PNG
            if (raw[0] == 0xFF && raw[1] == 0xD8)
                return true; // JPEG
            if (raw[0] == (byte)'G' && raw[1] == (byte)'I' && raw[2] == (byte)'F')
                return true; // GIF
            if (raw.Length >= 12 &&
                raw[0] == (byte)'R' && raw[1] == (byte)'I' && raw[2] == (byte)'F' && raw[3] == (byte)'F' &&
                raw[8] == (byte)'W' && raw[9] == (byte)'E' && raw[10] == (byte)'B' && raw[11] == (byte)'P')
                return true; // WEBP
            return false;
        }

        // 统一转成 32px PNG：页面只处理 png data URL，也顺带压掉超大原图。
        private static byte[]? NormalizeToPng(byte[] raw)
        {
            if (!LooksLikeImage(raw))
                return null;

            try
            {
                BitmapFrame? frame = null;
                using (var stream = new MemoryStream(raw))
                {
                    var isIco = raw[0] == 0x00 && raw[1] == 0x00 && raw[2] == 0x01 && raw[3] == 0x00;
                    BitmapDecoder decoder = isIco
                        ? new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad)
                        : BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

                    foreach (var candidate in decoder.Frames)
                    {
                        if (frame == null || candidate.PixelWidth > frame.PixelWidth)
                            frame = candidate;
                    }
                }

                if (frame == null || frame.PixelWidth <= 0)
                    return null;

                BitmapSource source = frame;
                if (source.PixelWidth > TargetSize)
                {
                    var scale = (double)TargetSize / source.PixelWidth;
                    source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
                }

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                using var output = new MemoryStream();
                encoder.Save(output);
                return output.ToArray();
            }
            catch (Exception ex) when (ex is NotSupportedException || ex is ArgumentException ||
                                       ex is FileFormatException || ex is InvalidOperationException ||
                                       ex is OverflowException)
            {
                return null;
            }
        }
    }
}
