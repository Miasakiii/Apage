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
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logs = logs ?? throw new ArgumentNullException(nameof(logs));
        _ruleUpdates = ruleUpdates;
        _ruleProvider = ruleProvider;

        InitializeComponent();
        _draft = Clone(settings.Current);
        PopulateUi(_draft);

        CachePathText.Text = "当前缓存：" + AppPaths.CacheDirectory;
        LogPathText.Text = "日志位置：" + logs.CurrentLogPath;
        LogPathText.ToolTip = logs.CurrentLogPath;

        RefreshRuleInfo();
        if (_ruleUpdates == null)
        {
            UpdateRulesButton.IsEnabled = false;
            RuleSourceText.Text = "规则服务当前不可用（内置快照可能未成功加载）。";
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

        ThirdPartyCookieToggle.IsChecked = source.BlockThirdPartyCookies;
        DntToggle.IsChecked = source.SendDoNotTrack;
        GpcToggle.IsChecked = source.SendGPC;
        WebRtcToggle.IsChecked = source.WebRTCIPProtection;
        PortableCacheToggle.IsChecked = source.PortableCache;
    }

    private void CaptureUi(AppSettings target)
    {
        target.SearchEngine = (SearchEngineBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "bing";
        target.BlockThirdPartyCookies = ThirdPartyCookieToggle.IsChecked == true;
        target.SendDoNotTrack = DntToggle.IsChecked == true;
        target.SendGPC = GpcToggle.IsChecked == true;
        target.WebRTCIPProtection = WebRtcToggle.IsChecked == true;
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
        target.BlockThirdPartyCookies = source.BlockThirdPartyCookies;
        target.SendDoNotTrack = source.SendDoNotTrack;
        target.SendGPC = source.SendGPC;
        target.WebRTCIPProtection = source.WebRTCIPProtection;
        target.Theme = source.Theme;
        target.OnlineSearchSuggestions = source.OnlineSearchSuggestions;
        target.ClearOnExit = source.ClearOnExit;
    }
}
