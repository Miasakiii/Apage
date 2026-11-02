#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Apage.Core.Data;
using Apage.Core.Services;
using Apage.Portable.Services;

namespace Apage.Portable.Views
{
    /// <summary>
    /// 书签 / 历史 / 下载的最小管理界面（B2）。
    /// 只暴露已经真实落库的数据与动作：打开、删除、导入、清空、取消下载、打开文件。
    /// 所有 SQLite 调用都在后台线程，界面线程只做绑定；任何数据层异常都转成状态栏提示 + 本地诊断，
    /// 不让窗口崩溃。
    /// </summary>
    public partial class LibraryWindow : Window
    {
        public enum LibraryTab
        {
            Bookmarks = 0,
            History = 1,
            Downloads = 2,
        }

        private readonly BookmarkRepository _bookmarks;
        private readonly HistoryRepository _history;
        private readonly DownloadRepository _downloads;
        private readonly DownloadCoordinator _coordinator;
        private readonly LocalLogService _log;
        private readonly Action<string> _openUrl;

        private readonly DispatcherTimer _searchDebounce;
        private int _historyQueryVersion;

        public LibraryWindow(
            BookmarkRepository bookmarks,
            HistoryRepository history,
            DownloadRepository downloads,
            DownloadCoordinator coordinator,
            LocalLogService log,
            Action<string> openUrl,
            LibraryTab initialTab = LibraryTab.Bookmarks)
        {
            _bookmarks = bookmarks ?? throw new ArgumentNullException(nameof(bookmarks));
            _history = history ?? throw new ArgumentNullException(nameof(history));
            _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _openUrl = openUrl ?? throw new ArgumentNullException(nameof(openUrl));

            InitializeComponent();

            _searchDebounce = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(250),
            };
            _searchDebounce.Tick += async (s, e) =>
            {
                _searchDebounce.Stop();
                await RefreshHistoryAsync();
            };

            Tabs.SelectedIndex = (int)initialTab;
            Loaded += async (s, e) => await RefreshAsync();
            _coordinator.Changed += OnDownloadsChanged;
            Closed += OnClosed;
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            _searchDebounce.Stop();
            _coordinator.Changed -= OnDownloadsChanged;
        }

        /// <summary>切换页签（窗口已打开时由宿主调用）。</summary>
        public void SelectTab(LibraryTab tab) => Tabs.SelectedIndex = (int)tab;

        private void OnDownloadsChanged(object? sender, EventArgs e)
        {
            // WebView2 下载事件在 UI 线程触发；防御性地再 marshal 一次。
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(async () => await RefreshDownloadsAsync()));
                return;
            }

            _ = RefreshDownloadsAsync();
        }

        private async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.Source, Tabs))
                return; // 内层 ListBox 的选择变化冒泡上来，忽略

            await RefreshAsync();
        }

        private Task RefreshAsync()
        {
            switch ((LibraryTab)Tabs.SelectedIndex)
            {
                case LibraryTab.History:
                    return RefreshHistoryAsync();
                case LibraryTab.Downloads:
                    return RefreshDownloadsAsync();
                default:
                    return RefreshBookmarksAsync();
            }
        }

        // ===== 书签 =====

        private async Task RefreshBookmarksAsync()
        {
            try
            {
                var entries = await _bookmarks.ListAsync();
                BookmarkList.ItemsSource = entries.Select(x => new BookmarkRow(x)).ToArray();
                StatusText.Text = entries.Count == 0
                    ? "还没有书签。按 Ctrl+D 收藏当前页面，或导入浏览器导出的书签 HTML。"
                    : $"共 {entries.Count} 条书签。";
            }
            catch (Exception ex)
            {
                Fail("读取书签失败", ex);
            }
        }

        private async void ImportBookmarks_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择浏览器导出的书签文件",
                Filter = "书签 HTML (*.html;*.htm)|*.html;*.htm|所有文件 (*.*)|*.*",
                CheckFileExists = true,
            };
            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                var parsed = BookmarkHtmlImporter.ParseFile(dialog.FileName);
                var outcome = await _bookmarks.ImportAsync(parsed.Items);
                await RefreshBookmarksAsync();

                StatusText.Text =
                    $"导入完成：新增 {outcome.Added}，更新 {outcome.Updated}；" +
                    $"跳过重复 {parsed.SkippedDuplicate + outcome.SkippedDuplicate}，" +
                    $"跳过无效 {parsed.SkippedInvalid + outcome.SkippedInvalid}。";
                _log.Write(
                    LocalLogLevel.Info,
                    "bookmarks-import",
                    $"added={outcome.Added}; updated={outcome.Updated}; invalid={parsed.SkippedInvalid}");
            }
            catch (Exception ex)
            {
                Fail("导入书签失败（文件可能不是浏览器导出的 HTML）", ex);
            }
        }

        private void OpenBookmark_Click(object sender, RoutedEventArgs e) => OpenSelectedBookmark();

        private void BookmarkList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
            OpenSelectedBookmark();

        private void OpenSelectedBookmark()
        {
            if (BookmarkList.SelectedItem is BookmarkRow row)
                _openUrl(row.Url);
        }

        private async void RemoveBookmark_Click(object sender, RoutedEventArgs e)
        {
            if (!(BookmarkList.SelectedItem is BookmarkRow row))
            {
                StatusText.Text = "请先选择一条书签。";
                return;
            }

            try
            {
                await _bookmarks.RemoveAsync(row.Id);
                await RefreshBookmarksAsync();
            }
            catch (Exception ex)
            {
                Fail("删除书签失败", ex);
            }
        }

        // ===== 历史 =====

        private void HistorySearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchDebounce.Stop();
            _searchDebounce.Start();
        }

        private async Task RefreshHistoryAsync()
        {
            var version = ++_historyQueryVersion;
            try
            {
                var keyword = HistorySearchBox.Text;
                var entries = string.IsNullOrWhiteSpace(keyword)
                    ? await _history.QueryRecentAsync()
                    : await _history.SearchAsync(keyword);

                if (version != _historyQueryVersion)
                    return; // 已有更新的查询，丢弃过期结果

                HistoryList.ItemsSource = entries.Select(x => new HistoryRow(x)).ToArray();
                StatusText.Text = entries.Count == 0
                    ? (string.IsNullOrWhiteSpace(keyword) ? "还没有历史记录。" : "没有匹配的记录。")
                    : $"显示 {entries.Count} 条历史记录（每页最多 200 条，按时间倒序）。";
            }
            catch (Exception ex)
            {
                Fail("读取历史失败", ex);
            }
        }

        private void HistoryList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (HistoryList.SelectedItem is HistoryRow row)
                _openUrl(row.Url);
        }

        private async void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                this,
                "清空全部本地历史记录？此操作不可撤销。",
                "清空历史",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK)
                return;

            try
            {
                var removed = await _history.ClearAsync();
                await RefreshHistoryAsync();
                StatusText.Text = $"已删除 {removed} 条历史记录。";
                _log.Write(LocalLogLevel.Info, "history", $"cleared={removed}");
            }
            catch (Exception ex)
            {
                Fail("清空历史失败", ex);
            }
        }

        // ===== 下载 =====

        private async Task RefreshDownloadsAsync()
        {
            try
            {
                var entries = await _downloads.ListRecentAsync();
                DownloadList.ItemsSource = entries.Select(x => new DownloadRow(x)).ToArray();
                StatusText.Text = entries.Count == 0
                    ? "还没有下载记录。"
                    : $"共 {entries.Count} 条下载记录；进行中 {entries.Count(x => x.IsActive)} 条。";
            }
            catch (Exception ex)
            {
                Fail("读取下载记录失败", ex);
            }
        }

        private void DownloadList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
            OpenSelectedDownload();

        private void OpenDownload_Click(object sender, RoutedEventArgs e) => OpenSelectedDownload();

        private void OpenSelectedDownload()
        {
            if (!(DownloadList.SelectedItem is DownloadRow row))
            {
                StatusText.Text = "请先选择一条下载记录。";
                return;
            }

            if (row.IsActive)
            {
                StatusText.Text = "下载尚未完成，无法打开。";
                return;
            }

            if (!_coordinator.Open(row.TargetPath))
                StatusText.Text = "文件不存在或无法打开（可能已被移动或删除）。";
        }

        private async void CancelDownload_Click(object sender, RoutedEventArgs e)
        {
            if (!(DownloadList.SelectedItem is DownloadRow row))
            {
                StatusText.Text = "请先选择一条下载记录。";
                return;
            }

            if (!row.IsActive)
            {
                StatusText.Text = "该下载已经结束，无需取消。";
                return;
            }

            if (!_coordinator.Cancel(row.Id))
            {
                // 进程重启后，旧记录虽标记为进行中，但已没有对应的内核对象。
                // 只处理这一条，绝不能全表清理——其他下载可能正在正常进行。
                await _downloads.MarkOrphanedAsync(row.Id);
                StatusText.Text = "该下载已不在当前会话中，已标记为失败。";
            }

            await RefreshDownloadsAsync();
        }

        private async void ClearDownloads_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var removed = await _downloads.ClearFinishedAsync();
                await RefreshDownloadsAsync();
                StatusText.Text = $"已清理 {removed} 条已结束的下载记录。";
            }
            catch (Exception ex)
            {
                Fail("清理下载记录失败", ex);
            }
        }

        private void Fail(string message, Exception ex)
        {
            _log.WriteException("library-window", ex);
            StatusText.Text = message + "。诊断信息已写入本地日志。";
        }

        // ===== 行模型（只读绑定） =====

        public sealed class BookmarkRow
        {
            public BookmarkRow(BookmarkEntry entry)
            {
                Id = entry.Id;
                Url = entry.Url;
                Title = string.IsNullOrWhiteSpace(entry.Title) ? entry.Url : entry.Title;
            }

            public long Id { get; }

            public string Url { get; }

            public string Title { get; }
        }

        public sealed class HistoryRow
        {
            public HistoryRow(HistoryEntry entry)
            {
                Url = entry.Url;
                Title = string.IsNullOrWhiteSpace(entry.Title) ? entry.Url : entry.Title;
                When = entry.VisitedAt.ToLocalTime().ToString("MM-dd HH:mm");
            }

            public string Url { get; }

            public string Title { get; }

            public string When { get; }
        }

        public sealed class DownloadRow
        {
            public DownloadRow(DownloadEntry entry)
            {
                Id = entry.Id;
                Url = entry.Url;
                TargetPath = entry.TargetPath;
                IsActive = entry.IsActive;
                FileName = string.IsNullOrWhiteSpace(entry.TargetPath)
                    ? UrlKey.GetHost(entry.Url)
                    : Path.GetFileName(entry.TargetPath);
                if (string.IsNullOrWhiteSpace(FileName))
                    FileName = entry.Url;

                StateText = entry.State switch
                {
                    DownloadStates.Completed => "已完成",
                    DownloadStates.Canceled => "已取消",
                    DownloadStates.Failed => "失败",
                    _ => "进行中",
                };

                var size = entry.TotalBytes > 0
                    ? $"{FormatBytes(entry.ReceivedBytes)} / {FormatBytes(entry.TotalBytes)} · {entry.ReceivedBytes * 100 / entry.TotalBytes}%"
                    : FormatBytes(entry.ReceivedBytes);
                ProgressText = string.IsNullOrEmpty(entry.Error) ? size : size + " · " + entry.Error;
            }

            public string Id { get; }

            public string Url { get; }

            public string TargetPath { get; }

            public bool IsActive { get; }

            public string FileName { get; }

            public string StateText { get; }

            public string ProgressText { get; }

            private static string FormatBytes(long bytes)
            {
                if (bytes < 1024)
                    return bytes + " B";
                if (bytes < 1024L * 1024)
                    return (bytes / 1024.0).ToString("0.0") + " KB";
                if (bytes < 1024L * 1024 * 1024)
                    return (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB";
                return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.00") + " GB";
            }
        }
    }
}
