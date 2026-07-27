#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Apage.Core.Configuration;
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

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 单实例判定（R13）：Mutex 名含数据目录哈希；已有实例持有则提示并退出
            _services = new AppServices(AppPaths.DataDirectory);
            if (!_services.SingleInstance.TryAcquire(out _))
            {
                MessageBox.Show(
                    "Apage 已在运行（同一数据目录只允许一个实例）。",
                    "Apage", MessageBoxButton.OK, MessageBoxImage.Information);
                _services.Dispose();
                _services = null;
                Shutdown();
                return;
            }

            // 加载设置（隐私默认全开），并把缓存策略灌给 AppPaths（R7：默认宿主机 %TEMP%，纯便携才写 exe 同级）
            _services.InitializeAsync().GetAwaiter().GetResult();
            AppPaths.PortableCache = _services.Settings.Current.PortableCache;
            AppPaths.EnsureCreated();

            // 内核生命周期门面：普通标签共享同一 Environment（Phase 2 多标签复用同一内核，省内存）
            _lifecycle = BrowserTabView.CreateLifecycleService();

            var window = new MainWindow(_services, _lifecycle, LoadAdBlockRules());
            MainWindow = window;
            window.Show();
        }

        /// <summary>
        /// 加载本地广告拦截规则（§5.3）：只读 {数据目录}\adblock\*.txt，绝不联网下载列表（零后台请求红线）。
        /// 目录不存在或无有效规则返回 null，下游据此完全不挂 WebResourceRequested 管道（无开销）。
        /// 规则视为不可信输入，引擎内部已做长度上限与 100ms 正则超时防护。
        /// </summary>
        private static AdBlockRuleEngine? LoadAdBlockRules()
        {
            try
            {
                var dir = Path.Combine(AppPaths.DataDirectory, "adblock");
                if (!Directory.Exists(dir))
                    return null;

                var engine = new AdBlockRuleEngine();
                foreach (var file in Directory.GetFiles(dir, "*.txt"))
                    engine.AddRules(File.ReadLines(file));

                return engine.BlockCount > 0 ? engine : null;
            }
            catch (Exception ex)
            {
                // 规则加载失败不阻断启动：无拦截运行
                Trace.WriteLine($"[App] 广告拦截规则加载失败：{ex}");
                return null;
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _lifecycle?.Dispose();
            _services?.Dispose(); // 释放单实例 Mutex
            base.OnExit(e);
        }
    }
}
