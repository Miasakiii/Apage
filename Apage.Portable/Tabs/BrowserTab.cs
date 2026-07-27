#nullable enable
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Apage.Portable.Views;

namespace Apage.Portable.Tabs
{
    /// <summary>
    /// 一个浏览器标签的会话状态 + （可选的）WebView 宿主。
    /// Title / IsCurrent / Favicon 供 TabStrip 绑定；Url 供切换标签时回填地址栏与休眠复活导航。
    /// <see cref="View"/> 为空即「休眠态」：内核已释放，仅保留可恢复元数据（标题/地址/图标），
    /// 下次激活时由 TabManager 重新物化。View 的创建与释放统一由 TabManager 管理
    /// （多标签共享同一 Environment）。
    /// </summary>
    public sealed class BrowserTab : INotifyPropertyChanged
    {
        private string _title = "新标签页";
        private string _url = string.Empty;
        private bool _isCurrent;
        private ImageSource? _favicon;

        /// <summary>标签唯一标识（会话保存/去重用，Phase 3）。</summary>
        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>是否为隐私标签（Phase 7+）。为 true 的标签绝不写入会话快照（roadmap 红线）。</summary>
        public bool IsPrivate { get; set; }

        /// <summary>
        /// 该标签的 WebView 宿主控件；为 null 表示已休眠（内核释放，元数据保留）。
        /// 由 TabManager 在物化/休眠时赋值/置空。
        /// </summary>
        public BrowserTabView? View { get; set; }

        /// <summary>是否处于休眠态（无活动内核）。</summary>
        public bool IsHibernated => View == null;

        /// <summary>最近一次成为当前标签的时间（休眠策略据此判定空闲时长）。</summary>
        public DateTime LastActiveTime { get; set; } = DateTime.UtcNow;

        /// <summary>标签标题（随页面 DocumentTitle 更新；休眠期间保留最后已知标题）。</summary>
        public string Title
        {
            get => _title;
            set { if (_title != value) { _title = value; OnPropertyChanged(); } }
        }

        /// <summary>当前地址（随页面 SourceChanged 更新；切换标签时回填地址栏，休眠复活时据此导航）。</summary>
        public string Url
        {
            get => _url;
            set { if (_url != value) { _url = value; OnPropertyChanged(); } }
        }

        /// <summary>是否为当前激活标签（驱动 TabStrip 高亮）。</summary>
        public bool IsCurrent
        {
            get => _isCurrent;
            set { if (_isCurrent != value) { _isCurrent = value; OnPropertyChanged(); } }
        }

        /// <summary>站点图标（随页面 favicon 更新；null 时 TabStrip 显示占位块；休眠期间保留）。</summary>
        public ImageSource? Favicon
        {
            get => _favicon;
            set { if (!ReferenceEquals(_favicon, value)) { _favicon = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
