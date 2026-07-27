#nullable enable
using System;
using System.Windows.Controls;
using System.Windows.Input;

namespace Apage.Portable.Controls;

/// <summary>
/// 地址栏 Omnibox 代码后置（docs/frontend-design.md §5.3）。
/// Phase 1 接线范围：回车提交导航（URL/搜索智能识别）+ 外部回填当前地址。
/// 建议下拉（历史/书签补全）待 Phase 3 数据层就绪后接入；联网建议默认关（隐私基线）。
/// </summary>
public partial class Omnibox : UserControl
{
    private bool _suppressReflect; // SetUrl 回填期间抑制 TextChanged 副作用

    public Omnibox()
    {
        InitializeComponent();
    }

    /// <summary>用户提交导航（Enter）。参数为可直接交给 BrowserTabView.Navigate 的 URL 或搜索 URL。</summary>
    public event EventHandler<string>? NavigationRequested;

    /// <summary>搜索引擎标识（bing/google/duckduckgo）。由宿主从设置注入。</summary>
    public string SearchEngine { get; set; } = "bing";

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
        // 预留：输入变化时刷新建议下拉（联网建议默认关，见隐私基线）
    }

    private void UrlBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var text = UrlBox.Text?.Trim();
            if (!string.IsNullOrEmpty(text))
                NavigationRequested?.Invoke(this, ResolveQuery(text!));
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Keyboard.ClearFocus(); // 失焦即可；失焦后下次页面回填会恢复当前地址
            e.Handled = true;
        }
    }

    /// <summary>把输入解析为可导航 URL：像地址则原样交由归一化补协议，否则用搜索引擎构造查询 URL。</summary>
    private string ResolveQuery(string input)
    {
        if (LooksLikeUrl(input))
            return input; // 交给 BrowserTabView.NormalizeUrl 补 https://

        var q = Uri.EscapeDataString(input);
        switch (SearchEngine?.ToLowerInvariant())
        {
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

    private void SuggestPopup_Closed(object sender, EventArgs e)
    {
        // 预留：下拉关闭时清理选中态
    }

    private void SuggestItem_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 预留：点击建议项后导航
    }
}
