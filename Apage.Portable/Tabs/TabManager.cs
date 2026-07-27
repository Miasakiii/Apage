#nullable enable
using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Apage.Core.Models;
using Apage.Core.Services;
using Apage.Portable.Views;

namespace Apage.Portable.Tabs
{
    /// <summary>
    /// 多标签会话管理（Phase 2）：为「活动」标签各创建一个 BrowserTabView，全部宿主于同一容器 Panel，
    /// 仅当前标签可见（Visibility 切换，避免重建内核）；所有标签经注入的 BrowserLifecycleService
    /// 共享同一 Environment（决策 #9：共享隔离环境，省内存）。
    /// 休眠：非活跃标签空闲超阈值即释放其内核（View 置空、仅留可恢复元数据），下次激活时懒物化；
    /// 会话恢复亦为懒加载（仅物化当前标签，其余保持休眠直到被点击）。
    /// 重排：MoveTab 仅改集合顺序，内核按可见性宿主、与显示顺序无关，无需重排 _host。
    /// </summary>
    public sealed class TabManager : IDisposable
    {
        // 休眠策略：非当前标签空闲超过该时长即释放内核；定时器周期扫描
        private static readonly TimeSpan HibernateAfter = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan HibernateScanInterval = TimeSpan.FromSeconds(60);

        private readonly Panel _host;
        private readonly BrowserLifecycleService _lifecycle;
        private readonly AdBlockRuleEngine? _adBlock;
        private readonly DispatcherTimer _hibernateTimer;
        private bool _disposed;

        public TabManager(Panel host, BrowserLifecycleService lifecycle, AdBlockRuleEngine? adBlock = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
            _adBlock = adBlock; // 可选：全部标签共享同一引擎实例（规则索引只建一份）

            _hibernateTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = HibernateScanInterval,
            };
            _hibernateTimer.Tick += (s, e) => HibernateIdleTabs();
            _hibernateTimer.Start();
        }

        /// <summary>所有标签（供 TabStrip 绑定，集合顺序即显示顺序）。</summary>
        public ObservableCollection<BrowserTab> Tabs { get; } = new ObservableCollection<BrowserTab>();

        /// <summary>当前激活标签（无标签时为 null）。</summary>
        public BrowserTab? Current { get; private set; }

        /// <summary>当前标签切换，或当前标签的 URL/标题变化时触发（宿主据此回填地址栏与窗口标题、滚入视区）。</summary>
        public event EventHandler? ActiveTabChanged;

        /// <summary>新建标签并（默认）激活；url 为空则为空白标签。新标签始终追加在集合末尾（夸克式）。</summary>
        public BrowserTab NewTab(string? url = null, bool activate = true)
        {
            var tab = new BrowserTab();
            if (!string.IsNullOrEmpty(url))
                tab.Url = url!; // 前置写入：休眠标签也能被会话捕获，物化时据此导航

            Tabs.Add(tab); // 唯一插入路径，恒追加末尾

            if (activate)
                Activate(tab);      // 前台标签：物化 + 显示 +（有 url 则）导航
            // 后台标签：保持休眠（无 View），首次被激活时才懒物化

            return tab;
        }

        /// <summary>激活指定标签：懒物化目标（若休眠）、仅其可见、刷新其活跃时间。</summary>
        public void Activate(BrowserTab tab)
        {
            if (tab == null || Current == tab)
                return;

            MaterializeView(tab); // 休眠标签在此复活（重建内核并导航到保存的 Url）

            foreach (var t in Tabs)
            {
                var isTarget = t == tab;
                t.IsCurrent = isTarget;
                if (t.View != null)
                    t.View.Visibility = isTarget ? Visibility.Visible : Visibility.Collapsed;
            }

            tab.LastActiveTime = DateTime.UtcNow;
            Current = tab;
            ActiveTabChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>把第 from 个标签移动到第 to 个位置（拖拽重排；仅改集合顺序，内核不动）。</summary>
        public void MoveTab(int from, int to)
        {
            var last = Tabs.Count - 1;
            if (from < 0 || from > last || to < 0 || to > last || from == to)
                return;

            Tabs.Move(from, to);
        }

        /// <summary>
        /// 关闭标签：释放其内核（若有）；若关的是当前标签则激活相邻标签；
        /// 关掉最后一个标签则关闭窗口（Chrome 语义）。
        /// </summary>
        public void CloseTab(BrowserTab tab)
        {
            var index = Tabs.IndexOf(tab);
            if (index < 0)
                return;

            var wasCurrent = tab.IsCurrent;
            Tabs.Remove(tab);
            ReleaseView(tab); // 休眠标签无 View，安全跳过

            if (Tabs.Count == 0)
            {
                Window.GetWindow(_host)?.Close(); // 关掉最后一个标签 = 关闭浏览器窗口
                return;
            }

            if (wasCurrent)
            {
                Current = null; // 迫使 Activate 生效（否则相邻标签恰为 Current 时会被短路）
                Activate(Tabs[Math.Min(index, Tabs.Count - 1)]);
            }
        }

        /// <summary>
        /// 捕获当前会话（退出时保存）：排除隐私标签（roadmap 红线）与不可恢复地址；
        /// ActiveIndex 指向捕获后列表中的当前标签。休眠标签同样被捕获（元数据保留）。
        /// </summary>
        public SessionState CaptureSession()
        {
            var state = new SessionState();
            foreach (var t in Tabs)
            {
                if (t.IsPrivate) continue;                          // 隐私标签绝不写入
                if (!SessionService.IsRestorableUrl(t.Url)) continue; // 空白/about: 等不恢复
                if (t.IsCurrent) state.ActiveIndex = state.Tabs.Count;
                state.Tabs.Add(new SessionTab { Url = t.Url, Title = t.Title });
            }
            return state;
        }

        /// <summary>
        /// 恢复会话（启动时调用，预期管理器为空）：为每个已保存标签建「休眠」标签（不物化内核），
        /// 最后仅激活并物化保存的当前标签 —— 懒加载恢复，启动只有一个活动内核，其余点击时才加载。
        /// 返回恢复的标签数（为 0 时宿主应自行开一个空白标签）。
        /// </summary>
        public int RestoreSession(SessionState state)
        {
            var normalized = SessionService.Normalize(state);
            foreach (var t in normalized.Tabs)
            {
                var tab = NewTab(t.Url, activate: false); // 后台：保持休眠
                // 先用保存的标题占位：休眠标签在被激活加载前也能显示正确标题，
                // 页面加载后再由 DocumentTitleChanged 覆盖。
                if (!string.IsNullOrWhiteSpace(t.Title))
                    tab.Title = t.Title;
            }

            if (Tabs.Count == 0)
                return 0;

            Activate(Tabs[normalized.ActiveIndex]);
            return normalized.Tabs.Count;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _hibernateTimer.Stop();
        }

        // ===== 物化 / 休眠 =====

        // 为标签懒物化 WebView 宿主：创建控件、接线事件、加入容器、（有保存地址则）导航
        private void MaterializeView(BrowserTab tab)
        {
            if (tab.View != null)
                return;

            var view = new BrowserTabView
            {
                Lifecycle = _lifecycle,       // 必须在加入可视树（Loaded）前赋值
                AdBlock = _adBlock,           // 同上：内核就绪时据此决定是否挂 WebResourceRequested
                Visibility = Visibility.Collapsed,
            };

            // 页面标题/地址变化 -> 更新标签模型；若为当前标签则通知宿主刷新地址栏与窗口标题
            view.TitleChanged += (s, title) =>
            {
                tab.Title = string.IsNullOrWhiteSpace(title) ? "新标签页" : title;
                if (tab.IsCurrent) ActiveTabChanged?.Invoke(this, EventArgs.Empty);
            };
            view.UrlChanged += (s, u) =>
            {
                tab.Url = u;
                if (tab.IsCurrent) ActiveTabChanged?.Invoke(this, EventArgs.Empty);
            };
            view.FaviconChanged += (s, img) => tab.Favicon = img;

            tab.View = view;
            _host.Children.Add(view);

            if (!string.IsNullOrEmpty(tab.Url))
                view.Navigate(tab.Url); // 内核未就绪时会自动暂存
        }

        // 休眠标签：释放内核、移出容器、View 置空，保留标题/地址/图标元数据
        private void Hibernate(BrowserTab tab)
        {
            if (tab == Current)
                return; // 当前标签永不休眠
            ReleaseView(tab);
        }

        // 释放并移除标签的 View（关闭与休眠共用）；休眠标签（View==null）安全跳过
        private void ReleaseView(BrowserTab tab)
        {
            var view = tab.View;
            if (view == null)
                return;

            tab.View = null;
            _host.Children.Remove(view);
            view.DisposeCore(); // 释放该标签内核（共享 Environment 不在此释放）
        }

        // 定时扫描：把非当前、空闲超阈值的活动标签休眠
        private void HibernateIdleTabs()
        {
            if (_disposed)
                return;

            var now = DateTime.UtcNow;
            foreach (var tab in Tabs)
            {
                if (tab == Current || tab.IsHibernated)
                    continue;
                if (now - tab.LastActiveTime >= HibernateAfter)
                    Hibernate(tab);
            }
        }
    }
}
