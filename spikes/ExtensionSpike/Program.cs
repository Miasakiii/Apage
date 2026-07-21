// Spike 3：WebView2 扩展能力摸底（对应 docs/product-design.md 风险 R3）
// 实测项：
//   1) 本机 WebView2 Runtime 版本（GetAvailableCoreWebView2BrowserVersionString）
//   2) CoreWebView2Profile.AddBrowserExtensionAsync 是否存在/可用（最小 MV3 扩展，程序内生成到临时目录）
//   3) 枚举已加载扩展（GetBrowserExtensionsAsync）、启用/禁用、卸载（RemoveAsync）
//   4) 附加负面测试：MV2 扩展应被拒绝（支撑「部分 MV3」口径）
// 运行：dotnet run --project spikes/ExtensionSpike -- <日志文件路径>
// 所有结论必须来自本程序的真实输出（spike-run.log）。

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Apage.Spikes.Extension;

internal static class Program
{
    private static readonly StringBuilder LogBuf = new();
    private static string _logPath = "spike-run.log";

    private static void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        Console.WriteLine(line);
        lock (LogBuf) { LogBuf.AppendLine(line); }
    }

    private static void FlushLog()
    {
        try { File.WriteAllText(_logPath, LogBuf.ToString(), new UTF8Encoding(false)); }
        catch (Exception ex) { Console.WriteLine("写日志文件失败: " + ex.Message); }
    }

    private static string Describe(Exception ex) =>
        $"{ex.GetType().FullName} HResult=0x{ex.HResult:X8}: {ex.Message}";

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        _logPath = Path.GetFullPath(args.Length > 0 ? args[0] : "spike-run.log");

        // 看门狗：任何挂起（如运行时弹窗等住）150s 后强退，保证 spike 可无人值守运行
        Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(150));
            Log("!! 看门狗超时，强制退出");
            FlushLog();
            Environment.Exit(2);
        });

        var exitCode = 0;
        try { exitCode = RunAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { Log("!! 未处理异常: " + Describe(ex)); exitCode = 1; }
        finally { FlushLog(); }
        return exitCode;
    }

    private static Task<int> RunAsync()
    {
        var tcs = new TaskCompletionSource<int>();
        var app = new Application();
        var win = new Window
        {
            Title = "ExtensionSpike (自动运行，结束后自动关闭)",
            Width = 960,
            Height = 600,
        };
        var webView = new WebView2();
        win.Content = webView;

        win.Loaded += async (_, _) =>
        {
            try
            {
                await RunStepsAsync(webView);
                tcs.TrySetResult(0);
            }
            catch (Exception ex)
            {
                Log("!! 步骤异常: " + Describe(ex));
                tcs.TrySetResult(1);
            }
            finally { win.Close(); }
        };
        win.Closed += (_, _) => app.Shutdown();
        app.Run(win);
        return tcs.Task;
    }

    private static async Task RunStepsAsync(WebView2 webView)
    {
        Log("=== Apage Spike 3: WebView2 扩展能力摸底（R3） ===");
        Log($"时间(本地): {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Log($"OS: {Environment.OSVersion}");
        Log($".NET 运行时: {Environment.Version}");
        Log($"WebView2 SDK 程序集: {typeof(CoreWebView2Environment).Assembly.GetName().Name} " +
            $"{typeof(CoreWebView2Environment).Assembly.GetName().Version}");

        // ---------- 1) 本机 Runtime 版本 ----------
        // 注：SDK 1.0.4078.44 中该 API 名为 GetAvailableBrowserVersionString（反射实测确认，
        // 不存在 GetAvailableCoreWebView2BrowserVersionString 这一命名）。
        Log("--- [1] CoreWebView2Environment.GetAvailableBrowserVersionString ---");
        LogApi(typeof(CoreWebView2Environment).GetMethod("GetAvailableBrowserVersionString",
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null),
            "CoreWebView2Environment.GetAvailableBrowserVersionString(string)");
        string runtimeVersion;
        try
        {
            runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString(null);
            Log($"OK: Runtime 版本 = {runtimeVersion}");
        }
        catch (Exception ex)
        {
            Log("FAIL: " + Describe(ex));
            throw new InvalidOperationException("无可用 WebView2 Runtime，后续步骤无意义", ex);
        }

        // ---------- 2) SDK API 面（反射确认存在性） ----------
        Log("--- [2] SDK 扩展 API 可用性（反射，编译期引用 1.0.4078.44） ---");
        var profType = typeof(CoreWebView2Profile);
        LogApi(profType.GetMethod("AddBrowserExtensionAsync"), "CoreWebView2Profile.AddBrowserExtensionAsync(string)");
        LogApi(profType.GetMethod("GetBrowserExtensionsAsync"), "CoreWebView2Profile.GetBrowserExtensionsAsync()");
        LogApi(profType.GetProperty("BrowserExtensions"), "CoreWebView2Profile.BrowserExtensions (同步属性)");
        var extType = typeof(CoreWebView2BrowserExtension);
        LogApi(extType.GetProperty("Id"), "CoreWebView2BrowserExtension.Id");
        LogApi(extType.GetProperty("Name"), "CoreWebView2BrowserExtension.Name");
        LogApi(extType.GetProperty("IsEnabled"), "CoreWebView2BrowserExtension.IsEnabled");
        LogApi(extType.GetMethod("EnableAsync"), "CoreWebView2BrowserExtension.EnableAsync(bool)");
        LogApi(extType.GetMethod("RemoveAsync"), "CoreWebView2BrowserExtension.RemoveAsync()");

        // ---------- 3) 生成最小扩展到临时目录 ----------
        Log("--- [3] 生成最小扩展到临时目录 ---");
        var tempRoot = Path.Combine(Path.GetTempPath(), "ApageExtSpike-" + Guid.NewGuid().ToString("N"));
        var mv3Dir = Path.Combine(tempRoot, "mv3");
        var mv2Dir = Path.Combine(tempRoot, "mv2");
        var udfDir = Path.Combine(tempRoot, "udf");
        Directory.CreateDirectory(mv3Dir);
        Directory.CreateDirectory(mv2Dir);
        WriteMinimalMv3(mv3Dir);
        WriteMinimalMv2(mv2Dir);
        Log($"MV3 扩展目录: {mv3Dir}");
        Log($"MV2 扩展目录: {mv2Dir}（负面测试用）");
        Log($"UserDataFolder: {udfDir}");

        try
        {
            // ---------- 4) 初始化 WebView2 ----------
            Log("--- [4] 初始化 WebView2 ---");
            var env = await CoreWebView2Environment.CreateAsync(null, udfDir);
            await webView.EnsureCoreWebView2Async(env);
            var core = webView.CoreWebView2;
            Log($"OK: CoreWebView2 初始化完成，CookieManager 可用={core.CookieManager != null}");
            var profile = core.Profile;
            Log($"默认 Profile: Name={profile.ProfileName}, IsInPrivateModeEnabled={profile.IsInPrivateModeEnabled}");

            // ---------- 5) 加载最小 MV3 扩展 ----------
            Log("--- [5] AddBrowserExtensionAsync 加载最小 MV3 扩展 ---");
            CoreWebView2BrowserExtension? ext = null;
            try
            {
                ext = await profile.AddBrowserExtensionAsync(mv3Dir);
                Log($"OK: 加载成功 -> Id={ext.Id}, Name={ext.Name}, IsEnabled={ext.IsEnabled}");
            }
            catch (Exception ex)
            {
                Log("FAIL: " + Describe(ex));
            }

            // ---------- 6) 枚举已加载扩展 ----------
            Log("--- [6] GetBrowserExtensionsAsync 枚举 ---");
            if (ext != null)
            {
                var list = await profile.GetBrowserExtensionsAsync();
                Log($"枚举到 {list.Count} 个扩展:");
                foreach (var e in list)
                    Log($"  - Id={e.Id}, Name={e.Name}, IsEnabled={e.IsEnabled}");

                // ---------- 7) 禁用/启用 ----------
                Log("--- [7] EnableAsync 禁用/启用 ---");
                await ext.EnableAsync(false);
                var cur = (await profile.GetBrowserExtensionsAsync()).FirstOrDefault(x => x.Id == ext.Id);
                Log($"EnableAsync(false) 后: IsEnabled={cur?.IsEnabled.ToString() ?? "(已从列表消失)"}");
                await ext.EnableAsync(true);
                cur = (await profile.GetBrowserExtensionsAsync()).FirstOrDefault(x => x.Id == ext.Id);
                Log($"EnableAsync(true) 后: IsEnabled={cur?.IsEnabled.ToString() ?? "(已从列表消失)"}");
            }
            else
            {
                Log("跳过（步骤 5 未成功加载）");
            }

            // ---------- 8) MV2 负面测试 ----------
            Log("--- [8] MV2 扩展负面测试（预期被拒绝） ---");
            try
            {
                var bad = await profile.AddBrowserExtensionAsync(mv2Dir);
                Log($"意外成功: Id={bad.Id}, Name={bad.Name}（MV2 也被接受，口径需修正）");
                try { await bad.RemoveAsync(); } catch { /* 清理失败不影响结论 */ }
            }
            catch (Exception ex)
            {
                Log("如预期失败: " + Describe(ex));
            }

            // ---------- 9) 卸载 MV3 扩展并确认 ----------
            Log("--- [9] RemoveAsync 卸载并确认 ---");
            if (ext != null)
            {
                await ext.RemoveAsync();
                var after = await profile.GetBrowserExtensionsAsync();
                var still = after.Any(x => x.Id == ext.Id);
                Log($"RemoveAsync 后: 仍在列表={still}（当前共 {after.Count} 个扩展）");
            }
            else
            {
                Log("跳过（步骤 5 未成功加载）");
            }
        }
        finally
        {
            // 清理临时目录（UDF 可能仍被运行时占用，尽力而为）
            try { Directory.Delete(tempRoot, true); Log($"临时目录已清理: {tempRoot}"); }
            catch (Exception ex) { Log($"临时目录清理失败（不影响结论）: {ex.Message}"); }
        }

        Log("=== Spike 3 执行完毕 ===");
    }

    private static void LogApi(MemberInfo? member, string name) =>
        Log(member != null ? $"存在: {name} -> {member}" : $"缺失: {name}");

    private static void WriteMinimalMv3(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "manifest.json"), """
        {
          "manifest_version": 3,
          "name": "Apage Spike MV3 Probe",
          "version": "1.0.0.0",
          "description": "Minimal MV3 extension generated by Apage ExtensionSpike.",
          "background": { "service_worker": "background.js" },
          "permissions": []
        }
        """);
        File.WriteAllText(Path.Combine(dir, "background.js"),
            "console.log('[ApageSpike] MV3 service worker evaluated');\n" +
            "chrome.runtime.onInstalled.addListener(() => console.log('[ApageSpike] onInstalled'));\n");
    }

    private static void WriteMinimalMv2(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "manifest.json"), """
        {
          "manifest_version": 2,
          "name": "Apage Spike MV2 Probe",
          "version": "1.0.0.0",
          "description": "Minimal MV2 extension, expected to be REJECTED by WebView2.",
          "background": { "scripts": ["background.js"], "persistent": false }
        }
        """);
        File.WriteAllText(Path.Combine(dir, "background.js"),
            "console.log('[ApageSpike] MV2 background evaluated');\n");
    }
}
