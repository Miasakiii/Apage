#nullable enable
using System;
using System.Windows;

namespace Apage.Portable.Views;

/// <summary>
/// 隐私盾面板（S3）。主窗口只在首次点击时实例化它，因此这块 XAML 不参与启动解析。
/// 只展示已有真实数据的项：本周拦截数、当前规则来源与条数、三个隐私开关状态。
/// </summary>
public partial class PrivacyPanel : System.Windows.Controls.UserControl
{
    public PrivacyPanel()
    {
        InitializeComponent();
    }

    /// <summary>点击“打开设置”。</summary>
    public event EventHandler? SettingsRequested;

    public void Refresh(long ads, long trackers, string rules, string switches)
    {
        AdsText.Text = ads.ToString();
        TrackersText.Text = trackers.ToString();
        RulesText.Text = rules;
        SwitchesText.Text = switches;
    }

    private void Settings_Click(object sender, RoutedEventArgs e) =>
        SettingsRequested?.Invoke(this, EventArgs.Empty);
}
