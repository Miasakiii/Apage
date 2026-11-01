#nullable enable
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Apage.Core.Data;
using Apage.Core.Services;
using Microsoft.Web.WebView2.Core;

namespace Apage.Portable.Services
{
    /// <summary>
    /// 下载协调器（B2）：把 WebView2 的下载生命周期落进 SQLite，并提供取消 / 打开文件能力。
    /// 保留 WebView2 自带下载提示（不设 Handled），用户始终能立即看到下载开始；
    /// 本类只负责持久化与后续操作，不另造一套下载 UI 行为。
    /// 进度写入按 1 秒节流，避免大文件每次回调都写库。
    /// </summary>
    public sealed class DownloadCoordinator
    {
        private static readonly TimeSpan ProgressWriteInterval = TimeSpan.FromSeconds(1);

        private readonly DownloadRepository _repository;
        private readonly LocalLogService _log;
        private readonly ConcurrentDictionary<string, CoreWebView2DownloadOperation> _active =
            new ConcurrentDictionary<string, CoreWebView2DownloadOperation>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, DateTimeOffset> _lastProgressWrite =
            new ConcurrentDictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        public DownloadCoordinator(DownloadRepository repository, LocalLogService log)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>下载记录发生变化（开始 / 完成 / 中断 / 取消），供下载列表刷新。</summary>
        public event EventHandler? Changed;

        /// <summary>挂接一个新下载。异常路径只记诊断，绝不影响页面下载本身。</summary>
        public void Track(CoreWebView2DownloadOperation operation)
        {
            if (operation == null)
                return;

            var id = Guid.NewGuid().ToString("N");
            _active[id] = operation;

            var uri = operation.Uri;
            var path = operation.ResultFilePath;
            var total = unchecked((long)(operation.TotalBytesToReceive ?? 0));

            _ = RunAsync(_repository.StartAsync(id, uri, path, total), "download-start");
            _log.Write(LocalLogLevel.Info, "download", $"started; total={total}");

            operation.BytesReceivedChanged += (s, e) => OnProgress(id, operation);
            operation.StateChanged += (s, e) => OnStateChanged(id, operation);

            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>取消一个仍在进行的下载；返回是否找到了活动下载。</summary>
        public bool Cancel(string? id)
        {
            if (string.IsNullOrEmpty(id) || !_active.TryGetValue(id!, out var operation))
                return false;

            try
            {
                operation.Cancel();
                return true;
            }
            catch (Exception ex)
            {
                _log.WriteException("download-cancel", ex);
                return false;
            }
        }

        /// <summary>用系统默认程序打开已下载文件；文件不存在或无法启动时返回 false。</summary>
        public bool Open(string? targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath))
                return false;

            try
            {
                Process.Start(new ProcessStartInfo(targetPath!) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                _log.WriteException("download-open", ex);
                return false;
            }
        }

        /// <summary>是否有进行中的下载（退出时可据此提示，当前不阻断关闭）。</summary>
        public int ActiveCount => _active.Count;

        private void OnProgress(string id, CoreWebView2DownloadOperation operation)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                if (_lastProgressWrite.TryGetValue(id, out var last) && now - last < ProgressWriteInterval)
                    return;

                _lastProgressWrite[id] = now;
                var received = operation.BytesReceived;
                var total = unchecked((long)(operation.TotalBytesToReceive ?? 0));
                _ = RunAsync(_repository.UpdateProgressAsync(id, received, total), "download-progress");
            }
            catch (Exception ex)
            {
                _log.WriteException("download-progress", ex);
            }
        }

        private void OnStateChanged(string id, CoreWebView2DownloadOperation operation)
        {
            try
            {
                switch (operation.State)
                {
                    case CoreWebView2DownloadState.Completed:
                        _active.TryRemove(id, out _);
                        _lastProgressWrite.TryRemove(id, out _);
                        _ = RunAsync(_repository.MarkCompletedAsync(id, operation.ResultFilePath), "download-complete");
                        _log.Write(LocalLogLevel.Info, "download", "completed");
                        Changed?.Invoke(this, EventArgs.Empty);
                        break;

                    case CoreWebView2DownloadState.Interrupted:
                        _active.TryRemove(id, out _);
                        _lastProgressWrite.TryRemove(id, out _);
                        var reason = operation.InterruptReason;
                        if (reason == CoreWebView2DownloadInterruptReason.UserCanceled)
                        {
                            _ = RunAsync(_repository.MarkCanceledAsync(id), "download-canceled");
                            _log.Write(LocalLogLevel.Info, "download", "canceled");
                        }
                        else
                        {
                            _ = RunAsync(_repository.MarkFailedAsync(id, reason.ToString()), "download-interrupted");
                            _log.Write(LocalLogLevel.Warning, "download", "interrupted; reason=" + reason);
                        }
                        Changed?.Invoke(this, EventArgs.Empty);
                        break;
                }
            }
            catch (Exception ex)
            {
                _log.WriteException("download-state", ex);
            }
        }

        private async System.Threading.Tasks.Task RunAsync(
            System.Threading.Tasks.Task task,
            string operation)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.WriteException(operation, ex);
            }
        }
    }
}
