using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Apage.Spikes.WebMessage;

public partial class MainWindow : Window
{
    private const string VirtualHost = "apage-spike.local";
    private readonly StringBuilder _logBuffer = new();
    private readonly bool _stay;
    private bool _allPassed = true;
    private bool _finished;

    public MainWindow()
    {
        InitializeComponent();
        _stay = Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--stay", StringComparison.OrdinalIgnoreCase));
        Loaded += async (_, _) => await RunSpikeAsync();
    }

    private void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        lock (_logBuffer) { _logBuffer.AppendLine(line); }
        if (Dispatcher.CheckAccess())
            AppendLine(line);
        else
            Dispatcher.BeginInvoke(() => AppendLine(line));
    }

    private void AppendLine(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    private async Task RunSpikeAsync()
    {
        // 看门狗：任何阶段挂死都以 exit 3 收场，保证自动化可观测
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(120));
            if (!_finished)
            {
                Log("FATAL: 整体超时（120s），强制退出");
                FlushLogToFile();
                Environment.Exit(3);
            }
        });

        try
        {
            Log("== WebMessage Spike 开始（docs/product-design.md §4.4）==");
            Log($"WebView2 Runtime 版本: {CoreWebView2Environment.GetAvailableBrowserVersionString()}");

            var udf = Path.Combine(Path.GetTempPath(), "ApageWebMessageSpike", "udf");
            Directory.CreateDirectory(udf);
            var env = await CoreWebView2Environment.CreateAsync(null, udf);
            await WebView.EnsureCoreWebView2Async(env);
            var core = WebView.CoreWebView2;

            // 离线可复现的“真实导航”：虚拟主机名映射到本地 wwwroot
            var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            core.SetVirtualHostNameToFolderMapping(VirtualHost, wwwroot, CoreWebView2HostResourceAccessKind.Allow);

            var bridgeJs = LoadEmbeddedResource("Apage.Spikes.WebMessage.bridge.js");
            await core.AddScriptToExecuteOnDocumentCreatedAsync(bridgeJs);
            Log("注入脚本已注册（AddScriptToExecuteOnDocumentCreatedAsync）");

            core.WebMessageReceived += OnWebMessageReceived;

            var navDone = new TaskCompletionSource();
            core.NavigationCompleted += (_, e) =>
            {
                Log($"导航完成: IsSuccess={e.IsSuccess} WebErrorStatus={e.WebErrorStatus}");
                navDone.TrySetResult();
            };

            var url = $"https://{VirtualHost}/testpage.html";
            Log($"导航: {url}");
            WebView.Source = new Uri(url);
            await navDone.Task.WaitAsync(TimeSpan.FromSeconds(20));

            await Task.Delay(500); // 给 document-start 发起的 ping 留出落定时间

            StatusText.Text = "断言执行中…";
            var sw = Stopwatch.StartNew();
            var json = await core.ExecuteScriptAsync("window.__runSpikeTests()");
            sw.Stop();
            Log($"ExecuteScriptAsync(全量断言) 返回，耗时 {sw.ElapsedMilliseconds} ms");

            ReportResults(json);
        }
        catch (Exception ex)
        {
            _allPassed = false;
            Log("EXCEPTION: " + ex);
        }

        _finished = true;
        var exitCode = _allPassed ? 0 : 1;
        StatusText.Text = _allPassed ? "全部断言通过（exit 0）" : "存在失败断言（exit 1）";
        Log($"== SPIKE 结果: {(_allPassed ? "PASS" : "FAIL")} (exit {exitCode}) ==");
        FlushLogToFile();

        Environment.ExitCode = exitCode;
        if (!_stay)
        {
            await Task.Delay(2500); // 让日志可见
            Application.Current.Shutdown(exitCode);
        }
    }

    /// <summary>
    /// §4.4 C# 端：解析 {id,type,payload} → 异步分发 → 回 {id,ok,data} / {id,ok:false,error}
    /// </summary>
    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var core = (CoreWebView2)sender!;
        var id = 0;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            id = root.GetProperty("id").GetInt32();
            var type = root.GetProperty("type").GetString() ?? "";
            // JsonDocument 释放前必须 Clone，payload 才会跨 await 存活
            var payload = root.TryGetProperty("payload", out var p)
                ? p.Clone()
                : JsonDocument.Parse("{}").RootElement.Clone();

            var data = await BackendDispatcher.DispatchAsync(type, payload);
            PostJson(core, JsonSerializer.Serialize(new { id, ok = true, data }));
        }
        catch (Exception ex)
        {
            PostJson(core, JsonSerializer.Serialize(new { id, ok = false, error = ex.Message }));
        }
    }

    private void PostJson(CoreWebView2 core, string json)
    {
        // CoreWebView2 成员须在 UI 线程调用
        if (Dispatcher.CheckAccess())
            core.PostWebMessageAsJson(json);
        else
            Dispatcher.Invoke(() => core.PostWebMessageAsJson(json));
    }

    private void ReportResults(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        foreach (var a in root.GetProperty("assertions").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString();
            var pass = a.GetProperty("pass").GetBoolean();
            var detail = a.GetProperty("detail").GetString();
            Log($"[{(pass ? "PASS" : "FAIL")}] {name}" + (string.IsNullOrEmpty(detail) ? "" : $" | {detail}"));
            if (!pass) _allPassed = false;
        }
        if (root.TryGetProperty("latency", out var lat) && lat.ValueKind == JsonValueKind.Object)
        {
            Log($"延迟实测: count={lat.GetProperty("count")} avg={lat.GetProperty("avgMs")}ms " +
                $"min={lat.GetProperty("minMs")}ms p50={lat.GetProperty("p50Ms")}ms " +
                $"p95={lat.GetProperty("p95Ms")}ms max={lat.GetProperty("maxMs")}ms");
        }
    }

    private static string LoadEmbeddedResource(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException($"嵌入资源缺失: {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void FlushLogToFile()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "webmessage-spike-run.log");
            lock (_logBuffer) { File.WriteAllText(path, _logBuffer.ToString()); }
        }
        catch { /* 日志落盘失败不影响退出码 */ }
    }
}
