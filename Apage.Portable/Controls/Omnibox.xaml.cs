#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Apage.Core.Services;

namespace Apage.Portable.Controls;

/// <summary>地址栏下拉的一行（只读绑定）。</summary>
public sealed class SuggestionRow
{
    public SuggestionRow(string title, string url, Geometry? icon = null)
    {
        Title = title;
        Url = url;
        Icon = icon;
    }

    /// <summary>模板按 Kind 决定显示哪一层；本实现只有普通条目。</summary>
    public string Kind => "Entry";

    public string Title { get; }

    public string Url { get; }

    public Geometry? Icon { get; }
}

/// <summary>
/// 地址栏 Omnibox 代码后置（docs/frontend-design.md §5.3）。
/// 回车导航（URL/搜索智能识别）+ 外部回填当前地址 + 本地建议下拉（S3）：
/// 建议只来自本机书签与历史（<see cref="OmniboxSuggestionService"/>），不联网、不上报输入。
/// </summary>
public partial class Omnibox : UserControl
{
    private bool _suppressReflect; // SetUrl 回填期间抑制 TextChanged 副作用
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _inflight;

    public Omnibox()
    {
        InitializeComponent();

        _debounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        _debounce.Tick += OnDebounceTick;
    }

    /// <summary>用户提交导航（Enter）。参数为可直接交给 BrowserTabView.Navigate 的 URL 或搜索 URL。</summary>
    public event EventHandler<string>? NavigationRequested;

    /// <summary>搜索引擎标识（bing/google/duckduckgo）。由宿主从设置注入。</summary>
    public string SearchEngine { get; set; } = "bing";

    /// <summary>
    /// 本地建议提供者（由宿主注入）。为空时完全不显示下拉——
    /// 这也是数据库不可用时的自然降级：地址栏照常输入与导航。
    /// </summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<OmniboxSuggestion>>>? SuggestionProvider { get; set; }

    /// <summary>外部回填当前地址（页面 SourceChanged 时）。正在输入（已聚焦）时不覆盖，以免打断用户。</summary>
    public void SetUrl(string url)
    {
        if (UrlBox.IsKeyboardFocusWithin)
            return;
        _suppressReflect = true;
        UrlBox.Text = url;
        _suppressReflect = false;
    }

    private void UrlBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        UrlBox.SelectAll(); // 聚焦时全选地址文本，便于直接改写
    }

    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressReflect)
            return;

        _debounce.Stop();
        if (SuggestionProvider == null || string.IsNullOrWhiteSpace(UrlBox.Text))
        {
            CloseSuggestions();
            return;
        }

        _debounce.Start();
    }

    private async void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounce.Stop();
        var text = UrlBox.Text?.Trim();
        var provider = SuggestionProvider;
        if (string.IsNullOrEmpty(text) || provider == null)
        {
            CloseSuggestions();
            return;
        }

        _inflight?.Cancel();
        var cts = new CancellationTokenSource();
        _inflight = cts;
        try
        {
            var items = await provider(text!, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested || !UrlBox.IsKeyboardFocusWithin)
                return;
            ShowSuggestions(items);
        }
        catch (OperationCanceledException)
        {
            // 输入已变化，丢弃这轮结果
        }
        catch (Exception)
        {
            // 数据层不可用（例如库损坏）时静默关闭下拉，地址栏照常可用
            CloseSuggestions();
        }
    }

    private void ShowSuggestions(IReadOnlyList<OmniboxSuggestion> items)
    {
        if (items.Count == 0)
        {
            CloseSuggestions();
            return;
        }

        var star = TryFindResource("Apage.Geo.Star") as Geometry;
        var clock = TryFindResource("Apage.Geo.History") as Geometry;
        var rows = items
            .Select(i => new SuggestionRow(
                string.IsNullOrWhiteSpace(i.Title) ? i.Url : i.Title,
                i.Url,
                icon: i.IsBookmark ? star : clock))
            .ToArray();

        SuggestList.ItemsSource = rows;
        SuggestList.SelectedIndex = -1;
        SuggestPopup.IsOpen = true;
    }

    private void CloseSuggestions()
    {
        _inflight?.Cancel();
        SuggestPopup.IsOpen = false;
        SuggestList.ItemsSource = null;
    }

    private void UrlBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (SuggestPopup.IsOpen)
        {
            var count = SuggestList.Items.Count;
            if (e.Key == Key.Down && count > 0)
            {
                SuggestList.SelectedIndex = Math.Min(count - 1, SuggestList.SelectedIndex + 1);
                SuggestList.ScrollIntoView(SuggestList.SelectedItem);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Up && count > 0)
            {
                SuggestList.SelectedIndex = Math.Max(0, SuggestList.SelectedIndex - 1);
                SuggestList.ScrollIntoView(SuggestList.SelectedItem);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape)
            {
                CloseSuggestions();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter && SuggestList.SelectedItem is SuggestionRow row)
            {
                NavigateTo(row.Url);
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Enter)
        {
            var text = UrlBox.Text?.Trim();
            if (!string.IsNullOrEmpty(text))
                NavigationRequested?.Invoke(this, ResolveQuery(text!));
            CloseSuggestions();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Keyboard.ClearFocus(); // 失焦即可；失焦后下次页面回填会恢复当前地址
            e.Handled = true;
        }
    }

    private void NavigateTo(string url)
    {
        CloseSuggestions();
        _suppressReflect = true;
        UrlBox.Text = url;
        _suppressReflect = false;
        NavigationRequested?.Invoke(this, url);
    }

    /// <summary>把输入解析为可导航 URL：像地址则原样交由归一化补协议，否则用搜索引擎构造查询 URL。</summary>
    private string ResolveQuery(string input)
    {
        if (LooksLikeUrl(input))
            return input; // 交给 BrowserTabView.NormalizeUrl 补 https://

        var q = Uri.EscapeDataString(input);
        switch (SearchEngine?.ToLowerInvariant())
        {
            case "baidu": return "https://www.baidu.com/s?wd=" + q;
            case "google": return "https://www.google.com/search?q=" + q;
            case "duckduckgo": return "https://duckduckgo.com/?q=" + q;
            default: return "https://www.bing.com/search?q=" + q;
        }
    }

    /// <summary>含空格 → 搜索；含协议/专有前缀/localhost/含点且无空格 → 视为地址。</summary>
    private static bool LooksLikeUrl(string input)
    {
        if (input.IndexOf(' ') >= 0)
            return false;
        if (input.IndexOf("://", StringComparison.Ordinal) >= 0)
            return true;
        if (input.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return input.IndexOf('.') >= 0; // 含点且无空格，视为域名
    }

    private void UrlBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // 等点击先落地：若焦点进了下拉项，就由点击处理导航并关闭
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!SuggestList.IsKeyboardFocusWithin)
                SuggestPopup.IsOpen = false;
        }), DispatcherPriority.Input);
    }

    private void SuggestPopup_Closed(object sender, EventArgs e)
    {
        SuggestList.SelectedIndex = -1;
    }

    private void SuggestItem_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is SuggestionRow row)
            NavigateTo(row.Url);
    }
}
