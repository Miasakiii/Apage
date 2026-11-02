#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Apage.Core.Data;

/// <summary>
/// 下载记录仓储（B2）：记录 WebView2 下载的开始/进度/终态，供下载列表展示与取消后回查。
/// 进度写入由调用方节流；终态一旦写入不可回退（completed/canceled/failed 不会变回 in_progress）。
/// 异常退出遗留的 in_progress 行由 <see cref="MarkOrphanedAsync"/> 在下次启动时标为失败。
/// </summary>
public sealed class DownloadRepository
{
    private const string SelectColumns =
        "id, url, target_path, state, received_bytes, total_bytes, started_at, finished_at, error";

    private readonly SqliteDatabase _database;

    public DownloadRepository(SqliteDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<bool> StartAsync(
        string id,
        string? url,
        string? targetPath,
        long totalBytes,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("下载 id 不能为空。", nameof(id));
        if (!UrlKey.TryNormalize(url, out var key))
            return false; // 非 http/https（blob: 等）不记录，避免虚构地址

        var path = targetPath ?? string.Empty;
        var total = Math.Max(0, totalBytes);
        var startedAt = BookmarkRepository.Format(DateTimeOffset.UtcNow);

        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            _database.WriteGate.Wait(cancellationToken);
            try
            {
                using var connection = _database.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = @"
INSERT INTO downloads(id, url, target_path, state, received_bytes, total_bytes, started_at, finished_at, error)
VALUES ($id, $url, $path, $state, 0, $total, $started, NULL, NULL)
ON CONFLICT(id) DO UPDATE SET
    url = excluded.url,
    target_path = excluded.target_path,
    total_bytes = excluded.total_bytes;";
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$url", key);
                command.Parameters.AddWithValue("$path", path);
                command.Parameters.AddWithValue("$state", DownloadStates.InProgress);
                command.Parameters.AddWithValue("$total", total);
                command.Parameters.AddWithValue("$started", startedAt);
                return command.ExecuteNonQuery() > 0;
            }
            finally
            {
                _database.WriteGate.Release();
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>更新进度（不改变状态）。</summary>
    public Task UpdateProgressAsync(
        string id,
        long receivedBytes,
        long totalBytes,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(
            id,
            "received_bytes = $received, total_bytes = CASE WHEN $total > 0 THEN $total ELSE total_bytes END",
            command =>
            {
                command.Parameters.AddWithValue("$received", Math.Max(0, receivedBytes));
                command.Parameters.AddWithValue("$total", Math.Max(0, totalBytes));
            },
            cancellationToken);

    public Task MarkCompletedAsync(string id, string? targetPath = null, CancellationToken cancellationToken = default) =>
        UpdateAsync(
            id,
            "state = $state, finished_at = $finished, error = NULL, received_bytes = CASE WHEN total_bytes > 0 THEN total_bytes ELSE received_bytes END" +
            (string.IsNullOrEmpty(targetPath) ? string.Empty : ", target_path = $path"),
            command =>
            {
                command.Parameters.AddWithValue("$state", DownloadStates.Completed);
                command.Parameters.AddWithValue("$finished", BookmarkRepository.Format(DateTimeOffset.UtcNow));
                if (!string.IsNullOrEmpty(targetPath))
                    command.Parameters.AddWithValue("$path", targetPath!);
            },
            cancellationToken);

    public Task MarkFailedAsync(string id, string? reason, CancellationToken cancellationToken = default) =>
        UpdateAsync(
            id,
            "state = $state, finished_at = $finished, error = $error",
            command =>
            {
                command.Parameters.AddWithValue("$state", DownloadStates.Failed);
                command.Parameters.AddWithValue("$finished", BookmarkRepository.Format(DateTimeOffset.UtcNow));
                command.Parameters.AddWithValue("$error", Truncate(reason));
            },
            cancellationToken);

    public Task MarkCanceledAsync(string id, CancellationToken cancellationToken = default) =>
        UpdateAsync(
            id,
            "state = $state, finished_at = $finished, error = NULL",
            command =>
            {
                command.Parameters.AddWithValue("$state", DownloadStates.Canceled);
                command.Parameters.AddWithValue("$finished", BookmarkRepository.Format(DateTimeOffset.UtcNow));
            },
            cancellationToken);

    /// <summary>最近下载（默认 100 条，按开始时间倒序）。</summary>
    public async Task<IReadOnlyList<DownloadEntry>> ListRecentAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var safeLimit = Math.Max(1, Math.Min(limit, 1000));
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run<IReadOnlyList<DownloadEntry>>(() =>
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT {SelectColumns} FROM downloads ORDER BY started_at DESC, rowid DESC LIMIT $limit;";
            command.Parameters.AddWithValue("$limit", safeLimit);
            var list = new List<DownloadEntry>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                list.Add(Read(reader));
            return list;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DownloadEntry?> FindAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run<DownloadEntry?>(() =>
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {SelectColumns} FROM downloads WHERE id = $id LIMIT 1;";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 把上次运行遗留的 in_progress 行标为失败。只处理 <paramref name="startedBefore"/>
    /// 之前创建的行，避免启动维护延迟执行时误伤本次进程新开始的下载。
    /// </summary>
    public async Task<int> MarkOrphanedAsync(
        DateTimeOffset startedBefore,
        CancellationToken cancellationToken = default)
    {
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            _database.WriteGate.Wait(cancellationToken);
            try
            {
                using var connection = _database.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = @"
UPDATE downloads
SET state = $failed, finished_at = $finished, error = $error
WHERE state = $inProgress AND started_at < $cutoff;";
                command.Parameters.AddWithValue("$failed", DownloadStates.Failed);
                command.Parameters.AddWithValue("$finished", BookmarkRepository.Format(DateTimeOffset.UtcNow));
                command.Parameters.AddWithValue("$error", "应用退出时下载未完成");
                command.Parameters.AddWithValue("$inProgress", DownloadStates.InProgress);
                command.Parameters.AddWithValue("$cutoff", BookmarkRepository.Format(startedBefore));
                return command.ExecuteNonQuery();
            }
            finally
            {
                _database.WriteGate.Release();
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 只把指定 id 的残留 in_progress 行标为失败。下载列表中的单条取消必须用此重载，
    /// 不得调用全表清理，否则会把其他健康的活动下载一起误标。
    /// </summary>
    public async Task<bool> MarkOrphanedAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            _database.WriteGate.Wait(cancellationToken);
            try
            {
                using var connection = _database.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = @"
UPDATE downloads
SET state = $failed, finished_at = $finished, error = $error
WHERE id = $id AND state = $inProgress;";
                command.Parameters.AddWithValue("$failed", DownloadStates.Failed);
                command.Parameters.AddWithValue("$finished", BookmarkRepository.Format(DateTimeOffset.UtcNow));
                command.Parameters.AddWithValue("$error", "该下载已不在当前会话中");
                command.Parameters.AddWithValue("$inProgress", DownloadStates.InProgress);
                command.Parameters.AddWithValue("$id", id);
                return command.ExecuteNonQuery() > 0;
            }
            finally
            {
                _database.WriteGate.Release();
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>清理已完成/失败/取消的记录（保留进行中），返回删除行数。</summary>
    public async Task<int> ClearFinishedAsync(CancellationToken cancellationToken = default)
    {
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            _database.WriteGate.Wait(cancellationToken);
            try
            {
                using var connection = _database.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM downloads WHERE state <> $inProgress;";
                command.Parameters.AddWithValue("$inProgress", DownloadStates.InProgress);
                return command.ExecuteNonQuery();
            }
            finally
            {
                _database.WriteGate.Release();
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateAsync(
        string id,
        string setClause,
        Action<SqliteCommand> bind,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;

        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await Task.Run(() =>
        {
            _database.WriteGate.Wait(cancellationToken);
            try
            {
                using var connection = _database.OpenConnection();
                using var command = connection.CreateCommand();
                // 进度与终态都只允许从 in_progress 迁移：迟到/重复事件不得改写终态，
                // 也不得在 Start 尚未落库时先创建一条“无主”记录。
                command.CommandText =
                    $"UPDATE downloads SET {setClause} WHERE id = $id AND state = $inProgress;";
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$inProgress", DownloadStates.InProgress);
                bind(command);
                command.ExecuteNonQuery();
            }
            finally
            {
                _database.WriteGate.Release();
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static DownloadEntry Read(SqliteDataReader reader) => new DownloadEntry
    {
        Id = reader.GetString(0),
        Url = reader.GetString(1),
        TargetPath = reader.GetString(2),
        State = reader.GetString(3),
        ReceivedBytes = reader.GetInt64(4),
        TotalBytes = reader.GetInt64(5),
        StartedAt = BookmarkRepository.Parse(reader.GetString(6)),
        FinishedAt = reader.IsDBNull(7) ? (DateTimeOffset?)null : BookmarkRepository.Parse(reader.GetString(7)),
        Error = reader.IsDBNull(8) ? null : reader.GetString(8),
    };

    private static string? Truncate(string? value) =>
        value == null ? null : value.Length <= 512 ? value : value.Substring(0, 512);
}
