#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Apage.Core.Configuration;
using Apage.Core.Models;
using Apage.Core.Services;
using Apage.Portable.Views;

namespace Apage.Portable
{
    /// <summary>
    /// 应用入口装配（无 StartupUri）：单实例判定（R13）→ 加载设置（隐私默认全开基线）→
    /// 按 R7 将缓存策略灌给 AppPaths → 创建内核生命周期门面 → 依赖注入 MainWindow。
    /// 退出时释放单实例 Mutex 与共享 Environment。
    /// </summary>
    public partial class App : Application
    {
        private AppServices? _services;
        private BrowserLifecycleService? _lifecycle;
        private bool _smokeMode;

        protected override void OnStartup(StartupEventArgs e)
        {
            StartupMetrics.Arm(e.Args);
            StartupMetrics.Mark("OnStartup");
            base.OnStartup(e);

            // 发布线冒烟模式（--smoke）：复用真实启动路径，五信号验证后自行退出（见 SmokeCheck）
            _smokeMode = SmokeCheck.IsRequested(e.Args);
            _services = new AppServices(AppPaths.DataDirectory);
            HookLocalDiagnostics(_services.LocalLog);
            if (_smokeMode)
                SmokeCheck.HookCrashFastExit(this);

            // 单实例判定（R13）：Mutex 名含数据目录哈希；已有实例持有则提示并退出
            if (!_services.SingleInstance.TryAcquire(out _))
            {
                if (!_smokeMode)
                {
                    // 冒烟模式无人值守，绝不弹阻塞对话框，只经退出码回传冲突
                    MessageBox.Show(
                        "Apage 已在运行（同一数据目录只允许一个实例）。",
                        "Apage", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                StartupMetrics.Complete("single-instance-conflict", SmokeCheck.ExitSingleInstanceConflict);
                _services.Dispose();
                _services = null;
                Shutdown(_smokeMode ? SmokeCheck.ExitSingleInstanceConflict : 0);
                return;
            }

            // 单文件原生库必须在 WebView2/SQLite 首次 P/Invoke 前预加载。
            // 失败时诚实中止并保留诊断，不静默降级到伴随 DLL。
            try
            {
                NativeBootstrap.Initialize();
                _services.LocalLog.Write(LocalLogLevel.Info, "native", "native-libraries-ready");
            }
            catch (Exception ex)
            {
                _services.LocalLog.WriteException("native-bootstrap", ex);
                if (!_smokeMode)
                {
                    MessageBox.Show(
                        "Apage 无法初始化内置组件，应用将退出。\n可查看 data\\logs\\apage.log 获取诊断信息。",
                        "Apage", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                StartupMetrics.Complete("native-bootstrap-failed", 1);
                _services.Dispose();
                _services = null;
                Shutdown(1);
                return;
            }

            // 加载设置（隐私默认全开），并把缓存策略灌给 AppPaths（R7：默认宿主机 %TEMP%，纯便携才写 exe 同级）
            _services.InitializeAsync().GetAwaiter().GetResult();
            AppPaths.PortableCache = _services.Settings.Current.PortableCache;
            AppPaths.EnsureCreated();
            StartupMetrics.Mark("SettingsAndStatsReady");

            // WebRTC IP 泄露防护（§5.2）：Environment 创建时一次性生效（普通/隐私环境同参数，§4.4）
            var webrtcArgs = _services.Settings.Current.WebRTCIPProtection
                ? "--force-webrtc-ip-handling-policy=disable_non_proxied_udp"
                : null;

            // 内核生命周期门面：普通标签共享同一 Environment（Phase 2 多标签复用同一内核，省内存）
            _lifecycle = BrowserTabView.CreateLifecycleService(webrtcArgs);
            StartupMetrics.Mark("LifecycleReady");

            // 新标签页资产落盘（§4.3）：空白标签的导航目标须在窗口创建前就绪
            WebUiAssets.EnsureExtracted();
            StartupMetrics.Mark("NtpAssetsReady");

            // 标签依赖包（设置/统计/拦截/生命周期）经 MainWindow → TabManager → 各 BrowserTabView 下发
            var tabContext = new BrowserTabContext
            {
                Settings = _services.Settings.Current,
                Stats = _services.PrivacyStats,
                Lifecycle = _lifecycle,
            };
            // 内置 PSL/规则与用户规则在窗口首帧后加载（MainWindow.InitializeTabsAsync），
            // 避免 2MB+ 快照解析阻塞 ContentRendered。
            StartupMetrics.Mark("PrivacyAssetsDeferred");

            StartupMetrics.Mark("WindowConstructionStarting");
            var window = new MainWindow(_services, tabContext);
            StartupMetrics.Mark("WindowConstructed");
            MainWindow = window;
            window.ContentRendered += (s, args) => StartupMetrics.Mark("ContentRendered");
            StartupMetrics.Mark("WindowShowStarting");
            window.Show();
            StartupMetrics.Mark("WindowShown");
            window.InitializeTabs(); // 统计/会话/首个标签在首帧之后加载，不阻塞窗口可见

            if (_smokeMode)
                SmokeCheck.Start(this, window); // 看门狗接管：信号齐备/超时后 Shutdown 带退出码
        }

        private void HookLocalDiagnostics(LocalLogService logs)
        {
            DispatcherUnhandledException += (s, e) =>
                logs.WriteException("ui-unhandled", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                    logs.WriteException("appdomain-unhandled", ex);
            };
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                logs.WriteException("task-unobserved", e.Exception);
                e.SetObserved();
            };
        }

        protected override void OnExit(ExitEventArgs e)
        {
            StartupMetrics.Complete("exited", e.ApplicationExitCode);
            try
            {
                _services?.LocalLog.Write(
                    LocalLogLevel.Info, "app", "exit-code=" + e.ApplicationExitCode);
            }
            catch
            {
                // 日志失败不得阻断退出
            }
            _lifecycle?.Dispose();
            _services?.Dispose(); // 释放统计定时器与单实例 Mutex
            base.OnExit(e);
        }
    }
}
