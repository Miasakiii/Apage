#nullable enable
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Apage.Core.Services;
using Apage.Portable.Views;

namespace Apage.Portable
{
    /// <summary>
    /// 发布线冒烟验证（--smoke）：复用真实启动路径，五信号齐备立即退出——
    /// ① 应用可启动（OnStartup 正常走完）② 主窗口可呈现（ContentRendered 触发）
    /// ③ WebView2 运行时可初始化（当前标签 CoreWebView2 就绪）
    /// ④ SQLite 在真实（单文件）产物中完成安全初始化
    /// ⑤ 内置 PSL 与离线规则快照通过哈希校验并加载。
    /// WinExe 无控制台，结果只经进程退出码回传，由 build/smoke.ps1 翻译为通过/失败：
    /// 0=通过；2=等待信号超时；3=单实例冲突；4=WebView2 Runtime 未安装；其余非零=启动崩溃。
    /// 仅本地/CI 冒烟使用，不产生任何额外联网请求（零遥测红线不受影响）。
    /// </summary>
    internal static class SmokeCheck
    {
        // 内部看门狗：CI 冷启动首次拉起 WebView2 内核较慢，给足冗余
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

        internal const int ExitTimeout = 2;
        internal const int ExitSingleInstanceConflict = 3;
        internal const int ExitRuntimeMissing = 4;

        /// <summary>命令行是否请求冒烟模式。</summary>
        internal static bool IsRequested(string[] args)
        {
            foreach (var arg in args)
            {
                if (string.Equals(arg, "--smoke", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 冒烟模式下把未处理异常转为确定的非零退出码：
        /// 避免 WER 崩溃对话框把无人值守的冒烟进程挂死（否则脚本只能靠外层超时强杀）。
        /// </summary>
        internal static void HookCrashFastExit(Application app)
        {
            app.DispatcherUnhandledException += (s, e) =>
            {
                Trace.WriteLine($"[Smoke] UI 线程未处理异常：{e.Exception}");
                StartupMetrics.Complete("startup-crash", 1);
                Environment.Exit(1);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Trace.WriteLine($"[Smoke] 未处理异常：{e.ExceptionObject}");
                StartupMetrics.Complete("startup-crash", 1);
                Environment.Exit(1);
            };
        }

        /// <summary>主窗口 Show 之后启动看门狗：轮询五信号，齐备则 0 退出，超时则以 2 退出。</summary>
        internal static void Start(Application app, MainWindow window)
        {
            // 信号③前置条件：Runtime 未安装时 BrowserTabView 只会显示 R1 引导面板、
            // 内核永不就绪——直接给出明确退出码，而不是拖到超时。
            if (!WebView2RuntimeService.Detect().IsInstalled)
            {
                Trace.WriteLine("[Smoke] WebView2 Runtime 未安装");
                StartupMetrics.Complete("webview2-runtime-missing", ExitRuntimeMissing);
                app.Shutdown(ExitRuntimeMissing);
                return;
            }

            // 窗口构造校验延后到首帧之后：构造 Settings/Library 窗口会解析整份 XAML，
            // 若放在 ContentRendered 之前，会污染启动基准里“窗口可见”的时间口径。
            var windowsValidated = false;
            var rendered = false;
            window.ContentRendered += (s, e) =>
            {
                rendered = true; // 信号②
                StartupMetrics.Mark("ContentRendered");
            };

            var deadline = DateTime.UtcNow + Timeout;
            var dataCheckStarted = false;
            var dataChecked = false;
            var findProbeStarted = false;
            var findProbeDone = false;
            var ntpProbeStarted = false;
            var ntpProbeDone = false;
            var timer = new DispatcherTimer { Interval = PollInterval };
            timer.Tick += async (s, e) =>
            {
                if (rendered && !windowsValidated)
                {
                    try
                    {
                        // 真实构造设置窗口与数据窗口，暴露 XAML 资源缺失/构造异常，但不展示、不修改数据。
                        window.ValidateSettingsWindowConstruction();
                        window.ValidateLibraryWindowConstruction();
                        windowsValidated = true;
                    }
                    catch (Exception ex)
                    {
                        timer.Stop();
                        window.LogSmokeFailure(ex);
                        Trace.WriteLine("[Smoke] 窗口构造失败：" + ex.GetType().FullName);
                        StartupMetrics.Complete("window-construction-failed", 1);
                        app.Shutdown(1);
                        return;
                    }
                }

                // 真实数据往返：书签写入/读回/删除 + 历史写入/读回，验证仓储 SQL 与事务链
                if (rendered && windowsValidated && window.DatabaseReady && !dataCheckStarted)
                {
                    dataCheckStarted = true;
                    try
                    {
                        dataChecked = await window.ValidateDataRoundTripAsync();
                    }
                    catch (Exception ex)
                    {
                        window.LogSmokeFailure(ex);
                        dataChecked = false;
                    }

                    if (!dataChecked)
                    {
                        timer.Stop();
                        Trace.WriteLine("[Smoke] 数据往返失败（书签或历史未能读回）");
                        StartupMetrics.Complete("data-roundtrip-failed", 1);
                        app.Shutdown(1);
                        return;
                    }
                }

                // 页内查找探针（S3，非门禁信号）：在真实内核上走一遍
                // CreateFindOptions → StartAsync →（等待命中回报）→ Stop，验证 1.0.4078 SDK
                // 的 Find 会话与命中计数在当前 Runtime 上确实可用。
                // Runtime 过旧时功能本就按「不可用」降级，所以这里只记录结果，不改变退出码。
                if (!findProbeStarted && window.Tabs?.Current?.View?.Core != null)
                {
                    findProbeStarted = true;
                    var view = window.Tabs!.Current!.View!;
                    var findAvailable = view.CanFind;
                    var findMatches = 0;

                    if (findAvailable)
                    {
                        // 命中计数由 MatchCountChanged 异步回报：用事件等结果，而不是固定 sleep
                        var reported = new TaskCompletionSource<int>();
                        EventHandler<FindResultsChangedEventArgs> onResults =
                            (s, e2) => reported.TrySetResult(e2.MatchCount);
                        view.FindResultsChanged += onResults;

                        try
                        {
                            findAvailable = await view.StartFindAsync("本周已拦截");
                            if (findAvailable)
                            {
                                var first = await Task.WhenAny(reported.Task, Task.Delay(1500));
                                if (ReferenceEquals(first, reported.Task))
                                    findMatches = reported.Task.Result;
                            }
                        }
                        catch (Exception ex)
                        {
                            findAvailable = false;
                            window.LogSmokeFailure(ex);
                        }
                        finally
                        {
                            view.FindResultsChanged -= onResults;
                            view.StopFind();
                        }
                    }

                    Trace.WriteLine($"[Smoke] 页内查找探针：available={findAvailable}, matches={findMatches}");
                    window.LogSmokeNote("find-probe", $"available={findAvailable}; matches={findMatches}");

                    // 查找条布局探针（可门禁）：真实打开 Popup 并校验尺寸/定位/关闭。
                    // 布局退化属代码回归，不是环境差异，因此这一条失败即判冒烟失败。
                    if (findAvailable)
                    {
                        if (!window.ValidateFindBarLayout(out var layoutDetail))
                        {
                            timer.Stop();
                            Trace.WriteLine("[Smoke] 查找条布局校验失败：" + layoutDetail);
                            window.LogSmokeNote("find-layout-failed", layoutDetail);
                            StartupMetrics.Complete("find-layout-failed", 1);
                            app.Shutdown(1);
                            return;
                        }

                        window.LogSmokeNote("find-layout", layoutDetail);
                    }

                    // 提示条布局探针（可门禁）：Toast 与查找条同属 Popup（airspace），
                    // 一旦被改回 Grid 覆盖层或定位溢出，属代码回归而非环境差异。
                    if (!window.ValidateToastLayout(out var toastDetail))
                    {
                        timer.Stop();
                        Trace.WriteLine("[Smoke] 提示条布局校验失败：" + toastDetail);
                        window.LogSmokeNote("toast-layout-failed", toastDetail);
                        StartupMetrics.Complete("toast-layout-failed", 1);
                        app.Shutdown(1);
                        return;
                    }

                    window.LogSmokeNote("toast-layout", toastDetail);

                    findProbeDone = true;
                }

                // NTP WebMessage 桥（可门禁）：从真实新标签页发送 getStats，
                // 验证 e.Source/core.Source 精确来源校验不会误杀正常宿主通信。
                if (findProbeDone && !ntpProbeStarted)
                {
                    ntpProbeStarted = true;
                    bool bridgeOk;
                    try
                    {
                        bridgeOk = await window.ValidateNtpBridgeAsync();
                    }
                    catch (Exception ex)
                    {
                        bridgeOk = false;
                        window.LogSmokeFailure(ex);
                    }

                    window.LogSmokeNote("ntp-bridge", bridgeOk ? "pass" : "fail");
                    if (!bridgeOk)
                    {
                        timer.Stop();
                        Trace.WriteLine("[Smoke] NTP WebMessage 桥校验失败");
                        StartupMetrics.Complete("ntp-bridge-failed", 1);
                        app.Shutdown(1);
                        return;
                    }

                    ntpProbeDone = true;
                }

                // 信号③：当前标签 CoreWebView2 非空 = Environment 创建 + 内核进程初始化成功
                // 信号④：SQLite 初始化完成（验证单文件中的托管/原生依赖链真实可用）
                if (rendered && windowsValidated && dataChecked && findProbeDone && ntpProbeDone &&
                    window.Tabs?.Current?.View?.Core != null &&
                    window.DatabaseReady && window.PrivacyAssetsReady)
                {
                    timer.Stop();
                    StartupMetrics.Mark("CoreReady");
                    StartupMetrics.Complete("pass", 0);
                    app.Shutdown(0);
                    return;
                }

                if (DateTime.UtcNow >= deadline)
                {
                    timer.Stop();
                    var coreReady = window.Tabs?.Current?.View?.Core != null;
                    Trace.WriteLine(
                        $"[Smoke] 超时：rendered={rendered}, windowsValidated={windowsValidated}, " +
                        $"dataChecked={dataChecked}, coreReady={coreReady}, " +
                        $"databaseReady={window.DatabaseReady}, privacyAssetsReady={window.PrivacyAssetsReady}");
                    StartupMetrics.Complete("signal-timeout", ExitTimeout);
                    app.Shutdown(ExitTimeout);
                }
            };
            timer.Start();
        }
    }
}
