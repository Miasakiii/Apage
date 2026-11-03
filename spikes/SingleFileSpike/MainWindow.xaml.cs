using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Apage.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Web.WebView2.Core;

namespace Apage.Spikes.SingleFile
{
    public partial class MainWindow : Window
    {
        private bool _finished;
        private DateTime? _windowRenderedUtc;
        private static System.Threading.Timer _shutdownBackstop;

        public MainWindow()
        {
            InitializeComponent();
            ContentRendered += OnContentRendered;
            Loaded += async (s, e) => await RunSpikeAsync();
        }

        private void OnContentRendered(object sender, EventArgs e)
        {
            _windowRenderedUtc = DateTime.UtcNow;
            try
            {
                File.WriteAllText(
                    Path.Combine(App.ResultDirectory, "window-rendered.txt"),
                    _windowRenderedUtc.Value.ToString("O"));
            }
            catch { /* 结果 JSON 仍会记录时间；标记文件失败不替代真实断言。 */ }
        }

        private async Task RunSpikeAsync()
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(75));
                if (!_finished)
                {
                    TryWriteText("error.txt", "FATAL: spike timed out after 75 seconds.");
                    Environment.Exit(3);
                }
            });

            var exitCode = 0;
            try
            {
                StatusText.Text = "验证 Core 与 SQLite…";

                // 引用真实 Apage.Core，确保共享层及其依赖也被合并并能在 net48 中加载。
                var settings = new AppSettings();
                if (!settings.SendDoNotTrack ||
                    !settings.SendGPC || !settings.WebRTCIPProtection)
                    throw new InvalidOperationException("Apage.Core privacy defaults were not loaded correctly.");

                var sqliteValue = await Task.Run(() => VerifySqlite());
                if (sqliteValue != 42)
                    throw new InvalidOperationException($"SQLite round-trip returned {sqliteValue}, expected 42.");
                var sqliteReadyUtc = DateTime.UtcNow;

                StatusText.Text = "验证 WebView2 初始化与离线导航…";
                var runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
                var userDataFolder = Path.Combine(App.ResultDirectory, "webview2");
                Directory.CreateDirectory(userDataFolder);
                var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await WebView.EnsureCoreWebView2Async(environment);

                var navigationDone = new TaskCompletionSource<bool>();
                WebView.CoreWebView2.NavigationCompleted += (s, e) =>
                {
                    if (e.IsSuccess)
                        navigationDone.TrySetResult(true);
                    else
                        navigationDone.TrySetException(new InvalidOperationException($"Navigation failed: {e.WebErrorStatus}"));
                };
                const string expectedTitle = "ApageSingleFileSpike";
                WebView.CoreWebView2.NavigateToString(
                    "<!doctype html><html><head><meta charset=\"utf-8\"><title>ApageSingleFileSpike</title></head>" +
                    "<body style=\"font-family:Segoe UI;padding:24px\"><h1>Single-file render OK</h1></body></html>");
                var completed = await Task.WhenAny(navigationDone.Task, Task.Delay(TimeSpan.FromSeconds(20)));
                if (completed != navigationDone.Task)
                    throw new TimeoutException("WebView2 offline document navigation timed out.");
                await navigationDone.Task;
                if (!string.Equals(WebView.CoreWebView2.DocumentTitle, expectedTitle, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Rendered document title was '{WebView.CoreWebView2.DocumentTitle}', expected '{expectedTitle}'.");
                var webViewReadyUtc = DateTime.UtcNow;

                if (_windowRenderedUtc == null)
                    throw new InvalidOperationException("ContentRendered was not observed.");

                var result = new
                {
                    windowRenderedUtc = _windowRenderedUtc.Value,
                    sqliteReadyUtc,
                    webViewReadyUtc,
                    completedUtc = DateTime.UtcNow,
                    sqliteValue,
                    sqliteDatabaseBytes = new FileInfo(Path.Combine(App.ResultDirectory, "spike.db")).Length,
                    webViewVersion = runtimeVersion,
                    navigationTitle = WebView.CoreWebView2.DocumentTitle,
                    corePrivacyDefaults = true,
                    processBitness = Environment.Is64BitProcess ? "x64" : "x86"
                };
                var json = JsonSerializer.Serialize(result);
                File.WriteAllText(Path.Combine(App.ResultDirectory, "result.json"), json);
                StatusText.Text = "PASS：单文件、Core、SQLite、WebView2 均可用";
            }
            catch (Exception ex)
            {
                exitCode = 1;
                TryWriteText("error.txt", ex.ToString());
                StatusText.Text = "FAIL：" + ex.Message;
            }
            finally
            {
                _finished = true;
            }

            await Task.Delay(150);
            // WebView2 退出偶发挂起；超过 5 秒强制退出，保证自动化验证可收敛。
            _shutdownBackstop = new System.Threading.Timer(
                _ => Environment.Exit(exitCode),
                null,
                TimeSpan.FromSeconds(5),
                System.Threading.Timeout.InfiniteTimeSpan);
            Application.Current.Shutdown(exitCode);
        }

        private static int VerifySqlite()
        {
            // 单文件场景必须显式选择静态 P/Invoke provider：dynamic_cdecl 依赖 Assembly.Location。
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
            SQLitePCL.raw.FreezeProvider();

            var databasePath = Path.Combine(App.ResultDirectory, "spike.db");
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            };

            using (var connection = new SqliteConnection(builder.ToString()))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
PRAGMA journal_mode=DELETE;
PRAGMA synchronous=FULL;
CREATE TABLE spike (value INTEGER NOT NULL);
INSERT INTO spike(value) VALUES (42);";
                    command.ExecuteNonQuery();
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT value FROM spike LIMIT 1;";
                    return Convert.ToInt32(command.ExecuteScalar());
                }
            }
        }

        private static void TryWriteText(string fileName, string content)
        {
            try { File.WriteAllText(Path.Combine(App.ResultDirectory, fileName), content); }
            catch { }
        }
    }
}
