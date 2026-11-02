using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Apage.Spikes.CookieProbe;

/// <summary>
/// 真机探针：页面与图片同源，服务端先验证 Cookie 一定会到达；
/// 第二轮在 WebResourceRequested 中移除 Cookie 请求头，再观察服务器是否仍收到。
/// 同源请求刻意排除 SameSite / 第三方 Cookie 策略的干扰，只验证事件头部修改语义。
/// </summary>
internal static class Program
{
    private const string Host = "127.0.0.1";
    internal const string CookieName = "sid";
    internal const string CookieValue = "probe-cookie-value";

    [STAThread]
    private static int Main(string[] args)
    {
        var outputPath = ResolveOutputPath(args);
        var exitCode = 3;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                exitCode = await RunAsync(outputPath);
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

    private static async Task<int> RunAsync(string outputPath)
    {
        Console.WriteLine("== CookieProbe: WebResourceRequested Cookie 头移除语义 ==");
        Console.WriteLine("WebView2 Runtime: " + CoreWebView2Environment.GetAvailableBrowserVersionString());

        await using var server = LocalHttpServer.Start();
        Console.WriteLine($"本地 HTTP: http://{Host}:{server.Port}/");

        var userDataFolder = Path.Combine(Path.GetTempPath(), "ApageCookieProbe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userDataFolder);

        var window = new Window
        {
            Title = "Apage CookieProbe",
            Width = 640,
            Height = 480,
            Left = -10000,
            Top = -10000,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Opacity = 0.01,
        };
        var webView = new WebView2();
        window.Content = webView;

        string? eventCookie = null;
        string? baselineEventCookie = null;
        var removeMode = false;
        var removedCount = 0;

        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);

            // 先显示页面，保证 WebView2 有真实 HWND 与消息泵，再初始化内核。
            window.Show();
            await webView.EnsureCoreWebView2Async(environment);
            var core = webView.CoreWebView2;

            core.AddWebResourceRequestedFilter(
                "*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, e) =>
            {
                if (!e.Request.Uri.Contains("/probe?", StringComparison.Ordinal))
                    return;

                try
                {
                    var observed = e.Request.Headers.GetHeader("Cookie");
                    if (!removeMode)
                    {
                        baselineEventCookie = observed;
                        return;
                    }

                    eventCookie = observed;
                    if (!string.IsNullOrEmpty(eventCookie))
                    {
                        e.Request.Headers.RemoveHeader("Cookie");
                        removedCount++;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("读取/移除 Cookie 失败: " + ex.GetType().Name);
                }
            };

            await Task.Delay(300);

            var baselineRun = "baseline-" + Guid.NewGuid().ToString("N");
            removeMode = false;
            await NavigateAndWaitAsync(core, $"http://{Host}:{server.Port}/page?run={baselineRun}");
            var baselineHit = await server.WaitForProbeAsync(baselineRun, TimeSpan.FromSeconds(8));

            var removedRun = "removed-" + Guid.NewGuid().ToString("N");
            eventCookie = null;
            removeMode = true;
            removedCount = 0;
            await NavigateAndWaitAsync(core, $"http://{Host}:{server.Port}/page?run={removedRun}");
            var removedHit = await server.WaitForProbeAsync(removedRun, TimeSpan.FromSeconds(8));

            var baselineSeen = baselineHit?.ServerCookie?.Contains(CookieValue, StringComparison.Ordinal) == true;
            var removedServerSeen = removedHit?.ServerCookie?.Contains(CookieValue, StringComparison.Ordinal) == true;
            string verdict;
            if (!baselineSeen)
                verdict = "inconclusive-baseline-cookie-missing";
            else if (removedServerSeen)
                verdict = "ineffective-server-still-received-cookie";
            else
                verdict = "effective-server-did-not-receive-cookie";

            var result = new
            {
                generatedAtUtc = DateTimeOffset.UtcNow,
                runtime = CoreWebView2Environment.GetAvailableBrowserVersionString(),
                outputPath = $"http://{Host}:{server.Port}/",
                baseline = new
                {
                    run = baselineRun,
                    eventCookie = baselineEventCookie,
                    serverCookie = baselineHit?.ServerCookie,
                },
                removal = new
                {
                    run = removedRun,
                    eventCookie = removedHit?.EventCookie,
                    observedEventCookie = eventCookie,
                    removedHeaderCount = removedCount,
                    serverCookie = removedHit?.ServerCookie,
                },
                baselineSeen,
                removedServerSeen,
                verdict,
            };

            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            await File.WriteAllTextAsync(outputPath, json);
            Console.WriteLine(json);
            Console.WriteLine("结果已写入: " + Path.GetFullPath(outputPath));

            return baselineSeen ? 0 : 2;
        }
        finally
        {
            try { webView.Dispose(); } catch { }
            window.Close();
            try { Directory.Delete(userDataFolder, recursive: true); } catch { }
        }
    }

    private static async Task NavigateAndWaitAsync(CoreWebView2 core, string url)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            core.NavigationCompleted -= Handler;
            if (e.IsSuccess)
                completed.TrySetResult();
            else
                completed.TrySetException(new InvalidOperationException("导航失败: " + e.WebErrorStatus));
        }

        core.NavigationCompleted += Handler;
        core.Navigate(url);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await Task.Delay(500); // 给子资源请求落定留出时间
    }

    private static string ResolveOutputPath(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--output", StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return Path.Combine(Environment.CurrentDirectory, "artifacts", "cookie-probe", "result.json");
    }
}

internal sealed class ProbeHit
{
    public required string Run { get; init; }
    public string? EventCookie { get; init; }
    public string? ServerCookie { get; init; }
}

internal sealed class LocalHttpServer : IAsyncDisposable
{
    private static readonly byte[] GifBytes =
        Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==");

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ProbeHit>> _probes = new(StringComparer.Ordinal);

    private LocalHttpServer(TcpListener listener)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
    }

    public int Port { get; }

    public static LocalHttpServer Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new LocalHttpServer(listener);
    }

    public async Task<ProbeHit?> WaitForProbeAsync(string run, TimeSpan timeout)
    {
        var tcs = _probes.GetOrAdd(run, _ => new TaskCompletionSource<ProbeHit>(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            return await tcs.Task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }

            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(_cts.Token);
                if (string.IsNullOrEmpty(requestLine))
                    return;

                var parts = requestLine.Split(' ');
                if (parts.Length < 2)
                    return;

                var target = parts[1];
                string? cookie = null;
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_cts.Token)))
                {
                    if (line.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
                        cookie = line.Substring("Cookie:".Length).Trim();
                }

                var path = target.Split('?')[0];
                var query = target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : string.Empty;
                var run = ReadQueryValue(query, "run") ?? "none";

                byte[] body;
                string contentType;
                string? setCookie = null;
                if (path == "/page")
                {
                    var nonce = Guid.NewGuid().ToString("N");
                    var html = "<!doctype html><html><body>" +
                               $"<img src=\"/probe?run={WebUtility.UrlEncode(run)}&n={nonce}\">" +
                               "</body></html>";
                    body = Encoding.UTF8.GetBytes(html);
                    contentType = "text/html; charset=utf-8";
                    setCookie = $"{Program.CookieName}={Program.CookieValue}; Path=/; SameSite=Lax";
                }
                else if (path == "/probe")
                {
                    _probes.GetOrAdd(run, _ => new TaskCompletionSource<ProbeHit>(TaskCreationOptions.RunContinuationsAsynchronously))
                        .TrySetResult(new ProbeHit { Run = run, ServerCookie = cookie });
                    body = GifBytes;
                    contentType = "image/gif";
                }
                else
                {
                    body = Encoding.UTF8.GetBytes("not found");
                    contentType = "text/plain; charset=utf-8";
                }

                var header = "HTTP/1.1 200 OK\r\n" +
                             $"Content-Type: {contentType}\r\n" +
                             $"Content-Length: {body.Length}\r\n" +
                             (setCookie == null ? string.Empty : $"Set-Cookie: {setCookie}\r\n") +
                             "Cache-Control: no-store\r\n" +
                             "Connection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), _cts.Token);
                await stream.WriteAsync(body, _cts.Token);
                await stream.FlushAsync(_cts.Token);
            }
            catch (Exception ex) when (ex is IOException ||
                                       ex is OperationCanceledException || ex is ObjectDisposedException)
            {
                // 探针进入关闭阶段或浏览器提前断开，无需噪音日志
            }
        }
    }

    private static string? ReadQueryValue(string query, string key)
    {
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && string.Equals(pair[0], key, StringComparison.Ordinal))
                return WebUtility.UrlDecode(pair[1]);
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        _cts.Dispose();
    }
}
