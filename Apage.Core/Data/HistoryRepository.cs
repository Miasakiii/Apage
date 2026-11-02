#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Apage.Core.Data;

/// <summary>
/// 历史记录仓储（B2）：只接收成功的顶层 http/https 导航，隐私标签与本地页面由调用方排除。
/// 每条导航一行，不做跨会话去重；查询按访问时间倒序，搜索对 URL 与标题做子串匹配。
/// </summary>
public sealed class HistoryRepository
{
    private const int MaxTitleLength = 512;
    private const string SelectColumns = "id, url, title, visited_at";
    private readonly SqliteDatabase _database;

    public HistoryRepository(SqliteDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    /// <summary>记录一次访问；非 http/https 地址（含本地新标签页）静默忽略，返回 false。</summary>
    public async Task<bool> RecordAsync(
        string? url,
        string? title,
        DateTimeOffset visitedAt,
        CancellationToken cancellationToken = default)
    {
        if (!UrlKey.TryNormalize(url, out var key))
            return false;

        var safeTitle = Truncate(title ?? string.Empty);
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await Task.Run(() =>
        {
            _database.WriteGate.Wait(cancellationToken);
            try
            {
                using var connection = _database.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText =
                    "INSERT INTO history_entries(url, url_key, title, visited_at) VALUES ($url, $key, $title, $at);";
                command.Parameters.AddWithValue("$url", key);
                command.Parameters.AddWithValue("$key", key);
                command.Parameters.AddWithValue("$title", safeTitle);
                command.Parameters.AddWithValue("$at", BookmarkRepository.Format(visitedAt));
                command.ExecuteNonQuery();
            }
            finally
            {
                _database.WriteGate.Release();
            }
        }, cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>最近访问（默认 200 条）。</summary>
    public async Task<IReadOnlyList<HistoryEntry>> QueryRecentAsync(
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        var safeLimit = Math.Max(1, Math.Min(limit, 2000));
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run<IReadOnlyList<HistoryEntry>>(() =>
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT {SelectColumns} FROM history_entries ORDER BY visited_at DESC, id DESC LIMIT $limit;";
            command.Parameters.AddWithValue("$limit", safeLimit);
            return ReadAll(command);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按 URL 或标题搜索（大小写不敏感的子串匹配；空关键字返回最近记录）。</summary>
    public async Task<IReadOnlyList<HistoryEntry>> SearchAsync(
        string? keyword,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        var trimmed = keyword?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return await QueryRecentAsync(limit, cancellationToken).ConfigureAwait(false);

        var safeLimit = Math.Max(1, Math.Min(limit, 2000));
        var pattern = SqlLike.Contains(trimmed!);
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run<IReadOnlyList<HistoryEntry>>(() =>
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                $@"SELECT {SelectColumns} FROM history_entries
                   WHERE url LIKE $q ESCAPE '\' OR title LIKE $q ESCAPE '\'
                   ORDER BY visited_at DESC, id DESC LIMIT $limit;";
            command.Parameters.AddWithValue("$q", pattern);
            command.Parameters.AddWithValue("$limit", safeLimit);
            return ReadAll(command);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>清空全部历史，返回删除行数。</summary>
    public async Task<int> ClearAsync(CancellationToken cancellationToken = default)
    {
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            _database.WriteGate.Wait(cancellationToken);
            try
            {
                using var connection = _database.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM history_entries;";
                return command.ExecuteNonQuery();
            }
            finally
            {
                _database.WriteGate.Release();
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>删除早于 <paramref name="cutoff"/> 的记录（保留期裁剪），返回删除行数。</summary>
    public async Task<int> PruneAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            _database.WriteGate.Wait(cancellationToken);
            try
            {
                using var connection = _database.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM history_entries WHERE visited_at < $cutoff;";
                command.Parameters.AddWithValue("$cutoff", BookmarkRepository.Format(cutoff));
                return command.ExecuteNonQuery();
            }
            finally
            {
                _database.WriteGate.Release();
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<HistoryEntry> ReadAll(SqliteCommand command)
    {
        var list = new List<HistoryEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new HistoryEntry
            {
                Id = reader.GetInt64(0),
                Url = reader.GetString(1),
                Title = reader.GetString(2),
                VisitedAt = BookmarkRepository.Parse(reader.GetString(3)),
            });
        }
        return list;
    }

    private static string Truncate(string value) =>
        value.Length <= MaxTitleLength ? value : value.Substring(0, MaxTitleLength);
}
