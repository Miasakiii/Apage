#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Apage.Core.Configuration;
using Apage.Core.Models;
using Apage.Core.Services;
using Apage.Portable.Services;
using Microsoft.Web.WebView2.Core;

namespace Apage.Portable.Views;

/// <summary>
/// 单个浏览器标签：内嵌 WebView2，对外暴露导航契约（Navigate/GoBack/GoForward/Reload、
/// TitleChanged/UrlChanged、CanGoBack/CanGoForward）。
/// Runtime 缺失时按 R1 显示中文引导面板（手动下载，绝不静默联网下载）。
/// </summary>
public partial class BrowserTabView : UserControl
{
    // 微软官网 WebView2 Runtime 下载页（R1 手动引导）
    private const string RuntimeDownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    private bool _initStarted;
    private bool _coreReady;
    private bool _findAvailable;
    private bool _findActive;
    private string? _pendingUrl;

    // 顶层导航 URI 的有界记录：Sec-Fetch-Dest 缺失时作为主文档判定的安全回退。
    private const int TopLevelNavigationMemory = 32;
    private readonly HashSet<string> _topLevelNavigationUris = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _topLevelNavigationOrder = new();

    public BrowserTabView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>页面标题变化（参数为新标题）。</summary>
    public event EventHandler<string>? TitleChanged;

    /// <summary>地址变化（参数为新 URL，含重定向）。</summary>
    public event EventHandler<string>? UrlChanged;

    /// <summary>
    /// 站点图标变化（无图标时为 null）。图标由 WebView2 随页面加载取回——
    /// 是网页自身声明的 favicon（非第三方图标服务），无额外联网请求。
    /// </summary>
    public event EventHandler<ImageSource?>? FaviconChanged;

    /// <summary>
    /// 宿主可注入共享 Environment（通常来自 BrowserLifecycleService.GetSharedEnvironmentAsync()）。
    /// 不注入则按 AppPaths 缓存策略（R7）自行创建。必须在控件 Loaded 前赋值才生效。
    /// </summary>
    public CoreWebView2Environment? SharedEnvironment { get; set; }

    /// <summary>
    /// 宿主注入的标签依赖包（设置 / 隐私统计 / 广告拦截 / 内核生命周期，
    /// docs/design/2026-10-23-privacy-stats-ntp.md §3.4）。须在 Loaded 前赋值；
    /// 与 <see cref="SharedEnvironment"/> 二选一（Context.Lifecycle 未设置时按 AppPaths（R7）自建）。
    /// </summary>
    public BrowserTabContext? Context { get; set; }

    /// <summary>
    /// 该标签是否为隐私标签。为 true 时**不写历史记录**（与会话捕获的排除规则一致）。
    /// 由 TabManager 在物化时按 BrowserTab.IsPrivate 赋值。
    /// </summary>
    public bool IsPrivate { get; set; }

    /// <summary>
    /// 下载协调器（B2）；未装配时下载仍由 WebView2 正常完成，只是不落库。
    /// 由 TabManager 在物化时注入共享实例。
    /// </summary>
    public Services.DownloadCoordinator? Downloads { get; set; }

    /// <summary>底层 CoreWebView2（未就绪时为 null）。供广告拦截/隐私等事件管道（§5.4）挂接。</summary>
    public CoreWebView2? Core => WebView.CoreWebView2;

    public bool CanGoBack => WebView.CoreWebView2?.CanGoBack == true;

    public bool CanGoForward => WebView.CoreWebView2?.CanGoForward == true;

    /// <summary>创建 BrowserLifecycleService 的工厂适配（Core 层不引用 WebView2 SDK，经此委托门面创建 Environment）。</summary>
    /// <param name="additionalBrowserArguments">附加浏览器参数（如 WebRTC IP 防护，§4.4；无则 null）。</param>
    public static BrowserLifecycleService CreateLifecycleService(string? additionalBrowserArguments = null) =>
        new BrowserLifecycleService(CreateEnvironmentAsync, additionalBrowserArguments);

    public void Navigate(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        // 内核未就绪时暂存，初始化完成后自动导航
        if (!_coreReady || WebView.CoreWebView2 == null)
        {
            _pendingUrl = url.Trim();
            return;
        }

        WebView.CoreWebView2.Navigate(NormalizeUrl(url));
    }

    public void GoBack()
    {
        if (CanGoBack)
            WebView.CoreWebView2!.GoBack();
    }

    public void GoForward()
    {
        if (CanGoForward)
            WebView.CoreWebView2!.GoForward();
    }

    public void Reload()
    {
        if (_coreReady)
            WebView.CoreWebView2?.Reload();
    }

    // ===== 页内查找（S3）=====
    // 实现依据：WebView2 SDK 1.0.4078.44 的 CoreWebView2Find 会话 API
    // （core.Find → StartAsync(CoreWebView2FindOptions) / FindNext / FindPrevious / Stop，
    // 结果经 MatchCountChanged / ActiveMatchIndexChanged 回报）。选项对象必须由
    // core.Environment.CreateFindOptions() 创建，不能 new。

    /// <summary>页面内按下 Ctrl+F 时触发（宿主据此打开查找条）。</summary>
    public event EventHandler? FindRequested;

    /// <summary>查找会话激活期间，页面内按下 Esc 时触发（宿主据此关闭查找条）。</summary>
    public event EventHandler? FindCloseRequested;

    /// <summary>命中结果变化（参数为命中总数与 1 起的活动序号，0 表示无活动命中）。</summary>
    public event EventHandler<FindResultsChangedEventArgs>? FindResultsChanged;

    /// <summary>当前标签是否支持页内查找（内核未就绪或 Runtime 不支持该 API 时为 false）。</summary>
    public bool CanFind => _findAvailable;

    /// <summary>最近一次查找会话的命中总数（不可用时为 0；仅供冒烟探针与诊断读取）。</summary>
    public int FindMatchCount
    {
        get
        {
            try { return WebView.CoreWebView2?.Find.MatchCount ?? 0; }
            catch { return 0; }
        }
    }

    /// <summary>
    /// 以指定查询词开启查找会话（空串等同于结束会话）。
    /// 返回 false 表示该标签当前不可查找（内核未就绪 / 不支持 / 调用失败）。
    /// </summary>
    public async Task<bool> StartFindAsync(string? term)
    {
        var core = WebView.CoreWebView2;
        if (!_findAvailable || core == null)
            return false;

        if (string.IsNullOrEmpty(term))
        {
            StopFind();
            return true;
        }

        try
        {
            var options = core.Environment.CreateFindOptions();
            options.FindTerm = term!;
            options.ShouldHighlightAllMatches = true; // 高亮全部命中，与主流浏览器一致
            options.SuppressDefaultFindDialog = true; // 自绘查找条，禁止内核再弹自带 UI
            await core.Find.StartAsync(options).ConfigureAwait(true);
            _findActive = true;
            RaiseFindResults(core);
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BrowserTabView] 查找会话启动失败：{ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>跳到下一个命中（无会话时内核侧静默忽略）。</summary>
    public void FindNextMatch() => InvokeFind(core => core.Find.FindNext());

    /// <summary>跳到上一个命中。</summary>
    public void FindPreviousMatch() => InvokeFind(core => core.Find.FindPrevious());

    /// <summary>结束查找会话（隐藏内核侧高亮）。关闭查找条时必须调用。</summary>
    public void StopFind()
    {
        _findActive = false;

        if (!_findAvailable)
            return;

        try { WebView.CoreWebView2?.Find.Stop(); }
        catch (Exception ex) { Trace.WriteLine($"[BrowserTabView] 结束查找会话失败：{ex.GetType().Name}"); }
    }

    /// <summary>把键盘焦点交回网页（关闭查找条后让 Esc/方向键继续作用于页面）。</summary>
    public void FocusPage()
    {
        try { WebView.Focus(); }
        catch { /* 内核已释放时忽略 */ }
    }

    private void InvokeFind(Action<CoreWebView2> action)
    {
        var core = WebView.CoreWebView2;
        if (!_findAvailable || core == null)
            return;

        try { action(core); }
        catch (Exception ex) { Trace.WriteLine($"[BrowserTabView] 查找导航失败：{ex.GetType().Name}"); }
    }

    private void RaiseFindResults(CoreWebView2 core)
    {
        try
        {
            FindResultsChanged?.Invoke(this, new FindResultsChangedEventArgs(
                core.Find.MatchCount, core.Find.ActiveMatchIndex));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BrowserTabView] 读取命中计数失败：{ex.GetType().Name}");
        }
    }

    /// <summary>
    /// 释放该标签的 WebView2 控件与其 CoreWebView2（关标签时调用）。
    /// 共享 Environment 不在此释放，仍归 BrowserLifecycleService 管理。
    /// </summary>
    public void DisposeCore()
    {
        try { WebView.Dispose(); }
        catch { /* 关闭竞态下忽略 */ }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initStarted)
            return;
        _initStarted = true;
        Loaded -= OnLoaded;

        // R1：先做注册表探测，缺失直接给引导，不尝试初始化
        if (!WebView2RuntimeService.Detect().IsInstalled)
        {
            ShowRuntimeMissingPanel(null, null);
            return;
        }

        try
        {
            TrySetDefaultBackgroundColor();

            // 环境解析优先级：显式注入的 SharedEnvironment > Context.Lifecycle 共享 Environment > 按 R7 自建
            var environment = SharedEnvironment;
            if (environment == null)
            {
                environment = Context?.Lifecycle != null
                    ? (CoreWebView2Environment)await Context.Lifecycle.GetSharedEnvironmentAsync().ConfigureAwait(true)
                    : (CoreWebView2Environment)await CreateEnvironmentAsync(
                        Path.Combine(AppPaths.CacheDirectory, "WebView2"), null).ConfigureAwait(true);
            }

            await WebView.EnsureCoreWebView2Async(environment);
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            ShowRuntimeMissingPanel(null, ex.Message);
        }
        catch (Exception ex)
        {
            ShowRuntimeMissingPanel("浏览器内核初始化失败", ex.Message);
        }
    }

    private void WebView_CoreWebView2InitializationCompleted(object sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            ShowRuntimeMissingPanel(null, e.InitializationException?.Message);
            return;
        }

        OnCoreReady();
    }

    private void OnCoreReady()
    {
        if (_coreReady)
            return;

        var core = WebView.CoreWebView2;
        if (core == null)
            return;

        _coreReady = true;

        // 完整事件链入口（§5.4）：标题 / 地址 / 图标随页面同步
        core.DocumentTitleChanged += (s, e) => TitleChanged?.Invoke(this, core.DocumentTitle);
        core.SourceChanged += (s, e) => UrlChanged?.Invoke(this, core.Source);
        core.FaviconChanged += OnFaviconChanged;

        // 仅主框架触发 CoreWebView2.NavigationStarting（iframe 走 FrameNavigationStarting）；
        // 记录 URI 供请求管线区分主文档与第三方 iframe 文档。
        core.NavigationStarting += (s, e) => RememberTopLevelNavigation(e.Uri);

        // 历史记录（B2）：只记成功的顶层导航；隐私标签与本地页面在 RecordHistory 内排除。
        // 顶层导航失败（证书错误、连接中断）不写历史，避免把失败页当成访问记录。
        core.NavigationCompleted += (s, e) =>
        {
            if (!e.IsSuccess)
                return;
            RecordHistory(core.Source, core.DocumentTitle);
        };

        // 下载（B2）：保留 WebView2 自带下载提示，仅把生命周期落库，供下载列表取消/打开。
        core.DownloadStarting += (s, e) => Downloads?.Track(e.DownloadOperation);

        // §5.4 事件管道（设计 §4.1）：隐私头注入 / 广告拦截计数一处收口
        HookWebPipeline(core);

        // 新标签页 WebMessage 桥（设计 §4.2）：navigate / getConfig / setSearchEngine / openSettings
        core.WebMessageReceived += OnWebMessageReceived;

        // 页内查找（S3）：会话对象与两个计数事件在此一次性挂接；Ctrl+F 需从内核侧上浮
        HookFind(core);
        TryHookAcceleratorKeys();

        WebView.Visibility = Visibility.Visible;

        if (!string.IsNullOrEmpty(_pendingUrl))
        {
            var url = _pendingUrl!;
            _pendingUrl = null;
            Navigate(url);
        }
    }

    // 历史记录（B2）：隐私标签不写；本地页面与非 http/https 地址由仓储归一化兜底拒绝。
    // 写入放线程池，失败只留诊断，绝不影响导航。
    private void RecordHistory(string? url, string? title)
    {
        if (IsPrivate)
            return;

        var history = Context?.History;
        if (history == null)
            return;

        var visitedAt = DateTimeOffset.Now;
        _ = Task.Run(async () =>
        {
            try
            {
                await history.RecordAsync(url, title, visitedAt).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[BrowserTabView] 历史记录写入失败：{ex.GetType().Name}");
            }
        });
    }

    // 页内查找会话挂接：CoreWebView2.Find 在 Runtime 版本过旧时可能抛异常（属性本身不可用），
    // 故整体 try/catch 并把 _findAvailable 置 false —— 查找条据此提示「当前内核不支持」，
    // 而不是让 Ctrl+F 静默失败。
    private void HookFind(CoreWebView2 core)
    {
        try
        {
            var find = core.Find;
            if (find == null)
                return;

            find.MatchCountChanged += (s, e) => RaiseFindResults(core);
            find.ActiveMatchIndexChanged += (s, e) => RaiseFindResults(core);
            _findAvailable = true;
        }
        catch (Exception ex)
        {
            _findAvailable = false;
            Trace.WriteLine($"[BrowserTabView] 页内查找不可用：{ex.GetType().Name}");
        }
    }

    // Ctrl+F 从网页焦点上浮（S3）：WebView2 WPF 封装把 CoreWebView2Controller 作为 internal
    // 属性持有，未公开 AcceleratorKeyPressed；这里沿用本文件既有做法（DefaultBackgroundColor
    // 同样走反射）取控件，挂一次按键加速器。取不到就静默降级——此时 Ctrl+F 仅在应用界面
    // 焦点击中时生效（MainWindow 的 PreviewKeyDown 兜底），不影响其它功能。
    private void TryHookAcceleratorKeys()
    {
        try
        {
            var prop = typeof(Microsoft.Web.WebView2.Wpf.WebView2).GetProperty(
                "CoreWebView2Controller", BindingFlags.Instance | BindingFlags.NonPublic);
            if (prop?.GetValue(WebView) is CoreWebView2Controller controller)
                controller.AcceleratorKeyPressed += OnAcceleratorKeyPressed;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[BrowserTabView] 加速键挂接失败：{ex.GetType().Name}");
        }
    }

    private void OnAcceleratorKeyPressed(object? sender, CoreWebView2AcceleratorKeyPressedEventArgs e)
    {
        if (e.KeyEventKind != CoreWebView2KeyEventKind.KeyDown)
            return;

        // VK_F = 0x46：Ctrl+F 上浮给宿主打开查找条
        if (e.VirtualKey == 0x46)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
                return;

            e.Handled = true; // 阻止内核自带的查找 UI
            FindRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        // VK_ESCAPE = 0x1B：仅当本标签的查找会话处于激活态才吞掉，
        // 否则会抢走网页自身对 Esc 的处理（弹窗/预览关闭等）
        if (e.VirtualKey == 0x1B && _findActive)
        {
            e.Handled = true;
            FindCloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    // §5.3 + §5.2 请求管道（docs/design/2026-10-23-privacy-stats-ntp.md §4.1）：
    // - 必须用带 RequestSourceKinds 的三参过滤器重载（两参重载已被官方弃用：
    //   跨源 iframe 的子资源不会触发 WebResourceRequested，广告 iframe 恰是重灾区）
    // - 处理器同步判定即返回（引擎实测均值 24µs/URL，无需 GetDeferral）
    // - 注册条件：广告引擎有规则 **或** 任一隐私头开关开（无任何需求则不挂）
    //
    // 重要边界：2026-11-02 真机探针确认，在 WebResourceRequested 删除 Cookie 头后，
    // WebView2 网络栈仍会给服务器发送同一 Cookie（docs/spikes/results/cookie-privacy.md）。
    // 因此这里不再实现“第三方 Cookie 剥离”，也不得再把它计入追踪统计或对外宣传。
    private void HookWebPipeline(CoreWebView2 core)
    {
        var settings = Context?.Settings ?? new AppSettings();
        var provider = Context?.AdBlockProvider;

        // 有 provider 就挂管道：手动更新可能稍后才装入规则，管道已就绪才能即时生效。
        var adBlockConfigured = provider != null ||
            (Context?.AdBlock != null && Context.AdBlock.BlockCount > 0);

        if (!adBlockConfigured && !settings.SendDoNotTrack && !settings.SendGPC)
            return;

        core.AddWebResourceRequestedFilter(
            "*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += (s, e) =>
        {
            var uri = e.Request.Uri;

            // 本地资产（新标签页 file://）不走拦截/头注入管道：自家内容不存在广告，且避免误伤
            if (uri != null && uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                return;

            // 1) 追踪保护头（声明性；全部请求生效，含主文档）
            if (settings.SendDoNotTrack)
                e.Request.Headers.SetHeader("DNT", "1");
            if (settings.SendGPC)
                e.Request.Headers.SetHeader("Sec-GPC", "1");

            // 2) 广告拦截：命中规则即 403。
            //    只豁免真正的顶层主文档——泛匹配规则误杀主导航的代价过高；
            //    第三方 iframe 的 Document 必须继续参与匹配，否则广告 iframe 会整块绕过。
            var engine = provider?.Current ?? Context?.AdBlock;
            if (engine != null && !IsTopLevelDocumentRequest(e) && engine.IsBlocked(uri))
            {
                // 空内容 + 403 即取消该请求（args.Response 一经赋值请求不再放行）
                e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
                Context?.Stats?.RecordAdBlocked();
            }
        };
    }

    // 判定请求是否为顶层主文档。优先使用 Chromium 的 Sec-Fetch-Dest（iframe 文档为 iframe，
    // 顶层为 document）；头缺失时回退到 NavigationStarting 记录的有界 URI 集合。无法确认时
    // 一律按“非顶层”处理：宁可让规则继续判断，也不把广告 iframe 当主文档放行。
    private bool IsTopLevelDocumentRequest(CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (e.ResourceContext != CoreWebView2WebResourceContext.Document)
            return false;

        try
        {
            var destination = e.Request.Headers.GetHeader("Sec-Fetch-Dest");
            if (!string.IsNullOrEmpty(destination))
                return string.Equals(destination, "document", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // 读取失败时走 NavigationStarting 回退，不阻断请求
        }

        return !string.IsNullOrEmpty(e.Request.Uri) && _topLevelNavigationUris.Contains(e.Request.Uri);
    }

    private void RememberTopLevelNavigation(string? uri)
    {
        if (string.IsNullOrEmpty(uri) || !_topLevelNavigationUris.Add(uri!))
            return;

        _topLevelNavigationOrder.Enqueue(uri!);
        while (_topLevelNavigationOrder.Count > TopLevelNavigationMemory)
            _topLevelNavigationUris.Remove(_topLevelNavigationOrder.Dequeue());
    }

    /// <summary>新标签页动作上浮（openSettings：打开真实设置窗口；快捷方式增删在页面本地完成）。</summary>
    public event EventHandler<string>? NewTabAction;

    private const string NtpChannel = "ntp";

    // 新标签页 WebMessage 桥（设计 §4.2）：只处理 channel='ntp' 的
    // navigate / getConfig / setSearchEngine / openSettings。
    // 快捷方式增删已在新标签页本地完成，不再经宿主桥接。
    // 安全门控：任意网页均可 chrome.webview.postMessage，必须同时满足
    //   1) 发送消息的 document（e.Source）就是 NTP index.html；
    //   2) 当前顶层页面（core.Source）也是 NTP，排除“远程页面内嵌 NTP iframe”借用宿主能力。
    // 仅校验 core.Source 会把发信 frame 错当成顶层文档，e.Source 才是官方定义的发送方 URI。
    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var core = WebView.CoreWebView2;
        if (core == null || !IsNewTabSource(e.Source) || !IsNewTabSource(core.Source))
            return;

        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson ?? "null");
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return;
            if (!root.TryGetProperty("channel", out var channel) || channel.GetString() != NtpChannel)
                return;
            if (!root.TryGetProperty("type", out var type))
                return;

            switch (type.GetString())
            {
                case "navigate":
                    if (root.TryGetProperty("url", out var url))
                        Navigate(url.GetString() ?? string.Empty);
                    break;
                case "getConfig":
                    ReplyConfig(core);
                    break;
                case "getFavicons":
                    ReplyFavicons(core, root);
                    break;
                case "fetchEngineIcon":
                    FetchEngineIcon(core, root);
                    break;
                case "setSearchEngine":
                    if (root.TryGetProperty("engine", out var engine))
                        NewTabAction?.Invoke(this, "setSearchEngine:" + (engine.GetString() ?? string.Empty));
                    break;
                case "openSettings":
                    NewTabAction?.Invoke(this, type.GetString()!);
                    break;
            }
        }
        catch (JsonException)
        {
            // 页面发来的非 JSON / 半截消息：忽略（零遥测原则，不记录内容）
        }
    }

    // 新标签页来源判定：只接受运行时抽取的 index.html 本身，不再做目录前缀比对。
    // 目录内其他本地文件、相似前缀或远程页面的子 frame 都不具备调用宿主能力的资格。
    private static bool IsNewTabSource(string? source) =>
        !string.IsNullOrEmpty(source) &&
        string.Equals(source, WebUiAssets.IndexUri, StringComparison.OrdinalIgnoreCase);

    // 新标签页配置回复：当前只暴露搜索引擎，作为页面 localStorage 的权威值。
    private void ReplyConfig(CoreWebView2 core)
    {
        var engine = Context?.Settings?.SearchEngine ?? "bing";
        core.PostWebMessageAsJson(JsonSerializer.Serialize(
            new { channel = NtpChannel, type = "config", searchEngine = engine }));
    }

    /// <summary>
    /// 新标签页图标回复：页面只报自己当前快捷方式的主机名，宿主按需从本地缓存读，
    /// 缺图的主机不出现在结果里（页面回落字标）。上限 32 个主机，避免页面一次索要整库。
    /// </summary>
    private async void ReplyFavicons(CoreWebView2 core, JsonElement message)
    {
        var store = Context?.Favicons;
        var hosts = new List<string>();
        if (store != null &&
            message.TryGetProperty("hosts", out var array) &&
            array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (hosts.Count >= 32)
                    break;
                if (item.ValueKind == JsonValueKind.String)
                    hosts.Add(item.GetString() ?? string.Empty);
            }
        }

        var icons = new Dictionary<string, string>(StringComparer.Ordinal);
        if (store != null && hosts.Count > 0)
        {
            var bytesByHost = await store.ReadAsync(hosts).ConfigureAwait(true);
            foreach (var pair in bytesByHost)
                icons[pair.Key] = FaviconStore.ToDataUrl(pair.Value);
        }

        // 等待读盘期间页面可能已导航走：PostIcons 内部已吞掉这类异常
        PostIcons(core, icons);
    }

    // 会话级抓取记录：每个主机最多尝试一次，失败不重试（避免每次开新标签页都发请求）。
    private static readonly HashSet<string> IconFetchAttempted = new(StringComparer.Ordinal);
    private static readonly object IconFetchGate = new();

    /// <summary>
    /// 搜索引擎图标一次性抓取（唯一被批准的第二条出站路径）。
    /// 门控三层：用户开关 → 主机白名单 → 本地缓存；任一关掉都不发请求。
    /// 非白名单主机的请求直接丢弃，不回包，避免把浏览器变成通用取图代理。
    /// </summary>
    private async void FetchEngineIcon(CoreWebView2 core, JsonElement message)
    {
        if (!message.TryGetProperty("host", out var hostElement) ||
            hostElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var host = FaviconStore.NormalizeHost(hostElement.GetString());
        if (host == null || !SearchEngineHosts.IsAllowed(host))
            return;

        var empty = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Context?.Settings?.FetchEngineIcons != true)
        {
            PostIcons(core, empty);
            return;
        }

        var store = Context?.Favicons;
        if (store == null)
            return;

        // 已有缓存：直接回，不发请求（缓存可能由用户访问该站时写入）
        var cached = await store.ReadAsync(new[] { host }).ConfigureAwait(true);
        if (cached.TryGetValue(host, out var cachedBytes))
        {
            PostIcons(core, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [host] = FaviconStore.ToDataUrl(cachedBytes),
            });
            return;
        }

        bool firstAttempt;
        lock (IconFetchGate)
            firstAttempt = IconFetchAttempted.Add(host);
        if (!firstAttempt)
        {
            PostIcons(core, empty);
            return;
        }

        byte[]? fetched;
        try
        {
            fetched = await FaviconFetcher.FetchAsync(host, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception)
        {
            fetched = null; // 抓取失败只影响图标显示
        }

        if (fetched == null || fetched.Length == 0)
        {
            PostIcons(core, empty);
            return;
        }

        await store.SaveAsync(host, fetched).ConfigureAwait(true);
        PostIcons(core, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [host] = FaviconStore.ToDataUrl(fetched),
        });
    }

    // 统一的图标回包（getFavicons / fetchEngineIcon 共用）
    private void PostIcons(CoreWebView2 core, Dictionary<string, string> icons)
    {
        try
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(
                new { channel = NtpChannel, type = "favicons", icons }));
        }
        catch (Exception)
        {
            // 页面已导航走或内核已释放：丢弃
        }
    }

    // WebView2 favicon 变更：取回 PNG 流，一份解码给标签条，一份落盘给新标签页复用。
    // 这里不产生任何额外的网络请求——内核在加载该页时已经取回了图标。
    private async void OnFaviconChanged(object? sender, object e)
    {
        var core = WebView.CoreWebView2;
        if (core == null)
            return;

        ImageSource? icon = null;
        try
        {
            if (!string.IsNullOrEmpty(core.FaviconUri))
            {
                using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png).ConfigureAwait(true);
                var bytes = ReadAllBytes(stream);
                icon = DecodeIcon(bytes);

                if (bytes.Length > 0 && Context?.Favicons != null)
                {
                    var host = HostOf(core.Source);
                    if (host != null)
                        _ = Context.Favicons.SaveAsync(host, bytes);
                }
            }
        }
        catch
        {
            icon = null; // 取图标失败不影响浏览，回落占位
        }

        FaviconChanged?.Invoke(this, icon);
    }

    /// <summary>取页面主机名（仅 http/https）；本地页与新标签页返回 null。</summary>
    private static string? HostOf(string? source)
    {
        if (string.IsNullOrEmpty(source) ||
            !Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return null;

        return string.IsNullOrEmpty(uri.Host) ? null : uri.Host;
    }

    private static byte[] ReadAllBytes(Stream? stream)
    {
        if (stream == null)
            return Array.Empty<byte>();

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static ImageSource? DecodeIcon(byte[] bytes)
    {
        if (bytes.Length == 0)
            return null;

        // 用可定位的 MemoryStream：OnLoad 立即解码后即可释放源流
        using var ms = new MemoryStream(bytes);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze(); // 跨线程安全 + 可缓存
        return bmp;
    }

    /// <summary>无协议输入自动补 https://；已带协议（含 about:、file:）原样使用。</summary>
    internal static string NormalizeUrl(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.IndexOf("://", StringComparison.Ordinal) >= 0 ||
            trimmed.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return "https://" + trimmed;
    }

    private static async Task<object> CreateEnvironmentAsync(string userDataFolder, string? additionalBrowserArguments)
    {
        Directory.CreateDirectory(userDataFolder);
        var options = string.IsNullOrEmpty(additionalBrowserArguments)
            ? null
            : new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = additionalBrowserArguments };
        // 三参重载：browserExecutableFolder 传 null = 使用已安装的 WebView2 Runtime（官方文档口径）
        return await CoreWebView2Environment.CreateAsync(null, userDataFolder, options).ConfigureAwait(false);
    }

    private void TrySetDefaultBackgroundColor()
    {
        try
        {
            // csproj 未引用 System.Drawing（不可改），用反射设置 DefaultBackgroundColor=#F7F8FA 防白闪
            var prop = typeof(Microsoft.Web.WebView2.Wpf.WebView2).GetProperty("DefaultBackgroundColor");
            var fromArgb = prop?.PropertyType.GetMethod("FromArgb", new[] { typeof(int), typeof(int), typeof(int) });
            if (prop == null || fromArgb == null)
                return;

            var color = fromArgb.Invoke(null, new object[] { 0xF7, 0xF8, 0xFA });
            prop.SetValue(WebView, color);
        }
        catch
        {
            // 设置失败则退回 XAML 底色（Grid Background），不影响功能
        }
    }

    private void ShowRuntimeMissingPanel(string? title, string? detail)
    {
        if (!string.IsNullOrEmpty(title))
            PanelTitle.Text = title;

        if (!string.IsNullOrEmpty(detail))
        {
            PanelDetail.Text = detail;
            PanelDetail.Visibility = Visibility.Visible;
        }

        WebView.Visibility = Visibility.Collapsed;
        RuntimeMissingPanel.Visibility = Visibility.Visible;
    }

    private void OnDownloadRuntimeClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // R1：引导用户前往官网手动下载，绝不静默联网下载
            // .NET Core+ 中 Process.Start(string) 默认 UseShellExecute=false，无法直接打开 URL，
            // 必须显式 UseShellExecute=true 交给系统外壳用默认浏览器打开
            Process.Start(new ProcessStartInfo(RuntimeDownloadUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // 打开浏览器失败时面板仍在，用户可手动复制地址
            Trace.WriteLine($"[BrowserTabView] 打开 Runtime 下载页失败：{ex}");
        }
    }
}

/// <summary>页内查找的命中结果（<see cref="FindResultsChangedEventArgs.ActiveMatchIndex"/> 为 0 表示无活动命中）。</summary>
public sealed class FindResultsChangedEventArgs : EventArgs
{
    public FindResultsChangedEventArgs(int matchCount, int activeMatchIndex)
    {
        MatchCount = matchCount;
        ActiveMatchIndex = activeMatchIndex;
    }

    /// <summary>命中总数。</summary>
    public int MatchCount { get; }

    /// <summary>活动命中序号（1 起；0 = 无活动命中）。</summary>
    public int ActiveMatchIndex { get; }
}
