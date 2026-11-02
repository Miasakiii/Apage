using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using Apage.Core.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Apage.Spikes.FaviconProbe;

/// <summary>
/// 真机探针：确认“站点自带图标”可以由 WebView2 自己提供，而不必联网抓 favicon。
/// 流程：加载 https://github.com → 等 FaviconChanged → GetFaviconAsync(Png)
///      → FaviconStore 落盘 → 读回 → data URL。
/// 输出：字节数、PNG 签名、存储文件名、读回一致、data URL 前缀，并把 PNG 写到指定路径供肉眼比对。
/// </summary>
internal static class Program
{
    private const string DefaultTargetUrl = "https://github.com";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(25);

    [STAThread]
    private static int Main(string[] args)
    {
        var targetUrl = args.Length > 1 ? args[1] : DefaultTargetUrl;
        var outputPath = args.Length > 0
            ? args[0]
            : Path.Combine(Path.GetTempPath(), "apage-favicon-probe.png");

        var exitCode = 3;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                exitCode = await RunAsync(outputPath, targetUrl);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("PROBE ERROR: " + ex);
                exitCode = 2;
            }
            finally
            {
                app.Shutdown(exitCode);
            }
        };
        app.Run();
        return exitCode;
    }

    private static async Task<int> RunAsync(string outputPath, string targetUrl)
    {
        Console.WriteLine("== FaviconProbe: WebView2 自带 favicon → 本地缓存 ==");
        Console.WriteLine("WebView2 Runtime: " + CoreWebView2Environment.GetAvailableBrowserVersionString());

        var root = Path.Combine(Path.GetTempPath(), "ApageFaviconProbe", Guid.NewGuid().ToString("N"));
        var userDataFolder = Path.Combine(root, "profile");
        Directory.CreateDirectory(userDataFolder);

        var window = new Window
        {
            Width = 960,
            Height = 640,
            Title = "Apage FaviconProbe",
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000, // 移出可视区，避免打断前台
            Top = -4000,
        };
        var webView = new WebView2();
        window.Content = webView;
        window.Show();

        var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
        await webView.EnsureCoreWebView2Async(environment);
        var core = webView.CoreWebView2;

        var icons = new List<byte[]>();
        var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        core.FaviconChanged += async (_, _) =>
        {
            try
            {
                if (string.IsNullOrEmpty(core.FaviconUri))
                    return;
                using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                var bytes = ms.ToArray();
                if (bytes.Length == 0)
                    return;
                lock (icons) { icons.Add(bytes); }
                signal.TrySetResult(true);
            }
            catch (Exception ex)
            {
                Console.WriteLine("favicon-error: " + ex.GetType().Name);
            }
        };

        core.Navigate(targetUrl);
        var completed = await Task.WhenAny(signal.Task, Task.Delay(Timeout));
        if (completed != signal.Task)
        {
            Console.Error.WriteLine("FAIL: 超时未收到 favicon");
            window.Close();
            return 10;
        }

        byte[] png;
        lock (icons) { png = icons[icons.Count - 1]; }

        var host = new Uri(targetUrl).Host;
        var store = new FaviconStore(Path.Combine(root, "favicons"));
        await store.SaveAsync(host, png);

        var readBack = await store.ReadAsync(new[] { host.ToUpperInvariant() });
        var fileName = FaviconStore.FileNameFor(FaviconStore.NormalizeHost(host)!);
        var dataUrl = FaviconStore.ToDataUrl(png);

        var storedPath = Path.Combine(root, "favicons", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, png);

        var pngSignature =
            png.Length > 8 && png[0] == 0x89 && png[1] == 0x50 && png[2] == 0x4E && png[3] == 0x47;
        var readMatches = readBack.TryGetValue(host, out var roundTrip) &&
                          roundTrip.Length == png.Length;

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            host,
            bytes = png.Length,
            pngSignature,
            storedFile = fileName,
            storedOnDisk = File.Exists(storedPath),
            readBackMatches = readMatches,
            dataUrlLength = dataUrl.Length,
            dataUrlPrefix = dataUrl.Substring(0, Math.Min(22, dataUrl.Length)),
            frames = CountFrames(icons),
            savedTo = outputPath,
        }, new JsonSerializerOptions { WriteIndented = true }));

        window.Close();

        var ok = pngSignature && readMatches && dataUrl.StartsWith("data:image/png;base64,", StringComparison.Ordinal);
        Console.WriteLine(ok ? "PROBE PASSED" : "PROBE FAILED");
        return ok ? 0 : 11;
    }

    private static int CountFrames(List<byte[]> icons)
    {
        lock (icons) { return icons.Count; }
    }
}
