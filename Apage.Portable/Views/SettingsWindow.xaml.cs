#nullable enable
using System;
using System.Windows;
using System.Windows.Controls;
using Apage.Core.Configuration;
using Apage.Core.Models;
using Apage.Core.Services;
using Microsoft.Win32;

namespace Apage.Portable.Views;

/// <summary>
/// 真实设置窗口（C1）：只展示已有消费方的设置；保存采用“先写盘、成功后再替换内存引用”，
/// 失败时回滚到保存前值。WebRTC 与缓存位置明确标注重启生效。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly LocalLogService _logs;
    private readonly AppSettings _draft;
    private readonly RuleUpdateService? _ruleUpdates;
    private readonly AdBlockRuleProvider? _ruleProvider;
    private readonly SiteShieldStore? _siteShields;
    private bool _saving;
    private bool _updatingRules;

    public SettingsWindow(SettingsService settings, LocalLogService logs)
        : this(settings, logs, null, null)
    {
    }

    public SettingsWindow(
        SettingsService settings,
        LocalLogService logs,
        RuleUpdateService? ruleUpdates,
        AdBlockRuleProvider? ruleProvider)
        : this(settings, logs, ruleUpdates, ruleProvider, null)
    {
    }

    public SettingsWindow(
        SettingsService settings,
        LocalLogService logs,
        RuleUpdateService? ruleUpdates,
        AdBlockRuleProvider? ruleProvider,
        SiteShieldStore? siteShields)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logs = logs ?? throw new ArgumentNullException(nameof(logs));
        _ruleUpdates = ruleUpdates;
        _ruleProvider = ruleProvider;
        _siteShields = siteShields;

        InitializeComponent();
        _draft = Clone(settings.Current);
        PopulateUi(_draft);

        CachePathText.Text = "当前缓存：" + AppPaths.CacheDirectory;
        LogPathText.Text = "日志位置：" + logs.CurrentLogPath;
        LogPathText.ToolTip = logs.CurrentLogPath;

        RefreshRuleInfo();
        RefreshSiteShieldList();
        if (_ruleUpdates == null)
        {
            UpdateRulesButton.IsEnabled = false;
            RuleSourceText.Text = "规则服务当前不可用（内置快照可能未成功加载）。";
        }
    }

    private void RefreshSiteShieldList()
    {
        if (_siteShields == null)
        {
            SiteShieldHint.Text = "按站点覆盖当前不可用。";
            AddSiteShieldButton.IsEnabled = false;
            SiteShieldInput.IsEnabled = false;
            return;
        }

        var sites = _siteShields.Snapshot();
        SiteShieldList.Items.Clear();
        foreach (var (host, entry) in sites)
        {
            var text = entry.Disabled ? host + "（已关闭防护）" : host;
            if (!string.IsNullOrWhiteSpace(entry.Note))
                text += " — " + entry.Note;
            SiteShieldList.Items.Add(new ComboBoxItem
            {
                Content = text,
                Tag = host,
            });
        }
        SiteShieldHint.Text = sites.Count == 0
            ? "还没有按站点覆盖。为被误伤的站点关闭防护，不会改动上面的全局默认开关。"
            : "关闭某站点只影响该站点；全局默认开关不变。";
    }

    private async void AddSiteShield_Click(object sender, RoutedEventArgs e)
    {
        if (_siteShields == null)
            return;

        var host = SiteShieldInput.Text;
        if (string.IsNullOrWhiteSpace(host))
        {
            SiteShieldHint.Text = "请输入站点主机名（如 news.example.com）。";
            return;
        }

        AddSiteShieldButton.IsEnabled = false;
        try
        {
            var ok = await _siteShields.SetAsync(host, disabled: true, note: SiteShieldNote.Text);
            if (!ok)
            {
                SiteShieldHint.Text = "主机名无效（仅接受字母数字与 . - _，≤255 字符）。";
                return;
            }
            SiteShieldInput.Text = string.Empty;
            SiteShieldNote.Text = string.Empty;
            RefreshSiteShieldList();
            _logs.Write(LocalLogLevel.Info, "site-shield", "site-shield-added");
        }
        catch (Exception ex)
        {
            _logs.WriteException("site-shield-add", ex);
            SiteShieldHint.Text = "保存失败；该站点覆盖未生效。诊断信息已写入本地日志。";
        }
        finally
        {
            AddSiteShieldButton.IsEnabled = true;
        }
    }

    private async void RemoveSiteShield_Click(object sender, RoutedEventArgs e)
    {
        if (_siteShields == null)
            return;
        if (SiteShieldList.SelectedItem is not ComboBoxItem item || item.Tag is not string host)
        {
            SiteShieldHint.Text = "请先选择要移除的站点。";
            return;
        }

        RemoveSiteShieldButton.IsEnabled = false;
        try
        {
            await _siteShields.RemoveAsync(host);
            RefreshSiteShieldList();
            _logs.Write(LocalLogLevel.Info, "site-shield", "site-shield-removed");
        }
        catch (Exception ex)
        {
            _logs.WriteException("site-shield-remove", ex);
            SiteShieldHint.Text = "移除失败；该站点覆盖未改动。";
        }
        finally
        {
            RemoveSiteShieldButton.IsEnabled = true;
        }
    }

    private void RefreshRuleInfo()
    {
        var info = _ruleProvider?.Info;
        if (info == null)
        {
            RuleSourceText.Text = "尚未加载规则。";
            RuleDateText.Text = string.Empty;
            return;
        }

        RuleSourceText.Text = info.IsBuiltin
            ? $"当前：随包快照 · {info.AcceptedRules} 条可执行"
            : $"当前：手动更新 · {info.AcceptedRules} 条可执行";
        RuleDateText.Text = info.GeneratedAtUtc is DateTimeOffset generated
            ? $"生成时间：{generated.ToLocalTime():yyyy-MM-dd HH:mm}（本机时间）"
            : "内置快照随 Apage 版本发布，手动更新后立即生效。";
    }

    private async void UpdateRules_Click(object sender, RoutedEventArgs e)
    {
        if (_ruleUpdates == null || _updatingRules)
            return;

        var confirm = MessageBox.Show(
            this,
            "将从 easylist.to 下载 EasyList 与 EasyPrivacy 两份规则（HTTPS），\n" +
            "筛选校验通过后替换本地规则并立即生效。\n\n" +
            "Apage 只在你点击时联网，不会后台自动更新。继续？",
            "更新拦截规则",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK)
            return;

        _updatingRules = true;
        UpdateRulesButton.IsEnabled = false;
        StatusText.Text = "正在下载并校验规则…";

        try
        {
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(3));
            var outcome = await _ruleUpdates.UpdateAsync(timeout.Token);
            StatusText.Text = outcome.Message;
            RefreshRuleInfo();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "更新超时，现有规则未改动。";
        }
        catch (Exception ex)
        {
            _logs.WriteException("rule-update", ex);
            StatusText.Text = "更新失败，现有规则未改动。诊断信息已写入本地日志。";
        }
        finally
        {
            _updatingRules = false;
            UpdateRulesButton.IsEnabled = true;
        }
    }

    private void PopulateUi(AppSettings source)
    {
        foreach (ComboBoxItem item in SearchEngineBox.Items)
        {
            if (string.Equals(item.Tag as string, source.SearchEngine, StringComparison.OrdinalIgnoreCase))
            {
                SearchEngineBox.SelectedItem = item;
                break;
            }
        }
        if (SearchEngineBox.SelectedItem == null)
            SearchEngineBox.SelectedIndex = 0;

        DntToggle.IsChecked = source.SendDoNotTrack;
        GpcToggle.IsChecked = source.SendGPC;
        WebRtcToggle.IsChecked = source.WebRTCIPProtection;
        FetchEngineIconsToggle.IsChecked = source.FetchEngineIcons;
        PortableCacheToggle.IsChecked = source.PortableCache;
    }

    private void CaptureUi(AppSettings target)
    {
        target.SearchEngine = (SearchEngineBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "bing";
        target.SendDoNotTrack = DntToggle.IsChecked == true;
        target.SendGPC = GpcToggle.IsChecked == true;
        target.WebRTCIPProtection = WebRtcToggle.IsChecked == true;
        target.FetchEngineIcons = FetchEngineIconsToggle.IsChecked == true;
        target.PortableCache = PortableCacheToggle.IsChecked == true;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_saving)
            return;

        _saving = true;
        SaveButton.IsEnabled = false;
        StatusText.Text = "正在保存…";

        CaptureUi(_draft);
        var live = _settings.Current;
        var rollback = Clone(live);
        CopyInto(_draft, live);

        try
        {
            await _settings.SaveAsync();
            _logs.Write(LocalLogLevel.Info, "settings", "settings-saved");
            DialogResult = true;
        }
        catch (Exception ex)
        {
            CopyInto(rollback, live);
            _logs.WriteException("settings-save", ex);
            StatusText.Text = "保存失败；设置未生效。可导出诊断日志后重试。";
        }
        finally
        {
            _saving = false;
            SaveButton.IsEnabled = true;
        }
    }

    private async void ExportLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出 Apage 诊断日志",
            FileName = "apage-diagnostics.log",
            DefaultExt = ".log",
            Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            AddExtension = true,
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
            return;

        ExportLogButton.IsEnabled = false;
        StatusText.Text = "正在导出日志…";
        try
        {
            var exported = await _logs.TryExportAsync(dialog.FileName);
            StatusText.Text = exported
                ? "日志已导出。"
                : "暂无可导出的日志，或写入目标失败。";
        }
        finally
        {
            ExportLogButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private static AppSettings Clone(AppSettings source) => source with { };

    private static void CopyInto(AppSettings source, AppSettings target)
    {
        target.SearchEngine = source.SearchEngine;
        target.PortableCache = source.PortableCache;
        target.SendDoNotTrack = source.SendDoNotTrack;
        target.SendGPC = source.SendGPC;
        target.WebRTCIPProtection = source.WebRTCIPProtection;
        target.FetchEngineIcons = source.FetchEngineIcons;
        target.Theme = source.Theme;
        target.OnlineSearchSuggestions = source.OnlineSearchSuggestions;
        target.ClearOnExit = source.ClearOnExit;
    }
}
