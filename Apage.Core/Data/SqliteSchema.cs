#nullable enable
using System.Collections.Generic;

namespace Apage.Core.Data;

/// <summary>
/// v0.1 最小结构化数据 schema。设置/会话/周统计继续 JSON，不在迁移范围内。
/// 表结构只覆盖书签、历史、下载三类新增数据；文件夹、FTS 等后续能力另起迁移。
/// </summary>
public static class SqliteSchema
{
    public const int CurrentVersion = 1;

    public static IReadOnlyList<SqliteMigration> CreateMigrations() =>
        new[]
        {
            new SqliteMigration(1, new[]
            {
                @"CREATE TABLE IF NOT EXISTS bookmarks (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    url TEXT NOT NULL,
                    url_key TEXT NOT NULL UNIQUE CHECK (length(url_key) > 0),
                    title TEXT NOT NULL DEFAULT '',
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );",
                "CREATE INDEX IF NOT EXISTS ix_bookmarks_created_at ON bookmarks(created_at);",
                @"CREATE TABLE IF NOT EXISTS history_entries (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    url TEXT NOT NULL,
                    url_key TEXT NOT NULL CHECK (length(url_key) > 0),
                    title TEXT NOT NULL DEFAULT '',
                    visited_at TEXT NOT NULL
                );",
                "CREATE INDEX IF NOT EXISTS ix_history_visited_at ON history_entries(visited_at DESC);",
                "CREATE INDEX IF NOT EXISTS ix_history_url_key ON history_entries(url_key);",
                @"CREATE TABLE IF NOT EXISTS downloads (
                    id TEXT PRIMARY KEY,
                    url TEXT NOT NULL,
                    target_path TEXT NOT NULL,
                    state TEXT NOT NULL CHECK (state IN ('in_progress', 'completed', 'failed', 'canceled')),
                    received_bytes INTEGER NOT NULL DEFAULT 0,
                    total_bytes INTEGER NOT NULL DEFAULT 0,
                    started_at TEXT NOT NULL,
                    finished_at TEXT NULL,
                    error TEXT NULL
                );",
                "CREATE INDEX IF NOT EXISTS ix_downloads_started_at ON downloads(started_at DESC);"
            })
        };
}
