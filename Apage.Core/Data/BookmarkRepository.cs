#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Apage.Core.Data;

/// <summary>
/// 书签仓储（B2 最小垂直切片）。一 URL 一行：<c>url_key</c> 唯一，重复保存视为更新标题。
/// 写入经 <see cref="SqliteDatabase.WriteGate"/> 串行化，读操作各自开会话，全部在后台线程执行，
/// 不阻塞 UI；SQLite 故障向上抛出，由调用方写入本地诊断并保持界面可用。
/// </summary>
public sealed class BookmarkRepository
{
    private const int MaxTitleLength = 512;
    private const string SelectColumns = "id, url, title, created_at, updated_at";
    private readonly SqliteDatabase _database;

    public BookmarkRepository(SqliteDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    /// <summary>全部书签，按创建时间倒序（最近添加在前）。</summary>
    public async Task<IReadOnlyList<BookmarkEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run<IReadOnlyList<BookmarkEntry>>(() =>
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {SelectColumns} FROM bookmarks ORDER BY created_at DESC, id DESC;";
            return ReadAll(command);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按标题或 URL 搜索书签（大小写不敏感子串匹配），供地址栏建议使用。</summary>
    public async Task<IReadOnlyList<BookmarkEntry>> SearchAsync(
        string? keyword,
        int limit = 8,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return Array.Empty<BookmarkEntry>();

        var safeLimit = Math.Max(1, Math.Min(limit, 50));
        var pattern = SqlLike.Contains(keyword!.Trim());
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run<IReadOnlyList<BookmarkEntry>>(() =>
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                $@"SELECT {SelectColumns} FROM bookmarks
                   WHERE title LIKE $q ESCAPE '\' OR url LIKE $q ESCAPE '\'
                   ORDER BY updated_at DESC, id DESC LIMIT $limit;";
            command.Parameters.AddWithValue("$q", pattern);
            command.Parameters.AddWithValue("$limit", safeLimit);
            return ReadAll(command);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>该 URL 是否已收藏（按归一化键判定）。非法 URL 返回 false。</summary>
    public async Task<bool> ContainsAsync(string? url, CancellationToken cancellationToken = default)
    {
        if (!UrlKey.TryNormalize(url, out var key))
            return false;

        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM bookmarks WHERE url_key = $key LIMIT 1;";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() != null;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 新增或更新书签（按归一化 URL 去重）。标题为空时保留旧标题。
    /// 非 http/https 地址直接抛 <see cref="ArgumentException"/>，调用方需先做 UI 侧过滤。
    /// </summary>
    public async Task<BookmarkEntry> SaveAsync(
        string url,
        string? title,
        CancellationToken cancellationToken = default)
    {
        if (!UrlKey.TryNormalize(url, out var key))
            throw new ArgumentException("只支持 http/https 书签。", nameof(url));

        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => SaveCore(key, title ?? string.Empty, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>按 id 删除；返回是否命中。</summary>
    public async Task<bool> RemoveAsync(long id, CancellationToken cancellationToken = default)
    {
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => RemoveCore("id = $value", id.ToString(CultureInfo.InvariantCulture), cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按 URL 删除（书签星标取消）；返回是否命中。</summary>
    public async Task<bool> RemoveByUrlAsync(string? url, CancellationToken cancellationToken = default)
    {
        if (!UrlKey.TryNormalize(url, out var key))
            return false;

        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => RemoveCore("url_key = $value", key, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>批量导入（单个事务）。文件内重复与非法地址在导入器阶段已剔除，这里再做一次兜底。</summary>
    public async Task<BookmarkImportOutcome> ImportAsync(
        IEnumerable<ImportedBookmark> items,
        CancellationToken cancellationToken = default)
    {
        if (items == null)
            throw new ArgumentNullException(nameof(items));

        var list = new List<ImportedBookmark>(items);
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => ImportCore(list, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    // ===== 同步核心（运行在线程池，持写门控）=====

    private BookmarkEntry SaveCore(string key, string title, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var safeTitle = Truncate(title).Trim(); // 纯空白视为“未提供标题”，保留旧值

        _database.WriteGate.Wait(cancellationToken);
        try
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO bookmarks(url, url_key, title, created_at, updated_at)
VALUES ($url, $key, $title, $now, $now)
ON CONFLICT(url_key) DO UPDATE SET
    url = excluded.url,
    title = CASE WHEN length(excluded.title) > 0 THEN excluded.title ELSE bookmarks.title END,
    updated_at = excluded.updated_at;";
                command.Parameters.AddWithValue("$url", key);
                command.Parameters.AddWithValue("$key", key);
                command.Parameters.AddWithValue("$title", safeTitle);
                command.Parameters.AddWithValue("$now", Format(now));
                command.ExecuteNonQuery();
            }

            var entry = ReadByKey(connection, transaction, key)
                ?? throw new InvalidOperationException("书签写入后无法读回。");
            transaction.Commit();
            return entry;
        }
        finally
        {
            _database.WriteGate.Release();
        }
    }

    private bool RemoveCore(string predicate, string value, CancellationToken cancellationToken)
    {
        _database.WriteGate.Wait(cancellationToken);
        try
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM bookmarks WHERE {predicate};";
            command.Parameters.AddWithValue("$value", value);
            return command.ExecuteNonQuery() > 0;
        }
        finally
        {
            _database.WriteGate.Release();
        }
    }

    private BookmarkImportOutcome ImportCore(IReadOnlyList<ImportedBookmark> items, CancellationToken cancellationToken)
    {
        var added = 0;
        var updated = 0;
        var skippedInvalid = 0;
        var skippedDuplicate = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var now = Format(DateTimeOffset.UtcNow);

        _database.WriteGate.Wait(cancellationToken);
        try
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();

            using var exists = connection.CreateCommand();
            exists.Transaction = transaction;
            exists.CommandText = "SELECT 1 FROM bookmarks WHERE url_key = $key LIMIT 1;";
            var existsKey = exists.Parameters.Add("$key", SqliteType.Text);

            using var upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText = @"
INSERT INTO bookmarks(url, url_key, title, created_at, updated_at)
VALUES ($url, $key, $title, $now, $now)
ON CONFLICT(url_key) DO UPDATE SET
    title = CASE WHEN length(excluded.title) > 0 THEN excluded.title ELSE bookmarks.title END,
    updated_at = excluded.updated_at;";
            var upUrl = upsert.Parameters.Add("$url", SqliteType.Text);
            var upKey = upsert.Parameters.Add("$key", SqliteType.Text);
            var upTitle = upsert.Parameters.Add("$title", SqliteType.Text);
            upsert.Parameters.AddWithValue("$now", now);

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (item == null || !UrlKey.TryNormalize(item.Url, out var key))
                {
                    skippedInvalid++;
                    continue;
                }

                if (!seen.Add(key))
                {
                    skippedDuplicate++;
                    continue;
                }

                existsKey.Value = key;
                var already = exists.ExecuteScalar() != null;

                upUrl.Value = key;
                upKey.Value = key;
                upTitle.Value = Truncate(item.Title ?? string.Empty).Trim();
                upsert.ExecuteNonQuery();

                if (already) updated++;
                else added++;
            }

            transaction.Commit();
        }
        finally
        {
            _database.WriteGate.Release();
        }

        return new BookmarkImportOutcome(added, updated, skippedInvalid, skippedDuplicate);
    }

    private static BookmarkEntry? ReadByKey(SqliteConnection connection, SqliteTransaction transaction, string key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {SelectColumns} FROM bookmarks WHERE url_key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", key);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static IReadOnlyList<BookmarkEntry> ReadAll(SqliteCommand command)
    {
        var list = new List<BookmarkEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            list.Add(Read(reader));
        return list;
    }

    private static BookmarkEntry Read(SqliteDataReader reader) => new BookmarkEntry
    {
        Id = reader.GetInt64(0),
        Url = reader.GetString(1),
        Title = reader.GetString(2),
        CreatedAt = Parse(reader.GetString(3)),
        UpdatedAt = Parse(reader.GetString(4)),
    };

    private static string Truncate(string value) =>
        value.Length <= MaxTitleLength ? value : value.Substring(0, MaxTitleLength);

    internal static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    internal static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
