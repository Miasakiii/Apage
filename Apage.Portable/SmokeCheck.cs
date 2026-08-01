#nullable enable
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Apage.Core.Services;

namespace Apage.Portable
{
    /// <summary>
    /// 发布线冒烟验证（--smoke）：复用真实启动路径，三信号齐备立即退出——
    /// ① 应用可启动（OnStartup 正常走完）② 主窗口可呈现（ContentRendered 触发）
    /// ③ WebView2 运行时可初始化（当前标签 CoreWebView2 就绪）。
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
                Environment.Exit(1);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Trace.WriteLine($"[Smoke] 未处理异常：{e.ExceptionObject}");
                Environment.Exit(1);
            };
        }

        /// <summary>主窗口 Show 之后启动看门狗：轮询三信号，齐备则 0 退出，超时则以 2 退出。</summary>
        internal static void Start(Application app, MainWindow window)
        {
            // 信号③前置条件：Runtime 未安装时 BrowserTabView 只会显示 R1 引导面板、
            // 内核永不就绪——直接给出明确退出码，而不是拖到超时。
            if (!WebView2RuntimeService.Detect().IsInstalled)
            {
                Trace.WriteLine("[Smoke] WebView2 Runtime 未安装");
                app.Shutdown(ExitRuntimeMissing);
                return;
            }

            var rendered = false;
            window.ContentRendered += (s, e) => rendered = true; // 信号②

            var deadline = DateTime.UtcNow + Timeout;
            var timer = new DispatcherTimer { Interval = PollInterval };
            timer.Tick += (s, e) =>
            {
                // 信号③：当前标签 CoreWebView2 非空 = Environment 创建 + 内核进程初始化成功
                if (rendered && window.Tabs?.Current?.View?.Core != null)
                {
                    timer.Stop();
                    app.Shutdown(0);
                    return;
                }

                if (DateTime.UtcNow >= deadline)
                {
                    timer.Stop();
                    var coreReady = window.Tabs?.Current?.View?.Core != null;
                    Trace.WriteLine($"[Smoke] 超时：rendered={rendered}, coreReady={coreReady}");
                    app.Shutdown(ExitTimeout);
                }
            };
            timer.Start();
        }
    }
}
