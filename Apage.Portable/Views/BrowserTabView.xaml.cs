#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
    /// 宿主可注入共享 Environment（通常来自 BrowserLifecycleService.GetSharedEnvironmentAsync()）。
    /// 不注入则按 AppPaths 缓存策略（R7）自行创建。必须在控件 Loaded 前赋值才生效。
    /// </summary>
    public CoreWebView2Environment? SharedEnvironment { get; set; }

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

            var environment = SharedEnvironment
                ?? (CoreWebView2Environment)await CreateEnvironmentAsync(Path.Combine(AppPaths.CacheDirectory, "WebView2"))
                    .ConfigureAwait(true);

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
        core.NavigationCompleted += (s, e) => { /* 预留：历史记录、拦截统计等 */ };

        WebView.Visibility = Visibility.Visible;

        if (!string.IsNullOrEmpty(_pendingUrl))
        {
            var url = _pendingUrl!;
            _pendingUrl = null;
            Navigate(url);
        }
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
