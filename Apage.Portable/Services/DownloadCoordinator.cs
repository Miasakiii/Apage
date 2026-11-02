#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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

        // 每个下载 id 一条串行写链：WebView2 事件可在 StartAsync 落库前到达，
        // 直接 fire-and-forget 会让终态 UPDATE 先执行并命中 0 行，随后 Start 又插入 in_progress。
        private readonly object _writeChainGate = new();
        private readonly Dictionary<string, Task> _writeChains = new(StringComparer.Ordinal);

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

            Enqueue(id,
                async () => { await _repository.StartAsync(id, uri, path, total).ConfigureAwait(false); },
                "download-start");
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
                Enqueue(id, () => _repository.UpdateProgressAsync(id, received, total), "download-progress");
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
                        Enqueue(id, () => _repository.MarkCompletedAsync(id, operation.ResultFilePath), "download-complete", final: true);
                        _log.Write(LocalLogLevel.Info, "download", "completed");
                        Changed?.Invoke(this, EventArgs.Empty);
                        break;

                    case CoreWebView2DownloadState.Interrupted:
                        _active.TryRemove(id, out _);
                        _lastProgressWrite.TryRemove(id, out _);
                        var reason = operation.InterruptReason;
                        if (reason == CoreWebView2DownloadInterruptReason.UserCanceled)
                        {
                            Enqueue(id, () => _repository.MarkCanceledAsync(id), "download-canceled", final: true);
                            _log.Write(LocalLogLevel.Info, "download", "canceled");
                        }
                        else
                        {
                            Enqueue(id, () => _repository.MarkFailedAsync(id, reason.ToString()), "download-interrupted", final: true);
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

        // 把同一 id 的仓储写入串成任务链；前一步失败也继续执行后续步骤，
        // 但每一步的异常都写诊断，避免单个 IO 故障阻断终态。
        private void Enqueue(string id, Func<Task> action, string operation, bool final = false)
        {
            Task next;
            lock (_writeChainGate)
            {
                _writeChains.TryGetValue(id, out var previous);
                next = RunChainedAsync(previous ?? Task.CompletedTask, action, operation);
                _writeChains[id] = next;
            }

            if (!final)
                return;

            _ = next.ContinueWith(completed =>
            {
                lock (_writeChainGate)
                {
                    if (_writeChains.TryGetValue(id, out var current) &&
                        ReferenceEquals(current, completed))
                    {
                        _writeChains.Remove(id);
                    }
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private async Task RunChainedAsync(Task previous, Func<Task> action, string operation)
        {
            try { await previous.ConfigureAwait(false); }
            catch { /* 前序步骤已记录，后续步骤仍需尝试 */ }

            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.WriteException(operation, ex);
            }
        }
    }
}
