using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using Apage.Core.Services.ScriptManager;
using Microsoft.Web.WebView2.Core;

namespace Apage.Spikes.UserScriptInjection;

public partial class MainWindow : Window
{
    private const string VirtualHost = "userscript-spike.local";
    private const string ScriptAId = "script-a";
    private const string ScriptBId = "script-b";
    private const string NoGrantsId = "script-none";
    private const string SpecialText = "引号\" 反斜杠\\ 换行\n 中文 \u2028 \u2029 </script>";

    private readonly List<AssertionResult> _assertions = new();
    private readonly List<WriteMessage> _writes = new();
    private readonly List<string> _registrationIds = new();
    private readonly Dictionary<string, string> _bridgeTokens = new();
    private readonly List<string> _rejectedReasons = new();
    private int _cspResponseCount;
    private string _runtimeVersion = string.Empty;
    private string? _bridgeError;
    private bool _finished;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RunSpikeAsync();
    }

    private async Task RunSpikeAsync()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(60));
            if (_finished) return;
            TryWriteTimeoutResult();
            Environment.Exit(3);
        });

        try
        {
            _runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
            Log($"WebView2 Runtime: {_runtimeVersion}");

            var udf = Path.Combine(Path.GetTempPath(), "ApageUserScriptInjectionSpike", "udf");
            Directory.CreateDirectory(udf);
            var environment = await CoreWebView2Environment.CreateAsync(null, udf);
            await WebView.EnsureCoreWebView2Async(environment);
            var core = WebView.CoreWebView2;
            ConfigureOfflineCspPage(core);
            core.WebMessageReceived += OnWebMessageReceived;

            await RegisterScriptsAsync(core, counterA: 7, counterB: 41, protoValue: "prototype-value");
            var firstTokenA = _bridgeTokens.First(pair => pair.Value == ScriptAId).Key;

            await NavigateAndWaitAsync(core, $"https://{VirtualHost}/testpage.html?generation=1");
            await WaitForWritesAsync(4);
            await VerifyFirstPageAsync(core);
            VerifyWriteMessages(expectedA: 8, expectedB: 42, label: "generation-1");

            // 快照刷新契约：移除旧 document-created 注册，按已持久化的值重新 Build，
            // 并轮换 bridgeToken 后再导航。官方 API 文档说明注册会作用于后续导航直至移除。
            foreach (var id in _registrationIds)
                core.RemoveScriptToExecuteOnDocumentCreated(id);
            _registrationIds.Clear();
            _bridgeTokens.Clear();
            _writes.Clear();
            _rejectedReasons.Clear();
            await RegisterScriptsAsync(core, counterA: 8, counterB: 42,
                protoValue: new Dictionary<string, object?> { ["safe"] = true });

            await NavigateAndWaitAsync(core, $"https://{VirtualHost}/testpage.html?generation=2");
            await core.ExecuteScriptAsync(
                "window.chrome.webview.postMessage({channel:'script-bridge',api:'GM_setValue'," +
                "scriptId:'script-a',bridgeToken:" + JsonSerializer.Serialize(firstTokenA) +
                ",args:{key:'stale-token',value:true}})");
            await WaitForWritesAsync(4);
            await VerifyRefreshPageAsync(core);
            VerifyWriteMessages(expectedA: 9, expectedB: 43, label: "generation-2");
        }
        catch (Exception ex)
        {
            Check("spike-execution", false, ex.ToString());
        }

        _finished = true;
        if (_bridgeError != null) Check("bridge", false, _bridgeError);
        var passed = _bridgeError == null && _assertions.All(a => a.Pass);
        var exitCode = passed ? 0 : 1;
        StatusText.Text = passed ? "全部断言通过" : "存在失败断言";
        WriteResult(passed);
        Log($"== {(passed ? "PASS" : "FAIL")} (exit {exitCode}) ==");
        await Task.Delay(400);
        Application.Current.Shutdown(exitCode);
    }

    private void ConfigureOfflineCspPage(CoreWebView2 core)
    {
        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var html = File.ReadAllText(Path.Combine(wwwroot, "testpage.html"));
        var bytes = Encoding.UTF8.GetBytes(html);
        // 不能同时启用虚拟目录映射：它会先于 WebResourceRequested 返回文件，
        // 使下面的 CSP 响应头处理器永远不命中。此处完全由 filter + 自定义响应供页。
        core.AddWebResourceRequestedFilter($"https://{VirtualHost}/*",
            CoreWebView2WebResourceContext.Document);
        core.WebResourceRequested += (_, e) =>
        {
            _cspResponseCount++;
            // 测试页不再带 CSP meta：若页面 eval 仍被阻止，必然来自此响应头。
            // 响应头 CSP 不含 unsafe-eval：页面内 new Function 应被阻止，
            // 同时验证 AddScriptToExecuteOnDocumentCreated 的用户脚本包装器仍可用。
            const string headers =
                "Content-Type: text/html; charset=utf-8\r\n" +
                "Content-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'";
            e.Response = core.Environment.CreateWebResourceResponse(
                new MemoryStream(bytes), 200, "OK", headers);
        };
        Log("离线测试页由 WebResourceRequested 响应投递 CSP（无 unsafe-eval）");
    }

    private async Task RegisterScriptsAsync(CoreWebView2 core, int counterA, int counterB, object? protoValue)
    {
        const string scriptA = """
            const initialCounter = GM_getValue('counter', -1);
            GM_setValue('counter', initialCounter + 1);
            const nested = GM_getValue('nested');
            const protoBefore = GM_getValue('__proto__');
            GM_setValue('__proto__', { safe: true });
            window.__scriptAResult = {
                readyState: document.readyState,
                text: GM_getValue('text'),
                missing: GM_getValue('missing', 'fallback'),
                explicitNull: GM_getValue('nothing', 'fallback'),
                explicitNullIsNull: GM_getValue('nothing', 'fallback') === null,
                initialCounter: initialCounter,
                immediateCounter: GM_getValue('counter', -1),
                nestedEnabled: nested.enabled,
                secondItem: nested.items[1],
                protoBefore: protoBefore,
                protoAfterSet: GM_getValue('__proto__').safe,
                constructorValue: GM_getValue('constructor'),
                internalStore: typeof __STORE__,
                internalToken: typeof __BRIDGE_TOKEN__,
                globalGetType: typeof window.GM_getValue,
                globalSetType: typeof window.GM_setValue,
                sloppyThis: (function () { return this === window; })(),
                unsupportedValueRejected: (function () {
                    try { GM_setValue('bigint', 1n); return 'none'; }
                    catch (e) { return e.name; }
                })(),
                callerLeak: (function () {
                    try {
                        var userFunction = arguments.callee.caller;
                        var outer = userFunction && userFunction.caller;
                        return !!(outer && outer.arguments && outer.arguments[2]);
                    } catch (e) { return false; }
                })()
            };
            window.__lateSet = function () {
                GM_setValue('late', { proof: true });
            };
            """;
        const string scriptB = """
            const initialCounter = GM_getValue('counter', -1);
            GM_setValue('counter', initialCounter + 1);
            window.__scriptBResult = {
                initialCounter: initialCounter,
                immediateCounter: GM_getValue('counter', -1)
            };
            """;
        const string noGrants = """
            window.__noneResult = {
                getType: typeof GM_getValue,
                setType: typeof GM_setValue,
                internalStore: typeof __STORE__,
                internalToken: typeof __BRIDGE_TOKEN__,
                callError: (function () {
                    try { GM_setValue('ungranted', true); return 'none'; }
                    catch (e) { return e.name; }
                })(),
                callerLeak: (function () {
                    try {
                        var userFunction = arguments.callee.caller;
                        var outer = userFunction && userFunction.caller;
                        return !!(outer && outer.arguments && outer.arguments[2]);
                    } catch (e) { return false; }
                })()
            };
            """;

        var valuesA = new Dictionary<string, object?>
        {
            ["text"] = SpecialText,
            ["nothing"] = null,
            ["counter"] = counterA,
            ["nested"] = new { enabled = true, items = new object?[] { 1, "two", null } },
            ["__proto__"] = protoValue,
            ["constructor"] = "constructor-value"
        };
        var valuesB = new Dictionary<string, object?> { ["counter"] = counterB };

        await AddScriptAsync(core, ScriptAId, scriptA, Metadata("GM_getValue", "GM_setValue"), valuesA);
        await AddScriptAsync(core, ScriptBId, scriptB, Metadata("GM_getValue", "GM_setValue"), valuesB);
        await AddScriptAsync(core, NoGrantsId, noGrants, Metadata("none"), null);
        Log($"已注册 3 个 document-start 注入脚本（快照 A={counterA}, B={counterB}）");
    }

    private async Task AddScriptAsync(CoreWebView2 core, string scriptId, string source,
        ScriptMetadata metadata, IReadOnlyDictionary<string, object?>? values)
    {
        var plan = ScriptInjectionBuilder.Build(scriptId, source, metadata, values);
        _bridgeTokens[plan.BridgeToken] = scriptId;
        var registrationId = await core.AddScriptToExecuteOnDocumentCreatedAsync(plan.JavaScript);
        _registrationIds.Add(registrationId);
    }

    private async Task VerifyFirstPageAsync(CoreWebView2 core)
    {
        var page = await EvaluateAsync(core, "window.__pageProbe");
        var a = await EvaluateAsync(core, "window.__scriptAResult");
        var b = await EvaluateAsync(core, "window.__scriptBResult");
        var none = await EvaluateAsync(core, "window.__noneResult");

        Check("document-start-sync", page.GetProperty("readyState").GetString() == "loading"
            && page.GetProperty("scriptARan").GetBoolean()
            && a.GetProperty("readyState").GetString() == "loading",
            "页面首段脚本无等待观察到用户脚本结果");
        Check("csp-response-header-delivered", _cspResponseCount >= 1
            && page.GetProperty("cspBlocksEval").GetBoolean()
            && page.GetProperty("scriptARan").GetBoolean(),
            "测试页无 meta CSP；响应 handler 命中且页面 eval 被阻止，注入脚本仍完成包装");
        Check("page-serialization-hook-cannot-sniff-token",
            page.GetProperty("serializationHookRan").GetBoolean()
            && page.GetProperty("sniffedBridgeToken").ValueKind == JsonValueKind.Null,
            "页面 toJSON 钩子确实运行，但预序列化字符串/null-prototype 消息不暴露 bridgeToken");
        Check("inline-json-roundtrip", page.GetProperty("text").GetString() == SpecialText
            && a.GetProperty("text").GetString() == SpecialText,
            "引号/反斜杠/换行/中文/U+2028/U+2029 均保持原值");
        Check("missing-vs-explicit-null", a.GetProperty("missing").GetString() == "fallback"
            && a.GetProperty("explicitNull").ValueKind == JsonValueKind.Null
            && a.GetProperty("explicitNullIsNull").GetBoolean());
        Check("set-then-get-is-synchronous", a.GetProperty("initialCounter").GetInt32() == 7
            && a.GetProperty("immediateCounter").GetInt32() == 8
            && b.GetProperty("initialCounter").GetInt32() == 41
            && b.GetProperty("immediateCounter").GetInt32() == 42);
        Check("script-storage-isolation", page.GetProperty("initialCounterA").GetInt32() == 7
            && page.GetProperty("initialCounterB").GetInt32() == 41);
        Check("nested-values", a.GetProperty("nestedEnabled").GetBoolean()
            && a.GetProperty("secondItem").GetString() == "two");
        Check("special-keys", a.GetProperty("protoBefore").GetString() == "prototype-value"
            && a.GetProperty("protoAfterSet").GetBoolean()
            && a.GetProperty("constructorValue").GetString() == "constructor-value");
        Check("unsupported-value-is-rejected",
            a.GetProperty("unsupportedValueRejected").GetString() == "TypeError");
        Check("user-script-scope-isolation", a.GetProperty("internalStore").GetString() == "undefined"
            && a.GetProperty("internalToken").GetString() == "undefined"
            && a.GetProperty("globalGetType").GetString() == "undefined"
            && a.GetProperty("globalSetType").GetString() == "undefined"
            && !a.GetProperty("callerLeak").GetBoolean()
            && a.GetProperty("sloppyThis").GetBoolean(),
            "非严格模式兼容 + 闭包变量/caller 链不可见；不是安全沙箱");
        Check("grant-none", none.GetProperty("getType").GetString() == "undefined"
            && none.GetProperty("setType").GetString() == "undefined"
            && none.GetProperty("callError").GetString() == "TypeError"
            && none.GetProperty("internalStore").GetString() == "undefined"
            && none.GetProperty("internalToken").GetString() == "undefined"
            && !none.GetProperty("callerLeak").GetBoolean(),
            "未授权脚本实际调用 GM_setValue 被拒绝");
        Check("page-first-script-observes-both-stores", page.GetProperty("noneRan").GetBoolean()
            && page.GetProperty("initialCounterA").GetInt32() == 7
            && page.GetProperty("initialCounterB").GetInt32() == 41);
        Check("forged-message-is-rejected", _rejectedReasons.Contains("missing-token"));
        Check("message-source-gate", !_rejectedReasons.Contains("wrong-source"));
    }

    private async Task VerifyRefreshPageAsync(CoreWebView2 core)
    {
        var page = await EvaluateAsync(core, "window.__pageProbe");
        var a = await EvaluateAsync(core, "window.__scriptAResult");
        var b = await EvaluateAsync(core, "window.__scriptBResult");
        var none = await EvaluateAsync(core, "window.__noneResult");

        Check("fresh-snapshot-on-second-navigation", page.GetProperty("scriptARan").GetBoolean()
            && page.GetProperty("initialCounterA").GetInt32() == 8
            && page.GetProperty("initialCounterB").GetInt32() == 42
            && a.GetProperty("initialCounter").GetInt32() == 8
            && a.GetProperty("immediateCounter").GetInt32() == 9
            && b.GetProperty("initialCounter").GetInt32() == 42
            && b.GetProperty("immediateCounter").GetInt32() == 43,
            "移除旧注册后按持久化值重建，第二次导航不再读旧快照");
        Check("csp-response-header-second-navigation", _cspResponseCount >= 2
            && page.GetProperty("cspBlocksEval").GetBoolean());
        Check("page-serialization-hook-cannot-sniff-token-second-navigation",
            page.GetProperty("serializationHookRan").GetBoolean()
            && page.GetProperty("sniffedBridgeToken").ValueKind == JsonValueKind.Null);
        Check("generation-2-common-invariants", a.GetProperty("text").GetString() == SpecialText
            && a.GetProperty("missing").GetString() == "fallback"
            && a.GetProperty("explicitNullIsNull").GetBoolean()
            && a.GetProperty("unsupportedValueRejected").GetString() == "TypeError"
            && a.GetProperty("internalStore").GetString() == "undefined"
            && a.GetProperty("internalToken").GetString() == "undefined"
            && a.GetProperty("sloppyThis").GetBoolean()
            && !a.GetProperty("callerLeak").GetBoolean()
            && a.GetProperty("protoBefore").ValueKind == JsonValueKind.Object
            && a.GetProperty("protoBefore").GetProperty("safe").GetBoolean()
            && none.GetProperty("getType").GetString() == "undefined"
            && none.GetProperty("callError").GetString() == "TypeError");
        Check("forged-message-rejected-after-rotation", _rejectedReasons.Contains("missing-token"));
        Check("stale-token-rejected-after-rotation", _rejectedReasons.Contains("unknown-token"));
        Check("message-source-gate-after-rotation", !_rejectedReasons.Contains("wrong-source"));
    }

    private void VerifyWriteMessages(int expectedA, int expectedB, string label)
    {
        Check($"{label}-write-message-count", _writes.Count == 4, $"actual={_writes.Count}");
        var aCounter = _writes.SingleOrDefault(w => w.ScriptId == ScriptAId && w.Key == "counter");
        var bCounter = _writes.SingleOrDefault(w => w.ScriptId == ScriptBId && w.Key == "counter");
        var proto = _writes.SingleOrDefault(w => w.ScriptId == ScriptAId && w.Key == "__proto__");
        var late = _writes.SingleOrDefault(w => w.ScriptId == ScriptAId && w.Key == "late");
        Check($"{label}-write-message-a", aCounter != null && aCounter.Value.GetInt32() == expectedA);
        Check($"{label}-write-message-b", bCounter != null && bCounter.Value.GetInt32() == expectedB);
        Check($"{label}-write-message-special-key", proto != null
            && proto.Value.GetProperty("safe").GetBoolean());
        Check($"{label}-late-write-after-page-hook", late != null
            && late.Value.ValueKind == JsonValueKind.Object
            && late.Value.GetProperty("proof").GetBoolean());
        Check($"{label}-no-write-from-ungranted-or-forged-message",
            _writes.All(w => w.ScriptId != NoGrantsId)
            && _writes.All(w => w.Key != "forged")
            && _writes.All(w => w.Key != "ungranted")
            && _writes.All(w => w.Key != "bigint")
            && _writes.All(w => w.Key != "stale-token"));
    }

    private async Task WaitForWritesAsync(int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_writes.Count < expected && DateTime.UtcNow < deadline && _bridgeError == null)
            await Task.Delay(50);
        // 安静窗口：捕获迟到、重复或未授权消息，避免达到预期数量就提前结束。
        for (var i = 0; i < 8; i++) await Task.Delay(50);
    }

    private static async Task NavigateAndWaitAsync(CoreWebView2 core, string url)
    {
        var navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<CoreWebView2NavigationCompletedEventArgs> handler = (_, e) =>
            navigation.TrySetResult(e.IsSuccess);
        core.NavigationCompleted += handler;
        try
        {
            core.Navigate(url);
            var succeeded = await navigation.Task.WaitAsync(TimeSpan.FromSeconds(20));
            if (!succeeded) throw new InvalidOperationException($"本地测试页导航失败: {url}");
        }
        finally
        {
            core.NavigationCompleted -= handler;
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var source)
                || source.Scheme != Uri.UriSchemeHttps
                || !string.Equals(source.Host, VirtualHost, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(source.AbsolutePath, "/testpage.html", StringComparison.OrdinalIgnoreCase))
            {
                Reject("wrong-source");
                return;
            }

            // 合法 GM 消息发送预序列化的 JSON 字符串；同时接受普通对象用于伪造/旧令牌负向探针。
            if (!TryGetMessageJson(e.WebMessageAsJson, out var messageJson))
            {
                Reject("bad-json");
                return;
            }
            using var document = JsonDocument.Parse(messageJson);
            var root = document.RootElement;
            if (!TryGetString(root, "channel", out var channel) || channel != ScriptInjectionBuilder.BridgeChannel)
            {
                Reject("wrong-channel");
                return;
            }
            if (!TryGetString(root, "api", out var api) || api != "GM_setValue")
            {
                Reject("wrong-api");
                return;
            }
            // 只信任宿主登记的令牌；消息中的 scriptId 只是待校验字段。
            if (!TryGetString(root, "bridgeToken", out var token))
            {
                Reject("missing-token");
                return;
            }
            if (!_bridgeTokens.TryGetValue(token, out var trustedScriptId))
            {
                Reject("unknown-token");
                return;
            }
            if (!TryGetString(root, "scriptId", out var scriptId) || scriptId != trustedScriptId)
            {
                Reject("script-id-mismatch");
                return;
            }
            if (!root.TryGetProperty("args", out var args)
                || args.ValueKind != JsonValueKind.Object
                || !TryGetString(args, "key", out var key)
                || !args.TryGetProperty("value", out var value))
            {
                Reject("bad-args");
                return;
            }
            _writes.Add(new WriteMessage(trustedScriptId, key, value.Clone()));
        }
        catch (JsonException)
        {
            Reject("bad-json");
        }
        catch (Exception ex)
        {
            _bridgeError = ex.ToString();
        }
    }

    private static bool TryGetMessageJson(string webMessageAsJson, out string messageJson)
    {
        messageJson = string.Empty;
        try
        {
            using var outer = JsonDocument.Parse(webMessageAsJson);
            messageJson = outer.RootElement.ValueKind == JsonValueKind.String
                ? outer.RootElement.GetString() ?? string.Empty
                : webMessageAsJson;
            return messageJson.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetString(JsonElement owner, string name, out string value)
    {
        value = string.Empty;
        if (!owner.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private void Reject(string reason) => _rejectedReasons.Add(reason);

    private static async Task<JsonElement> EvaluateAsync(CoreWebView2 core, string expression)
    {
        var json = await core.ExecuteScriptAsync(expression);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static ScriptMetadata Metadata(params string[] grants)
    {
        var metadata = new ScriptMetadata();
        metadata.Grants.AddRange(grants);
        return metadata;
    }

    private void Check(string name, bool pass, string detail = "")
    {
        _assertions.Add(new AssertionResult(name, pass, detail));
        Log($"[{(pass ? "PASS" : "FAIL")}] {name}" + (string.IsNullOrEmpty(detail) ? "" : $" | {detail}"));
    }

    private void Log(string message)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    private void WriteResult(bool passed)
    {
        var outputPath = Environment.GetEnvironmentVariable("APAGE_SPIKE_RESULTS")
            ?? Path.Combine(AppContext.BaseDirectory, "user-script-injection-results.json");
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var result = new
        {
            test = "user-script-injection",
            passed,
            runtimeVersion = _runtimeVersion,
            assertions = _assertions,
            writes = _writes,
            rejectedReasons = _rejectedReasons,
            cspResponseCount = _cspResponseCount,
            registrationCount = _registrationIds.Count
        };
        File.WriteAllText(outputPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void TryWriteTimeoutResult()
    {
        try
        {
            var outputPath = Environment.GetEnvironmentVariable("APAGE_SPIKE_RESULTS")
                ?? Path.Combine(AppContext.BaseDirectory, "user-script-injection-results.json");
            File.WriteAllText(outputPath, JsonSerializer.Serialize(new
            {
                test = "user-script-injection",
                passed = false,
                error = "timeout after 60 seconds"
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 超时时仍以 exit 3 作为失败信号 */ }
    }

    private sealed record AssertionResult(string Name, bool Pass, string Detail);
    private sealed record WriteMessage(string ScriptId, string Key, JsonElement Value);
}
