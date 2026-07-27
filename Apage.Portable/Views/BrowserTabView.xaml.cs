#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Apage.Core.Configuration;
using Apage.Core.Services;
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
    private string? _pendingUrl;

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
    /// 宿主可注入内核生命周期门面：普通标签经此共享同一 Environment（Phase 2 多标签复用同一内核）。
    /// 与 <see cref="SharedEnvironment"/> 二选一；均未设置时按 AppPaths（R7）自建。须在 Loaded 前赋值。
    /// </summary>
    public BrowserLifecycleService? Lifecycle { get; set; }

    /// <summary>
    /// 宿主可注入广告拦截引擎（§5.3，多标签共享同一实例，纯内存匹配无 IO）。须在 Loaded 前赋值。
    /// 为 null 或无规则时完全不挂 WebResourceRequested——该管道会让页面加载暂停等待 UI 线程处理
    /// （官方文档明确的性能开销），按需注册。
    /// </summary>
    public AdBlockRuleEngine? AdBlock { get; set; }

    /// <summary>底层 CoreWebView2（未就绪时为 null）。供广告拦截/隐私等事件管道（§5.4）挂接。</summary>
    public CoreWebView2? Core => WebView.CoreWebView2;

    public bool CanGoBack => WebView.CoreWebView2?.CanGoBack == true;

    public bool CanGoForward => WebView.CoreWebView2?.CanGoForward == true;

    /// <summary>创建 BrowserLifecycleService 的工厂适配（Core 层不引用 WebView2 SDK，经此委托门面创建 Environment）。</summary>
    public static BrowserLifecycleService CreateLifecycleService() =>
        new BrowserLifecycleService(CreateEnvironmentAsync);

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

            // 环境解析优先级：显式注入的 SharedEnvironment > 生命周期门面共享 Environment > 按 R7 自建
            var environment = SharedEnvironment;
            if (environment == null)
            {
                environment = Lifecycle != null
                    ? (CoreWebView2Environment)await Lifecycle.GetSharedEnvironmentAsync().ConfigureAwait(true)
                    : (CoreWebView2Environment)await CreateEnvironmentAsync(Path.Combine(AppPaths.CacheDirectory, "WebView2"))
                        .ConfigureAwait(true);
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

        // 完整事件链入口（§5.4）：后续广告拦截/DNT 头注入/历史记录等在此管道扩展
        core.DocumentTitleChanged += (s, e) => TitleChanged?.Invoke(this, core.DocumentTitle);
        core.SourceChanged += (s, e) => UrlChanged?.Invoke(this, core.Source);
        core.FaviconChanged += OnFaviconChanged;
        core.NavigationCompleted += (s, e) => { /* 预留：历史记录、拦截统计等 */ };

        HookAdBlock(core);

        WebView.Visibility = Visibility.Visible;

        if (!string.IsNullOrEmpty(_pendingUrl))
        {
            var url = _pendingUrl!;
            _pendingUrl = null;
            Navigate(url);
        }
    }

    // §5.3 广告拦截接入 WebView2 网络管道：
    // - 必须用带 RequestSourceKinds 的三参过滤器重载（两参重载已被官方弃用：
    //   跨源 iframe 的子资源不会触发 WebResourceRequested，广告 iframe 恰是重灾区）
    // - 处理器同步判定即返回（引擎实测均值 24µs/URL，无需 GetDeferral）
    // - 不拦截 Document 主文档：引擎暂不求值 $ 修饰符，泛匹配规则误杀主导航的代价过高
    private void HookAdBlock(CoreWebView2 core)
    {
        var engine = AdBlock;
        if (engine == null || engine.BlockCount == 0)
            return;

        core.AddWebResourceRequestedFilter(
            "*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += (s, e) =>
        {
            if (e.ResourceContext == CoreWebView2WebResourceContext.Document)
                return;
            if (engine.IsBlocked(e.Request.Uri))
            {
                // 空内容 + 403 即取消该请求（args.Response 一经赋值请求不再放行）
                e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            }
        };
    }

    // WebView2 favicon 变更：取回 PNG 流解码为 ImageSource（无图标则 null），广播给宿主回填标签。
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
                icon = DecodeIcon(stream);
            }
        }
        catch
        {
            icon = null; // 取图标失败不影响浏览，回落占位
        }

        FaviconChanged?.Invoke(this, icon);
    }

    private static ImageSource? DecodeIcon(Stream? stream)
    {
        if (stream == null)
            return null;

        // 拷到可定位的 MemoryStream，OnLoad 立即解码后即可释放源流
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        if (ms.Length == 0)
            return null;
        ms.Position = 0;

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

    private static async Task<object> CreateEnvironmentAsync(string userDataFolder)
    {
        Directory.CreateDirectory(userDataFolder);
        return await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder).ConfigureAwait(false);
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
