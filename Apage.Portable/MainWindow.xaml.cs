#nullable enable
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Apage.Core.Configuration;
using Apage.Core.Data;
using Apage.Core.Models;
using Apage.Core.Services;
using Apage.Portable.Services;
using Apage.Portable.Tabs;
using Apage.Portable.Views;

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
        private BrowserTabContext? _tabContext;
        private DownloadCoordinator? _downloads;
        private LibraryWindow? _library;
        private AdBlockRuleProvider? _adBlockProvider;
        private RuleUpdateService? _ruleUpdates;
        private OmniboxSuggestionService? _suggestions;

        // 启动瞬间冻结的时间线：下载维护只清理此时间之前遗留的 in_progress 行。
        private readonly DateTimeOffset _processStartedAt = DateTimeOffset.UtcNow;

        private readonly DispatcherTimer _toastTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1.8),
        };

        // 页内查找（S3）：状态机在 Core（可单测），此处只管 UI 与内核调用。
        // 输入去抖 120ms：连续敲字时不必每次按键都重开内核查找会话。
        private readonly FindBarState _findState = new FindBarState();
        private readonly DispatcherTimer _findTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };

        private BrowserTabView? _findTab;

        // R6 页级拦截计数：徽章挂载在活动标签上，切标签改挂，避免旧标签计数残留
        private BrowserTabView? _pageBlocksTab;

        // 只有成功读取并应用上次会话后才允许覆盖 session.json。
        // 启动加载期间关闭窗口、或恢复异常时保持旧文件不变，避免空/半份快照覆盖完整会话。
        private bool _sessionRestoreCompleted;

        /// <summary>多标签管理器（供 SmokeCheck 轮询当前标签内核就绪信号；无参构造时为 null）。</summary>
        internal TabManager? Tabs => _tabs;

        /// <summary>SQLite 安全初始化是否成功（SmokeCheck 的第四信号；失败不影响外壳浏览）。</summary>
        internal bool DatabaseReady { get; private set; }

        /// <summary>内置 PSL 与规则快照均验证并加载成功（SmokeCheck 的第五信号）。</summary>
        internal bool PrivacyAssetsReady { get; private set; }

        /// <summary>冒烟专用：真实构造设置窗口，验证 XAML 资源与构造链路可用（不展示、不修改设置）。</summary>
        internal void ValidateSettingsWindowConstruction()
        {
            if (_services == null)
                throw new InvalidOperationException("设置服务未装配。");
            _ = new SettingsWindow(_services.Settings, _services.LocalLog);
        }

        /// <summary>冒烟专用：真实构造书签/历史/下载窗口，验证数据服务与 XAML 绑定链路可解析（不展示）。</summary>
        internal void ValidateLibraryWindowConstruction()
        {
            if (_services == null || _downloads == null)
                throw new InvalidOperationException("数据服务未装配。");
            _ = new LibraryWindow(
                _services.Bookmarks,
                _services.History,
                _services.Downloads,
                _downloads,
                _services.LocalLog,
                _ => { });
        }

        internal void LogSmokeFailure(Exception exception) =>
            _services?.LocalLog.WriteException("smoke-settings-window", exception);

        /// <summary>冒烟专用：写一条非门禁探针结果到本地日志（不改变退出码）。</summary>
        internal void LogSmokeNote(string area, string message) =>
            _services?.LocalLog.Write(LocalLogLevel.Info, area, message);

        /// <summary>
        /// 冒烟专用：真实打开查找条并校验布局（Popup 打开、尺寸非零、不溢出内容区、可正常关闭）。
        /// 这条检查覆盖的就是 airspace 与定位这类「编译能过、真机才露」的问题。
        /// </summary>
        internal bool ValidateFindBarLayout(out string detail)
        {
            detail = string.Empty;

            if (_tabs?.Current?.View == null)
            {
                detail = "no-tab";
                return false;
            }

            try
            {
                ShowFindBar();
                if (!FindPopup.IsOpen)
                {
                    detail = "popup-not-open";
                    return false;
                }

                // 等 Input 优先级的 FocusFindInput 执行完，再检查真实 Win32 焦点。
                // 仅看 WPF IsKeyboardFocused 不够：WPF 认为焦点在 TextBox 时，键盘仍可能进入 WebView2。
                FindBox.Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.Input);
                var popupSource = PresentationSource.FromVisual(FindBar) as HwndSource;
                if (popupSource == null || GetFocus() != popupSource.Handle)
                {
                    detail = $"input-focus-not-acquired; expected={popupSource?.Handle ?? IntPtr.Zero}; actual={GetFocus()}";
                    return false;
                }

                var size = FindBar.DesiredSize;
                var hostWidth = TabHost.ActualWidth;
                var right = FindPopup.HorizontalOffset + size.Width;
                detail = $"size={size.Width:F0}x{size.Height:F0}; " +
                         $"offsetX={FindPopup.HorizontalOffset:F0}; hostWidth={hostWidth:F0}";

                if (size.Width < 200 || size.Height < 28)
                {
                    detail += "; FAIL=degenerate-size";
                    return false;
                }

                if (hostWidth > 0 && right > hostWidth + 1)
                {
                    detail += "; FAIL=overflows-content-area";
                    return false;
                }

                HideFindBar();
                if (FindPopup.IsOpen || FindBox.Text.Length != 0)
                {
                    detail += "; FAIL=close-incomplete";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                detail = "exception:" + ex.GetType().Name;
                return false;
            }
        }

        /// <summary>
        /// 冒烟专用：在真实单文件产物里做一次书签/历史往返（写入 → 读回 → 清理书签），
        /// 验证的不只是“库能打开”，而是仓储 SQL 与事务链真的可用。
        /// 探测地址使用保留的 .invalid 域，不会指向真实站点。
        /// </summary>
        internal async Task<bool> ValidateDataRoundTripAsync()
        {
            if (_services == null)
                return false;

            const string probeUrl = "https://smoke.apage.invalid/roundtrip";

            await _services.Bookmarks.SaveAsync(probeUrl, "smoke");
            var bookmarks = await _services.Bookmarks.ListAsync();
            var bookmarkOk = bookmarks.Any(b => b.Url == probeUrl);
            await _services.Bookmarks.RemoveByUrlAsync(probeUrl);

            await _services.History.RecordAsync(probeUrl, "smoke", DateTimeOffset.UtcNow);
            var history = await _services.History.QueryRecentAsync(10);
            var historyOk = history.Any(h => h.Url == probeUrl);

            return bookmarkOk && historyOk;
        }

        /// <summary>
        /// 冒烟专用：真实走一次 NTP WebMessage 桥（getConfig），验证 e.Source/core.Source
        /// 的精确来源门控没有把正常新标签页误拦截，并确认搜索引擎配置能下发。
        /// </summary>
        internal async Task<bool> ValidateNtpBridgeAsync()
        {
            if (_tabs == null)
                return false;

            var tab = _tabs.NewTab(WebUiAssets.IndexUri);
            var deadline = DateTime.UtcNow.AddSeconds(8);
            var core = tab.View?.Core;
            while (core == null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
                core = tab.View?.Core;
            }

            if (core == null)
                return false;

            while (DateTime.UtcNow < deadline &&
                   !string.Equals(core.Source, WebUiAssets.IndexUri, StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(100);
            }

            if (!string.Equals(core.Source, WebUiAssets.IndexUri, StringComparison.OrdinalIgnoreCase))
                return false;

            const string startProbe = @"(function () {
  window.__apageNtpProbe = 'pending';
  var w = window.chrome && window.chrome.webview;
  if (!w) { return false; }
  w.addEventListener('message', function (e) {
    if (e && e.data && e.data.type === 'config') {
      window.__apageNtpProbe = (typeof e.data.searchEngine === 'string' && e.data.searchEngine)
        ? 'ok' : 'bad';
    }
  });
  w.postMessage({ channel: 'ntp', type: 'getConfig' });
  return true;
})()";

            var started = await core.ExecuteScriptAsync(startProbe).ConfigureAwait(true);
            if (!string.Equals(started, "true", StringComparison.OrdinalIgnoreCase))
                return false;

            await Task.Delay(500);
            var observed = await core.ExecuteScriptAsync("window.__apageNtpProbe").ConfigureAwait(true);
            return observed?.IndexOf("ok", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 冒烟专用：在真实 NTP 页面上走一次快捷方式新增→删除→持久化恢复，
        /// 防止管理入口退化成只发消息的占位。
        /// </summary>
        internal async Task<bool> ValidateNtpShortcutsAsync()
        {
            var core = _tabs?.Current?.View?.Core;
            if (core == null ||
                !string.Equals(core.Source, WebUiAssets.IndexUri, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            const string script = @"(function () {
  var KEY = 'apage.ntp.shortcuts';
  var original = null;
  try { original = localStorage.getItem(KEY); } catch (_) { }
  try {
    // 搜索区布局门禁：主控件必须真的是大卡片，且提交钮在卡片内（防止退回小胶囊或丢按钮）
    var form = document.getElementById('search-form');
    var submit = document.getElementById('search-submit');
    if (!form || !submit) { return 'FAIL:no-search-submit'; }
    var formRect = form.getBoundingClientRect();
    if (formRect.height < 50 || formRect.width < 520) {
      return 'FAIL:search-box=' + formRect.width.toFixed(0) + 'x' + formRect.height.toFixed(0);
    }
    var submitRect = submit.getBoundingClientRect();
    if (submitRect.width < 30 || submitRect.right > formRect.right + 1) {
      return 'FAIL:submit-outside';
    }

    var before = document.querySelectorAll('#shortcuts .shortcut-wrap').length;
    var add = document.querySelector('#shortcuts .shortcut-item.add');
    if (!add) { return 'FAIL:no-add-button'; }
    add.click();

    var dialog = document.getElementById('shortcut-dialog');
    if (!dialog || !dialog.open) { return 'FAIL:dialog-not-open'; }

    // 居中门禁：临时停掉入场动画，取纯布局盒（transform 会污染 getBoundingClientRect）。
    // 若 dialog 被留在带 transform 的 #content 里，这里会偏离视口中心而直接拦下。
    var prevAnimation = dialog.style.animation;
    dialog.style.animation = 'none';
    var rect = dialog.getBoundingClientRect();
    dialog.style.animation = prevAnimation;
    if (rect.width < 100 || rect.height < 100) {
      return 'FAIL:dialog-collapsed=' + rect.width.toFixed(0) + 'x' + rect.height.toFixed(0);
    }
    var dx = Math.abs((rect.left + rect.right) / 2 - window.innerWidth / 2);
    var dy = Math.abs((rect.top + rect.bottom) / 2 - window.innerHeight / 2);
    if (dx > 1.5 || dy > 1.5) {
      return 'FAIL:not-centered dx=' + dx.toFixed(1) + ' dy=' + dy.toFixed(1);
    }

    document.getElementById('shortcut-name').value = 'Smoke';
    document.getElementById('shortcut-url').value = 'https://smoke.apage.invalid';
    document.getElementById('shortcut-form').dispatchEvent(
      new Event('submit', { bubbles: true, cancelable: true }));

    var added = document.querySelectorAll('#shortcuts .shortcut-wrap').length;
    var wraps = document.querySelectorAll('#shortcuts .shortcut-wrap');
    var last = wraps[wraps.length - 1];
    var remove = last && last.querySelector('.shortcut-remove');
    if (!remove) { return 'FAIL:no-remove-button'; }
    remove.click();

    var after = document.querySelectorAll('#shortcuts .shortcut-wrap').length;
    return (added === before + 1 && after === before)
      ? 'PASS'
      : 'FAIL:counts=' + before + '/' + added + '/' + after;
  } catch (e) {
    return 'FAIL:' + (e && e.message ? e.message : 'exception');
  } finally {
    try {
      if (original === null) localStorage.removeItem(KEY);
      else localStorage.setItem(KEY, original);
    } catch (_) { }
  }
})()";

            var raw = await core.ExecuteScriptAsync(script).ConfigureAwait(true);
            var outcome = System.Text.Json.JsonSerializer.Deserialize<string>(raw ?? "\"\"");
            if (!string.Equals(outcome, "PASS", StringComparison.Ordinal))
                return false;

            // 图标桥：宿主必须回一个 icons 对象。新标签页是 file:// 页，正常情况下查不到缓存，
            // 这里只验协议通（页面能拿到 reply），真实图片由 GetFaviconAsync 落盘后自然命中。
            const string faviconProbe = @"(function () {
  window.__apageFavicons = 'pending';
  var w = window.chrome && window.chrome.webview;
  if (!w) { return false; }
  w.addEventListener('message', function (e) {
    if (e && e.data && e.data.type === 'favicons') {
      window.__apageFavicons = (e.data.icons && typeof e.data.icons === 'object') ? 'ok' : 'bad';
    }
  });
  w.postMessage({ channel: 'ntp', type: 'getFavicons', hosts: ['example.com'] });
  return true;
})()";

            var faviconStarted = await core.ExecuteScriptAsync(faviconProbe).ConfigureAwait(true);
            if (!string.Equals(faviconStarted, "true", StringComparison.OrdinalIgnoreCase))
                return false;

            await Task.Delay(300);
            var faviconState = await core.ExecuteScriptAsync("window.__apageFavicons").ConfigureAwait(true);
            if (faviconState?.IndexOf("ok", StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            // 白名单门禁：非引擎主机的抓取请求必须被静默丢弃（不回包、不发请求）。
            // 先等前面可能的回包落地，再用哨兵观察 700ms。
            await Task.Delay(1200);
            const string denyProbe = @"(function () {
  window.__apageFavicons = 'armed';
  var w = window.chrome && window.chrome.webview;
  if (!w) { return false; }
  w.postMessage({ channel: 'ntp', type: 'fetchEngineIcon', host: 'evil.example.com' });
  w.postMessage({ channel: 'ntp', type: 'fetchEngineIcon', host: 'www.bing.com.evil.example.com' });
  return true;
})()";

            var denyStarted = await core.ExecuteScriptAsync(denyProbe).ConfigureAwait(true);
            if (!string.Equals(denyStarted, "true", StringComparison.OrdinalIgnoreCase))
                return false;

            await Task.Delay(700);
            var denyState = await core.ExecuteScriptAsync("window.__apageFavicons").ConfigureAwait(true);
            return denyState?.IndexOf("armed", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 冒烟专用：通过真实 NTP 消息切换搜索引擎，确认宿主内存、settings.json
        /// 与地址栏一致，然后恢复原值。防止“新标签页看起来保存了、重启又变回默认”。
        /// </summary>
        internal async Task<bool> ValidateNtpSearchEnginePersistenceAsync()
        {
            var core = _tabs?.Current?.View?.Core;
            if (core == null || _services == null ||
                !string.Equals(core.Source, WebUiAssets.IndexUri, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var original = _services.Settings.Current.SearchEngine;
            var target = string.Equals(original, "google", StringComparison.OrdinalIgnoreCase)
                ? "baidu"
                : "google";

            await core.ExecuteScriptAsync(
                "chrome.webview.postMessage({channel:'ntp',type:'setSearchEngine',engine:'" + target + "'});")
                .ConfigureAwait(true);

            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline &&
                   !string.Equals(_services.Settings.Current.SearchEngine, target, StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(50);
            }

            await Task.Delay(350); // 等异步 SaveAsync 落盘
            await _services.Settings.LoadAsync();
            var persisted = string.Equals(
                _services.Settings.Current.SearchEngine, target, StringComparison.OrdinalIgnoreCase);

            _services.Settings.Current.SearchEngine = original;
            Omnibox.SearchEngine = original;
            try { await _services.Settings.SaveAsync(); } catch { /* 冒烟恢复失败不掩盖主结论 */ }
            return persisted;
        }

        public MainWindow()
        {
            InitializeComponent();
        }

        /// <summary>依赖注入构造（由 App.OnStartup 调用）：建立多标签会话并接线 UI。</summary>
        public MainWindow(AppServices services, BrowserTabContext tabContext) : this()
        {
            StartupMetrics.Mark("MainWindowXamlLoaded");
            _tabContext = tabContext;
            _services = services;

            // 下载协调器：所有标签共享，落库 + 取消/打开；不改变 WebView2 自带下载提示
            _downloads = new DownloadCoordinator(services.Downloads, services.LocalLog);

            _tabs = new TabManager(TabHost, tabContext, _downloads);
            _tabs.ActiveTabChanged += OnActiveTabChanged;
            _tabs.TabAction += OnTabAction;

            Omnibox.SearchEngine = services.Settings.Current.SearchEngine;
            Omnibox.NavigationRequested += (s, url) => _tabs.Current?.View?.Navigate(url);

            // 本地建议（S3）：只查本机书签与历史，不联网
            _suggestions = new OmniboxSuggestionService(services.Bookmarks, services.History);
            Omnibox.SuggestionProvider = (text, ct) => _suggestions.QueryAsync(text, 8, ct);

            TabStrip.SetItemsSource(_tabs.Tabs);
            TabStrip.NewTabRequested += (s, e) => _tabs.NewTab();
            TabStrip.TabSelected += (s, tab) => _tabs.Activate(tab);
            TabStrip.TabCloseRequested += (s, tab) => _tabs.CloseTab(tab);
            TabStrip.TabReorderRequested += (from, to) => _tabs.MoveTab(from, to);
            StartupMetrics.Mark("MainWindowWired");

            _toastTimer.Tick += (s, e) => HideToast();
            _findTimer.Tick += (s, e) => RunFind();
            // Popup 的窗口在 Open 时才真正创建：定位与点击穿透必须等 Opened 之后再做
            ToastPopup.Opened += (s, e) =>
            {
                PositionToast();
                ApplyToastClickThrough();
            };
            // WebView2 是独立 HwndHost：Popup 打开后 WPF 焦点不会自动迁移 Win32 键盘焦点。
            // 必须在 Opened 后显式 SetFocus 到 Popup 子窗口，否则输入仍落到网页。
            FindPopup.Opened += (s, e) => FocusFindInput();
            TabHost.SizeChanged += (s, e) =>
            {
                if (FindPopup.IsOpen)
                    PositionFindPopup();
                if (ToastPopup.IsOpen)
                    PositionToast();
            };
            Closing += OnClosing;
            PreviewKeyDown += OnPreviewKeyDown;
        }

        /// <summary>
        /// Show 之后在 UI 上下文加载周统计与会话，再创建首个标签（S1-B 首屏优化）：
        /// 窗口先可见，但 NTP 呈现前统计已就绪，隐私数据语义不变。
        /// </summary>
        internal void InitializeTabs()
        {
            _ = InitializeTabsAsync();
        }

        private async Task InitializeTabsAsync()
        {
            // SQLite 初始化与首个标签并行；失败只禁用数据能力，不拖垮浏览外壳。
            _ = InitializeDatabaseAsync();

            // PSL 与内置规则必须在任何外部标签创建前就绪；解析放线程池，窗口已先行显示。
            await InitializePrivacyAssetsAsync();

            try
            {
                if (_services != null)
                    await _services.PrivacyStats.LoadAsync();
                var session = _services?.Session.LoadAsync() ?? Task.FromResult(new SessionState());
                var restored = _tabs?.RestoreSession(await session) ?? 0;
                if (restored == 0)
                    _tabs?.NewTab();
                _sessionRestoreCompleted = true;
            }
            catch (Exception ex)
            {
                _services?.LocalLog.WriteException("tabs-init", ex);
                if (_tabs != null && _tabs.Tabs.Count == 0)
                    _tabs.NewTab();
            }

            StartupMetrics.Mark("TabsReady");
        }

        private async Task InitializePrivacyAssetsAsync()
        {
            if (_services == null || _tabContext == null)
                return;

            AdBlockRuleEngine? engine = null;
            var snapshotInfo = new AdBlockRuleSnapshotInfo("builtin", 0, 0, null, null);
            var pslReady = false;
            try
            {
                var assets = await Task.Run(() => PrivacyAssets.LoadEmbedded());
                _tabContext.ThirdParty = new ThirdPartyDetector(assets.PublicSuffix);
                pslReady = true;
                engine = assets.CreateRuleEngine();
                snapshotInfo = new AdBlockRuleSnapshotInfo(
                    "builtin",
                    assets.Manifest.Rules.AcceptedRules,
                    assets.Manifest.Rules.SkippedRules,
                    assets.Manifest.GeneratedAtUtc,
                    null);
                _services.LocalLog.Write(
                    LocalLogLevel.Info,
                    "privacy-assets",
                    $"psl={assets.PublicSuffix.Version}; psl-rules={assets.PublicSuffix.RuleCount}; " +
                    $"builtin={engine.BlockCount + engine.ExceptionCount}; " +
                    $"sources={assets.Manifest.Rules.Sources.Count}");
            }
            catch (Exception ex)
            {
                _services.LocalLog.WriteException("privacy-assets", ex);
                // 规则快照失败不应连带放弃 PSL；单独重试 PSL 后仍可精确判断第三方。
                try
                {
                    var psl = await Task.Run(() => PublicSuffixList.LoadEmbedded());
                    _tabContext.ThirdParty = new ThirdPartyDetector(psl);
                    pslReady = true;
                    _services.LocalLog.Write(
                        LocalLogLevel.Info, "psl", $"fallback-path-ready; version={psl.Version}");
                }
                catch (Exception pslEx)
                {
                    _tabContext.ThirdParty = ThirdPartyDetector.Fallback;
                    _services.LocalLog.WriteException("psl-load", pslEx);
                }
            }

            // 手动更新过的本地快照优先于随包快照；校验失败则回退随包，绝不带病加载。
            try
            {
                var local = await Task.Run(() => RuleSnapshotStore.TryLoad(_services.PrivacyDirectory));
                if (local.Loaded)
                {
                    engine = local.Engine;
                    snapshotInfo = new AdBlockRuleSnapshotInfo(
                        "updated",
                        local.Manifest!.Rules.AcceptedRules,
                        local.Manifest.Rules.SkippedRules,
                        local.Manifest.GeneratedAtUtc,
                        _services.PrivacyDirectory);
                    _services.LocalLog.Write(
                        LocalLogLevel.Info,
                        "rule-snapshot",
                        $"updated; accepted={snapshotInfo.AcceptedRules}; generated={snapshotInfo.GeneratedAtUtc:o}");
                }
                else if (local.FailureReason != null)
                {
                    _services.LocalLog.Write(
                        LocalLogLevel.Warning, "rule-snapshot", "local-update-ignored: " + local.FailureReason);
                }
            }
            catch (Exception ex)
            {
                _services.LocalLog.WriteException("rule-snapshot-load", ex);
            }

            // 用户规则始终独立加载；即使内置快照损坏，用户规则仍可生效。
            // 与 RuleUpdateService 共用 UserRuleStore，保证热更新前后口径一致。
            var userRuleDirectory = Path.Combine(AppPaths.DataDirectory, "adblock");
            try
            {
                var userRules = UserRuleStore.LoadAll(userRuleDirectory);
                if (userRules.Count > 0)
                {
                    engine ??= new AdBlockRuleEngine();
                    engine.AddRules(userRules);
                }
            }
            catch (Exception ex)
            {
                _services.LocalLog.WriteException("user-rules-load", ex);
            }

            // 用户规则加载失败不能连带丢弃已通过校验的内置/更新快照。
            _tabContext.AdBlock = engine != null && engine.BlockCount > 0 ? engine : null;

            // 共享持有者：设置窗口手动更新后热替换，已打开的标签立即使用新规则。
            _adBlockProvider = new AdBlockRuleProvider(engine, snapshotInfo);
            _tabContext.AdBlockProvider = _adBlockProvider;
            var updateClient = new RuleUpdateClient();
            _ruleUpdates = new RuleUpdateService(
                _services.PrivacyDirectory,
                _adBlockProvider,
                updateClient.DownloadAsync,
                message => _services?.LocalLog.Write(LocalLogLevel.Info, "rule-update", message),
                userRuleDirectory: userRuleDirectory);

            if (engine != null)
            {
                _services.LocalLog.Write(
                    LocalLogLevel.Info,
                    "adblock",
                    $"rules={engine.BlockCount}; exceptions={engine.ExceptionCount}; " +
                    $"unsupported-modifiers={engine.UnsupportedModifierRuleCount}; " +
                    $"origin={snapshotInfo.Origin}");
            }

            PrivacyAssetsReady = pslReady && engine != null && engine.BlockCount > 0;
            StartupMetrics.Mark("PrivacyAssetsReady");
        }

        private async Task InitializeDatabaseAsync()
        {
            if (_services == null)
                return;

            try
            {
                var info = await _services.Database.InitializeAsync();
                DatabaseReady = true;

                // 数据能力装配：只有初始化成功才把历史仓储交给标签视图，
                // 避免库损坏时每次导航都重复抛错并刷日志。
                if (_tabContext != null)
                    _tabContext.History = _services.History;

                _services.LocalLog.Write(
                    LocalLogLevel.Info,
                    "sqlite",
                    $"ready; schema={info.SchemaVersion}; created={info.WasCreated}; backup={info.BackupPath != null}");

                // 维护任务（残留下载标记 + 历史保留期裁剪）留到首屏渲染之后，
                // 避免与 WebView2 初始化争抢磁盘和线程池。
                _ = Dispatcher.BeginInvoke(new Action(RunDataMaintenance), DispatcherPriority.ApplicationIdle);
            }
            catch (Exception ex)
            {
                _services.LocalLog.WriteException("sqlite-init", ex);
            }
        }

        // 启动后维护：把上次异常退出遗留的“进行中”下载标为失败，并裁剪超过 90 天的历史。
        // 全部在后台线程，失败只留诊断，不影响浏览。
        private async void RunDataMaintenance()
        {
            if (_services == null)
                return;

            try
            {
                var orphaned = await _services.Downloads.MarkOrphanedAsync(_processStartedAt);
                var pruned = await _services.History.PruneAsync(DateTimeOffset.UtcNow.AddDays(-90));
                _services.LocalLog.Write(
                    LocalLogLevel.Info,
                    "data-maintenance",
                    $"orphaned-downloads={orphaned}; pruned-history={pruned}");
            }
            catch (Exception ex)
            {
                _services.LocalLog.WriteException("data-maintenance", ex);
            }
        }

        // 当前标签切换 / 当前标签地址或标题变化 → 回填地址栏与窗口标题
        private void OnActiveTabChanged(object? sender, EventArgs e)
        {
            var cur = _tabs?.Current;
            if (cur == null)
                return;

            // 查找会话属于标签内核：切标签即收起查找条并改挂新标签的事件，避免计数串台
            if (_findState.IsOpen)
                HideFindBar();
            AttachFindTab();

            Omnibox.SetUrl(cur.Url);
            Title = string.IsNullOrWhiteSpace(cur.Title) ? "Apage" : cur.Title + " — Apage";
            TabStrip.ScrollToTab(cur); // 新建/切换后把当前标签滚入视区，杜绝被裁到屏外

            // R6：徽章跟随活动标签（含新标签首次挂载）
            AttachPageBlocksTab();
        }

        // R6：把页级拦截计数事件挂到当前标签视图；切标签时先摘旧挂新，避免计数串台。
        private void AttachPageBlocksTab()
        {
            var view = _tabs?.Current?.View;
            if (ReferenceEquals(_pageBlocksTab, view))
                return;

            if (_pageBlocksTab != null)
                _pageBlocksTab.PageBlocksChanged -= OnPageBlocksChanged;
            _pageBlocksTab = view;
            if (view != null)
                view.PageBlocksChanged += OnPageBlocksChanged;

            Omnibox.BlockedCount = view?.PageBlocks.Count ?? 0;
        }

        private void OnPageBlocksChanged(object? sender, EventArgs e)
        {
            if (sender is BrowserTabView view && !ReferenceEquals(view, _tabs?.Current?.View))
                return; // 非活动标签的计数不写徽章
            Omnibox.BlockedCount = _tabs?.Current?.View?.PageBlocks.Count ?? 0;
        }

        // 退出时保存会话（隐私标签已在 CaptureSession 内排除）与隐私统计（§3.2）；同步等待写完再关闭
        private void OnClosing(object? sender, CancelEventArgs e)
        {
            if (_services == null || _tabs == null)
                return;
            if (_sessionRestoreCompleted)
            {
                try { _services.Session.SaveAsync(_tabs.CaptureSession()).GetAwaiter().GetResult(); }
                catch (Exception ex) { _services.LocalLog.WriteException("session-save", ex); }
            }
            else
            {
                _services.LocalLog.Write(
                    LocalLogLevel.Info, "session-save", "skipped-before-restore-completed");
            }
            try { _services.PrivacyStats.SaveAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { _services.LocalLog.WriteException("privacy-save", ex); }
            _tabs.Dispose(); // 停止休眠定时器
        }

        // 新标签页动作上浮（设计 §4.2）；快捷方式增删已改为页面本地持久化。
        private void OnTabAction(object? sender, string action)
        {
            if (action == "openSettings")
            {
                OpenSettings();
                return;
            }

            const string enginePrefix = "setSearchEngine:";
            if (action.StartsWith(enginePrefix, StringComparison.Ordinal))
            {
                _ = ApplySearchEngineAsync(action.Substring(enginePrefix.Length));
                return;
            }

            _services?.LocalLog.Write(LocalLogLevel.Info, "ntp", "action-not-implemented=" + action);
        }

        // NTP 选择的搜索引擎写回宿主设置（settings.json），并同步地址栏。
        // NTP 的 localStorage 只作离线回退，宿主设置才是跨重启真源。
        private async Task ApplySearchEngineAsync(string? engineId)
        {
            if (_services == null ||
                !IsSupportedSearchEngine(engineId))
            {
                return;
            }

            var settings = _services.Settings;
            var previous = settings.Current.SearchEngine;
            settings.Current.SearchEngine = engineId!;
            Omnibox.SearchEngine = engineId!;
            try
            {
                await settings.SaveAsync();
                _services.LocalLog.Write(LocalLogLevel.Info, "settings", "search-engine=" + engineId);
            }
            catch (Exception ex)
            {
                settings.Current.SearchEngine = previous;
                Omnibox.SearchEngine = previous;
                _services.LocalLog.WriteException("search-engine-save", ex);
            }
        }

        private static bool IsSupportedSearchEngine(string? engineId) =>
            string.Equals(engineId, "baidu", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(engineId, "bing", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(engineId, "google", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(engineId, "duckduckgo", StringComparison.OrdinalIgnoreCase);

        private void HandleShortcut(string action)
        {
            switch (action)
            {
                case "bookmark":
                    ToggleBookmark();
                    break;
                case "history":
                    OpenLibrary(LibraryWindow.LibraryTab.History);
                    break;
                case "downloads":
                    OpenLibrary(LibraryWindow.LibraryTab.Downloads);
                    break;
            }
        }

        // 窗口级快捷键：焦点在工具栏/地址栏时生效；网页内焦点由 WebView2 AcceleratorKeyPressed 接管
        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 ||
                (Keyboard.Modifiers & (ModifierKeys.Alt | ModifierKeys.Shift)) != 0)
            {
                return;
            }

            switch (e.Key)
            {
                case Key.F:
                    ShowFindBar();
                    e.Handled = true;
                    break;
                case Key.D:
                    ToggleBookmark();
                    e.Handled = true;
                    break;
                case Key.H:
                    OpenLibrary(LibraryWindow.LibraryTab.History);
                    e.Handled = true;
                    break;
                case Key.J:
                    OpenLibrary(LibraryWindow.LibraryTab.Downloads);
                    e.Handled = true;
                    break;
            }
        }

        // ===== 页内查找（S3）：Ctrl+F 输入即查找 / Enter 下一个 / Shift+Enter 上一个 / Esc 关闭 =====

        // 只对「当前标签的视图」挂事件：查找会话绑定在该标签的内核上，
        // 切标签时由 OnActiveTabChanged 重挂，避免旧标签的计数写到新标签的查找条上。
        private void AttachFindTab()
        {
            var view = _tabs?.Current?.View;
            if (ReferenceEquals(_findTab, view))
                return;

            if (_findTab != null)
            {
                _findTab.FindRequested -= OnTabFindRequested;
                _findTab.FindCloseRequested -= OnTabFindCloseRequested;
                _findTab.FindResultsChanged -= OnTabFindResultsChanged;
            }

            _findTab = view;

            if (_findTab != null)
            {
                _findTab.FindRequested += OnTabFindRequested;
                _findTab.FindCloseRequested += OnTabFindCloseRequested;
                _findTab.FindResultsChanged += OnTabFindResultsChanged;
            }
        }

        private void OnTabFindRequested(object? sender, EventArgs e) => ShowFindBar();

        private void OnTabFindCloseRequested(object? sender, EventArgs e)
        {
            if (_findState.IsOpen)
                HideFindBar();
        }

        private void OnTabFindResultsChanged(object? sender, FindResultsChangedEventArgs e)
        {
            if (!_findState.IsOpen || !ReferenceEquals(sender, _findTab))
                return;

            _findState.ApplyResult(e.MatchCount, e.ActiveMatchIndex);
            FindStatus.Text = _findState.StatusText;
        }

        private void ShowFindBar()
        {
            var view = _tabs?.Current?.View;
            if (view == null)
                return;

            if (!view.CanFind)
            {
                ShowToast("当前页面暂不支持页内查找");
                return;
            }

            _findState.Open();
            FindPopup.IsOpen = true;
            PositionFindPopup();
            AttachFindTab();

            // 上次会话可能还有输入：直接按现有输入查找并全选，便于用户不删字就重打
            if (FindBox.Text.Length > 0)
                RunFind();

            // Opened 会处理首次打开；已打开时再次 Ctrl+F 也把焦点拉回查找框。
            FocusFindInput();
        }

        /// <summary>
        /// 把真实 Win32 键盘焦点移到查找 Popup。
        /// WPF 的 Popup 窗口带 WS_EX_NOACTIVATE，HwndHost（WebView2）内的焦点不会因
        /// FindBox.Focus() 自动迁出；只做 WPF Focus 会出现“光标在闪但字打进网页”的现象。
        /// </summary>
        private void FocusFindInput()
        {
            FindBox.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    try
                    {
                        if (PresentationSource.FromVisual(FindBar) is HwndSource source)
                            SetFocus(source.Handle);

                        Keyboard.Focus(FindBox);
                        FindBox.Focus();
                        FindBox.SelectAll();
                        FindBox.CaretIndex = FindBox.Text.Length;
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"[MainWindow] 查找框聚焦失败：{ex.GetType().Name}");
                    }
                }),
                DispatcherPriority.Input);
        }

        // 查找条贴内容区右上角：Popup 用相对坐标，宽度按实际期望尺寸算，窗口缩放时重算
        private void PositionFindPopup()
        {
            FindBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var width = FindBar.DesiredSize.Width;
            FindPopup.HorizontalOffset = Math.Max(8, TabHost.ActualWidth - width - 18);
            FindPopup.VerticalOffset = 12;
        }

        private void HideFindBar()
        {
            _findTimer.Stop();
            _findState.Close();

            FindPopup.IsOpen = false;
            FindBox.Text = string.Empty; // TextChanged 在 state 已关闭时是空操作
            FindStatus.Text = string.Empty;

            _findTab?.StopFind();   // 结束内核侧会话，清掉高亮
            _findTab?.FocusPage();  // 焦点交回网页
        }

        // 输入去抖到期：以当前输入重开查找会话（空输入等价于结束会话）
        private async void RunFind()
        {
            _findTimer.Stop();

            var view = _tabs?.Current?.View;
            if (view == null || !_findState.IsOpen)
                return;

            var intent = _findState.SetQuery(FindBox.Text);
            FindStatus.Text = _findState.StatusText;

            if (intent != FindBarIntent.Search)
            {
                view.StopFind();
                return;
            }

            if (!await view.StartFindAsync(_findState.Query))
                FindStatus.Text = "不可用";
        }

        private void FindBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_findState.IsOpen)
                return;

            _findTimer.Stop();
            _findTimer.Start();
        }

        // 查找条内按键：Enter / Shift+Enter / Esc；文本框与按钮都走同一条路径
        private void FindBox_PreviewKeyDown(object sender, KeyEventArgs e) =>
            HandleFindKey(e);

        private void FindBar_PreviewKeyDown(object sender, KeyEventArgs e) =>
            HandleFindKey(e);

        private void HandleFindKey(KeyEventArgs e)
        {
            var key = e.Key == Key.Enter
                ? FindKey.Enter
                : (e.Key == Key.Escape ? FindKey.Escape : FindKey.Other);

            var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            switch (_findState.MapKey(key, shift))
            {
                case FindBarIntent.Next:
                    _ = GoFindAsync(forward: true);
                    e.Handled = true;
                    break;
                case FindBarIntent.Previous:
                    _ = GoFindAsync(forward: false);
                    e.Handled = true;
                    break;
                case FindBarIntent.Close:
                    HideFindBar();
                    e.Handled = true;
                    break;
            }
        }

        // 跳转前先把未去抖的输入落到会话上，避免「刚敲完就按 Enter 跳的是旧词」
        private async Task GoFindAsync(bool forward)
        {
            var view = _tabs?.Current?.View;
            if (view == null || !_findState.IsOpen)
                return;

            _findTimer.Stop();

            if (!string.Equals(_findState.Query, FindBox.Text, StringComparison.Ordinal))
            {
                if (_findState.SetQuery(FindBox.Text) != FindBarIntent.Search)
                    return;
                if (!await view.StartFindAsync(_findState.Query))
                {
                    FindStatus.Text = "不可用";
                    return;
                }
            }

            if (forward)
                view.FindNextMatch();
            else
                view.FindPreviousMatch();
        }

        private void FindNextButton_Click(object sender, RoutedEventArgs e) => _ = GoFindAsync(forward: true);

        private void FindPrevButton_Click(object sender, RoutedEventArgs e) => _ = GoFindAsync(forward: false);

        private void FindCloseButton_Click(object sender, RoutedEventArgs e) => HideFindBar();

        private void BookmarksButton_Click(object sender, RoutedEventArgs e) =>
            OpenLibrary(LibraryWindow.LibraryTab.Bookmarks);

        // 隐私盾面板（frontend-design §5.4）：只展示本机已有数据，不做“按站点临时放行”这类未实现承诺
        private void ShieldButton_Click(object sender, RoutedEventArgs e)
        {
            if (ShieldPopup.IsOpen)
            {
                ShieldPopup.IsOpen = false;
                return;
            }

            RefreshShield();
            ShieldPopup.IsOpen = true;
        }

        private void RefreshShield()
        {
            var panel = ShieldPopup.Child as PrivacyPanel;
            if (panel == null)
            {
                panel = new PrivacyPanel();
                panel.SettingsRequested += (s, e) =>
                {
                    ShieldPopup.IsOpen = false;
                    OpenSettings();
                };
                ShieldPopup.Child = panel;
            }

            var ads = _services?.PrivacyStats.Snapshot.Ads ?? 0;

            var info = _adBlockProvider?.Info;
            var rules = info == null
                ? "规则未加载：当前不拦截广告。"
                : (info.IsBuiltin ? "规则：随包快照" : "规则：手动更新") +
                  $" · {info.AcceptedRules} 条可执行";

            var settings = _services?.Settings.Current;
            var switches = settings == null
                ? string.Empty
                : $"DNT：{(settings.SendDoNotTrack ? "开" : "关")} · " +
                  $"GPC：{(settings.SendGPC ? "开" : "关")}";

            panel.Refresh(ads, rules, switches);
        }

        // 用户脚本属 S5（v0.1 之后）；这里给明确说明，避免按钮点了没反应
        private void ScriptsButton_Click(object sender, RoutedEventArgs e) =>
            ShowToast("用户脚本功能计划在 v0.1 之后提供");

        private void DownloadsButton_Click(object sender, RoutedEventArgs e) =>
            OpenLibrary(LibraryWindow.LibraryTab.Downloads);

        private void OpenLibrary(LibraryWindow.LibraryTab tab)
        {
            if (_services == null || _downloads == null)
                return;

            try
            {
                if (_library != null)
                {
                    _library.SelectTab(tab);
                    _library.Activate();
                    return;
                }

                var window = new LibraryWindow(
                    _services.Bookmarks,
                    _services.History,
                    _services.Downloads,
                    _downloads,
                    _services.LocalLog,
                    url => _tabs?.NewTab(url),
                    tab)
                {
                    Owner = this,
                };
                window.Closed += (s, e) => _library = null;
                _library = window;
                window.Show();
            }
            catch (Exception ex)
            {
                _services.LocalLog.WriteException("library-window", ex);
                ShowToast("无法打开书签/历史窗口");
            }
        }

        // Ctrl+D：收藏当前页面 / 取消收藏（只对真实网页生效，本地新标签页不写库）
        private async void ToggleBookmark()
        {
            if (_services == null)
                return;

            var tab = _tabs?.Current;
            var url = tab?.Url;
            if (!UrlKey.IsWebUrl(url))
            {
                ShowToast("当前页面无法收藏");
                return;
            }

            try
            {
                if (await _services.Bookmarks.ContainsAsync(url))
                {
                    await _services.Bookmarks.RemoveByUrlAsync(url);
                    ShowToast("已取消收藏");
                }
                else
                {
                    await _services.Bookmarks.SaveAsync(url!, tab!.Title);
                    ShowToast("已添加到书签");
                }
            }
            catch (Exception ex)
            {
                _services.LocalLog.WriteException("bookmark-toggle", ex);
                ShowToast("书签操作失败");
            }
        }

        // 提示条与查找条同一类问题：WebView2 是 HwndHost 子窗口，放在内容区 Grid 里的 Border
        // 会被网页整块盖住（airspace），真机上「已添加到书签」这类反馈根本看不见。
        // 因此改为 Popup（独立窗口）承载，并置 WS_EX_TRANSPARENT 保持点击穿透、不抢焦点。
        private void ShowToast(string message)
        {
            ToastText.Text = message;
            ToastPopup.IsOpen = true;   // Opened 事件里做首次定位与穿透设置
            PositionToast();
            ApplyToastClickThrough();
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        private void HideToast()
        {
            _toastTimer.Stop();
            ToastPopup.IsOpen = false;
        }

        // 内容区底部居中：Popup 用相对坐标，宽度按实际期望尺寸算，窗口缩放时重算
        private void PositionToast()
        {
            if (!ToastPopup.IsOpen)
                return;

            Toast.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = Toast.DesiredSize;
            var hostWidth = TabHost.ActualWidth;
            var hostHeight = TabHost.ActualHeight;

            ToastPopup.HorizontalOffset = Math.Max(8, (hostWidth - size.Width) / 2);
            ToastPopup.VerticalOffset = Math.Max(8, hostHeight - size.Height - 24);
        }

        // 提示条只展示不交互：把 Popup 自己的窗口置为「鼠标穿透 + 不激活」，
        // 避免这 1.8 秒内挡住网页底部居中位置的点击。取不到句柄时静默降级（提示仍可见）。
        private void ApplyToastClickThrough()
        {
            try
            {
                var hwnd = (PresentationSource.FromVisual(Toast) as HwndSource)?.Handle ?? IntPtr.Zero;
                if (hwnd == IntPtr.Zero)
                    return;

                var style = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[MainWindow] 提示条点击穿透设置失败：{ex.GetType().Name}");
            }
        }

        /// <summary>
        /// 冒烟专用：校验标签条「+」是否紧跟最后一个标签（夸克式），
        /// 溢出时是否退化为独立列固定右侧。位置退化属代码回归。
        /// </summary>
        internal bool ValidateTabStripLayout(out string detail)
        {
            detail = string.Empty;
            try
            {
                return TabStrip.IsNewTabButtonAdjacentToLastTab(out detail);
            }
            catch (Exception ex)
            {
                detail = "exception:" + ex.GetType().Name;
                return false;
            }
        }

        /// <summary>
        /// 冒烟专用：真实打开提示条并校验布局（Popup 打开、尺寸非零、不溢出内容区、关闭后状态干净）。
        /// 与查找条同属 airspace 风险，布局退化即代码回归，故作为门禁项。
        /// </summary>
        internal bool ValidateToastLayout(out string detail)
        {
            detail = string.Empty;

            try
            {
                ShowToast("已添加到书签");
                if (!ToastPopup.IsOpen)
                {
                    detail = "popup-not-open";
                    return false;
                }

                var size = Toast.DesiredSize;
                var hostWidth = TabHost.ActualWidth;
                var hostHeight = TabHost.ActualHeight;
                detail = $"size={size.Width:F0}x{size.Height:F0}; " +
                         $"offsetX={ToastPopup.HorizontalOffset:F0}; offsetY={ToastPopup.VerticalOffset:F0}; " +
                         $"host={hostWidth:F0}x{hostHeight:F0}";

                if (size.Width < 60 || size.Height < 20)
                {
                    detail += "; FAIL=degenerate-size";
                    return false;
                }

                if (hostWidth > 0 && ToastPopup.HorizontalOffset + size.Width > hostWidth + 1)
                {
                    detail += "; FAIL=overflows-content-area-x";
                    return false;
                }

                if (hostHeight > 0 && ToastPopup.VerticalOffset + size.Height > hostHeight + 1)
                {
                    detail += "; FAIL=overflows-content-area-y";
                    return false;
                }

                HideToast();
                if (ToastPopup.IsOpen)
                {
                    detail += "; FAIL=close-incomplete";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                detail = "exception:" + ex.GetType().Name;
                return false;
            }
        }

        private void MenuButton_Click(object sender, RoutedEventArgs e) => OpenSettings();

        private void OpenSettings()
        {
            if (_services == null)
                return;

            try
            {
                var window = new SettingsWindow(
                    _services.Settings, _services.LocalLog, _ruleUpdates, _adBlockProvider, _tabContext?.SiteShields)
                {
                    Owner = this,
                };
                if (window.ShowDialog() == true)
                {
                    Omnibox.SearchEngine = _services.Settings.Current.SearchEngine;
                    App.ApplyTheme(_services.Settings.Current.Theme); // 外观即时换肤（画刷走 DynamicResource）
                    _services.LocalLog.Write(LocalLogLevel.Info, "settings", "settings-applied");
                }
            }
            catch (Exception ex)
            {
                _services.LocalLog.WriteException("settings-window", ex);
                MessageBox.Show(
                    "设置窗口无法打开。诊断信息已写入本地日志。",
                    "Apage", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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

        // ===== 提示条点击穿透（Toast Popup）=====
        // Toast 用 Popup 才能浮在 WebView2 之上；POPUP 窗口默认会吞掉自身矩形内的鼠标，
        // 因此需要 WS_EX_TRANSPARENT：不阻挡下方网页的点击，同时不激活、不抢焦点。
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_NOACTIVATE = 0x00000080;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();
    }
}
