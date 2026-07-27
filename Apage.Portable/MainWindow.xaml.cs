#nullable enable
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Apage.Core.Services;
using Apage.Portable.Tabs;

namespace Apage.Portable
{
    /// <summary>
    /// 主窗口外壳：标签条 + 工具栏 + 多标签内容区（frontend-design.md §5.1）。
    /// 拖窗/双击最大化由 WindowChrome 处理；此处把工具栏导航、Omnibox、TabStrip
    /// 接到 TabManager 的当前标签（回车导航 / 地址回填 / 窗口标题同步 / 新建关闭切换）。
    /// </summary>
    public partial class MainWindow : Window
    {
        private AppServices? _services;
        private TabManager? _tabs;

        public MainWindow()
        {
            InitializeComponent();
        }

        /// <summary>依赖注入构造（由 App.OnStartup 调用）：建立多标签会话并接线 UI。adBlock 为 null 表示未启用广告拦截。</summary>
        public MainWindow(AppServices services, BrowserLifecycleService lifecycle, AdBlockRuleEngine? adBlock = null) : this()
        {
            _tabs = new TabManager(TabHost, lifecycle, adBlock);
            _tabs.ActiveTabChanged += OnActiveTabChanged;

            _services = services;
            Omnibox.SearchEngine = services.Settings.Current.SearchEngine;
            Omnibox.NavigationRequested += (s, url) => _tabs.Current?.View?.Navigate(url);

            TabStrip.SetItemsSource(_tabs.Tabs);
            TabStrip.NewTabRequested += (s, e) => _tabs.NewTab();
            TabStrip.TabSelected += (s, tab) => _tabs.Activate(tab);
            TabStrip.TabCloseRequested += (s, tab) => _tabs.CloseTab(tab);
            TabStrip.TabReorderRequested += (from, to) => _tabs.MoveTab(from, to);

            // 恢复上次会话；无可恢复标签则开一个空白标签
            var session = services.Session.LoadAsync().GetAwaiter().GetResult();
            if (_tabs.RestoreSession(session) == 0)
                _tabs.NewTab();

            Closing += OnClosing;
        }

        // 当前标签切换 / 当前标签地址或标题变化 → 回填地址栏与窗口标题
        private void OnActiveTabChanged(object? sender, EventArgs e)
        {
            var cur = _tabs?.Current;
            if (cur == null)
                return;
            Omnibox.SetUrl(cur.Url);
            Title = string.IsNullOrWhiteSpace(cur.Title) ? "Apage" : cur.Title + " — Apage";
            TabStrip.ScrollToTab(cur); // 新建/切换后把当前标签滚入视区，杜绝被裁到屏外
        }

        // 退出时保存会话（隐私标签已在 CaptureSession 内排除）；同步等待写完再关闭
        private void OnClosing(object? sender, CancelEventArgs e)
        {
            if (_services == null || _tabs == null)
                return;
            try { _services.Session.SaveAsync(_tabs.CaptureSession()).GetAwaiter().GetResult(); }
            catch { /* 保存失败不阻断退出 */ }
            _tabs.Dispose(); // 停止休眠定时器
        }

        private void BackButton_Click(object sender, RoutedEventArgs e) => _tabs?.Current?.View?.GoBack();

        private void ForwardButton_Click(object sender, RoutedEventArgs e) => _tabs?.Current?.View?.GoForward();

        private void ReloadButton_Click(object sender, RoutedEventArgs e) => _tabs?.Current?.View?.Reload();

        // ===== 最大化边缘裁切修复（WM_GETMINMAXINFO）=====
        // WPF 自定义标题栏窗口最大化时默认外扩到 (-8,-8)+屏幕+16，导致边缘内容被裁。
        // 拦截 WM_GETMINMAXINFO，把最大化位置/尺寸限定到所在显示器工作区，消除裁切并不遮挡任务栏。
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WndProc);
        }

        private const int WM_GETMINMAXINFO = 0x0024;
        private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_GETMINMAXINFO)
                return IntPtr.Zero;

            try
            {
                var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (monitor != IntPtr.Zero)
                {
                    var mmi = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO));
                    var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                    if (GetMonitorInfo(monitor, ref info))
                    {
                        var work = info.rcWork;
                        var mon = info.rcMonitor;
                        mmi.ptMaxPosition.X = work.Left - mon.Left;
                        mmi.ptMaxPosition.Y = work.Top - mon.Top;
                        mmi.ptMaxSize.X = work.Right - work.Left;
                        mmi.ptMaxSize.Y = work.Bottom - work.Top;
                        Marshal.StructureToPtr(mmi, lParam, true);
                        handled = true;
                    }
                }
            }
            catch
            {
                handled = false; // 出错回落系统默认最大化行为，绝不因消息钉子异常崩溃
            }
            return IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    }
}
