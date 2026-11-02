#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Apage.Core.Data;

/// <summary>
/// SQLite 安全初始化入口（docs/design/2026-10-28-s0-baseline-contracts.md §4.3）。
/// 负责：创建目录、静态 provider、PRAGMA 基线、完整性检查、迁移前备份、
/// user_version 事务迁移和失败回滚。迁移/完整性失败一律抛出且保留原库，
/// 绝不通过删库“自愈”。初始化本身不持有长连接，可在窗口显示后后台调用。
/// </summary>
public sealed class SqliteDatabase
{
    private readonly string _databasePath;
    private readonly string _backupDirectory;
    private readonly IReadOnlyList<SqliteMigration> _migrations;
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

    private SqliteInitializationInfo? _initialized;

    public SqliteDatabase(string databasePath)
        : this(databasePath, SqliteSchema.CreateMigrations(), null)
    {
    }

    /// <summary>可注入迁移与备份目录，供故障注入和未来测试使用。</summary>
    public SqliteDatabase(
        string databasePath,
        IReadOnlyList<SqliteMigration> migrations,
        string? backupDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("数据库路径不能为空。", nameof(databasePath));
        if (migrations == null)
            throw new ArgumentNullException(nameof(migrations));

        _databasePath = Path.GetFullPath(databasePath);
        _backupDirectory = Path.GetFullPath(
            backupDirectory ?? Path.Combine(Path.GetDirectoryName(_databasePath) ?? ".", "backups"));
        _migrations = ValidateMigrations(migrations);
    }

    public string DatabasePath => _databasePath;

    public int TargetSchemaVersion => _migrations.Count == 0 ? 0 : _migrations[_migrations.Count - 1].Version;

    /// <summary>
    /// 写入门控：仓储层所有写事务经此串行化。SQLite 的 DELETE journal 在多连接并发写时
    /// 仍可能返回 SQLITE_BUSY，单进程内串行写入比依赖 busy_timeout 重试更可预期。
    /// </summary>
    public System.Threading.SemaphoreSlim WriteGate { get; } =
        new System.Threading.SemaphoreSlim(1, 1);

    /// <summary>
    /// 打开一条配置了 PRAGMA 基线的连接（调用方负责释放）。
    /// 仓储在 <see cref="InitializeAsync"/> 之后使用；未初始化时表可能不存在。
    /// </summary>
    public SqliteConnection OpenConnection()
    {
        SqliteProvider.EnsureInitialized();
        var connection = new SqliteConnection(BuildConnectionString(_databasePath));
        connection.DefaultTimeout = 10;
        connection.Open();
        ConfigureConnection(connection);
        return connection;
    }

    /// <summary>
    /// 后台执行安全初始化。可重复调用；成功后返回同一份结果。
    /// 不会删除损坏数据库；调用方应把异常写入本地诊断，并让依赖数据的 UI 保持不可用。
    /// </summary>
    public async Task<SqliteInitializationInfo> InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized != null)
                return _initialized;

            var info = await Task.Run(() => InitializeCore(cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            _initialized = info;
            return info;
        }
        finally
        {
            _gate.Release();
        }
    }

    private SqliteInitializationInfo InitializeCore(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SqliteProvider.EnsureInitialized();

        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var wasCreated = !File.Exists(_databasePath);
        using var connection = new SqliteConnection(BuildConnectionString(_databasePath));
        connection.DefaultTimeout = 10;
        try
        {
            connection.Open();
            ConfigureConnection(connection);
            EnsureIntegrity(connection);
        }
        catch (SqliteException ex)
        {
            throw new SqliteDatabaseCorruptedException(
                "SQLite 数据库无法安全打开或完整性检查失败；原文件已保留，未执行删除或覆盖。",
                _databasePath,
                ex);
        }

        var currentVersion = ReadUserVersion(connection);
        if (currentVersion > TargetSchemaVersion)
        {
            throw new SqliteSchemaVersionException(
                $"数据库 schema 版本 {currentVersion} 高于当前应用支持的 {TargetSchemaVersion}；拒绝降级修改。",
                _databasePath,
                currentVersion,
                TargetSchemaVersion);
        }

        string? backupPath = null;
        if (currentVersion < TargetSchemaVersion)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!wasCreated)
                backupPath = CreateBackup(connection, currentVersion);

            ApplyMigrations(connection, currentVersion, backupPath, cancellationToken);
            EnsureIntegrity(connection);
        }

        return new SqliteInitializationInfo(
            _databasePath,
            TargetSchemaVersion,
            wasCreated,
            backupPath,
            currentVersion);
    }

    private void ApplyMigrations(
        SqliteConnection connection,
        int currentVersion,
        string? backupPath,
        CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction();
        try
        {
            foreach (var migration in _migrations)
            {
                if (migration.Version <= currentVersion)
                    continue;

                cancellationToken.ThrowIfCancellationRequested();
                foreach (var statement in migration.Statements)
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = statement;
                    command.ExecuteNonQuery();
                }

                SetUserVersion(connection, transaction, migration.Version);
            }

            transaction.Commit();
        }
        catch (Exception ex) when (ex is SqliteException || ex is InvalidOperationException)
        {
            try { transaction.Rollback(); }
            catch (SqliteException) { /* 连接损坏时回滚也可能失败；原库仍在 */ }

            throw new SqliteMigrationException(
                "SQLite schema 迁移失败，事务已回滚；原库未被删除。",
                _databasePath,
                backupPath,
                ex);
        }
    }

    private string CreateBackup(SqliteConnection source, int sourceVersion)
    {
        Directory.CreateDirectory(_backupDirectory);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        var path = Path.Combine(_backupDirectory, $"apage-v{sourceVersion}-{stamp}.db");
        if (File.Exists(path))
            path = Path.Combine(_backupDirectory, $"apage-v{sourceVersion}-{stamp}-{Guid.NewGuid():N}.db");

        try
        {
            using var destination = new SqliteConnection(BuildConnectionString(path));
            destination.Open();
            source.BackupDatabase(destination);
            return path;
        }
        catch (Exception ex) when (ex is SqliteException || ex is IOException || ex is UnauthorizedAccessException)
        {
            throw new SqliteDatabaseException(
                "迁移前备份失败，已中止迁移；原库未修改。",
                _databasePath,
                ex);
        }
    }

    private static void ConfigureConnection(SqliteConnection connection)
    {
        Execute(connection, "PRAGMA journal_mode=DELETE;");
        Execute(connection, "PRAGMA synchronous=FULL;");
        Execute(connection, "PRAGMA foreign_keys=ON;");
        Execute(connection, "PRAGMA busy_timeout=5000;");
    }

    private static void EnsureIntegrity(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        using var reader = command.ExecuteReader();
        var sawOk = false;
        while (reader.Read())
        {
            var value = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            if (string.Equals(value, "ok", StringComparison.OrdinalIgnoreCase))
            {
                sawOk = true;
                continue;
            }

            throw new SqliteDatabaseCorruptedException(
                "SQLite 完整性检查失败；原文件已保留，请从备份恢复或人工处理。",
                connection.DataSource,
                null);
        }

        if (!sawOk)
        {
            throw new SqliteDatabaseCorruptedException(
                "SQLite 完整性检查未返回 ok；原文件已保留。",
                connection.DataSource,
                null);
        }
    }

    private static int ReadUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void SetUserVersion(SqliteConnection connection, SqliteTransaction transaction, int version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version = " + version + ";";
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string BuildConnectionString(string path) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();

    private static IReadOnlyList<SqliteMigration> ValidateMigrations(IReadOnlyList<SqliteMigration> migrations)
    {
        var ordered = migrations.OrderBy(m => m.Version).ToArray();
        for (var i = 0; i < ordered.Length; i++)
        {
            if (ordered[i].Version != i + 1)
                throw new ArgumentException("迁移版本必须从 1 开始连续递增，不允许缺号或重复。", nameof(migrations));
        }
        return ordered;
    }
}

public sealed class SqliteInitializationInfo
{
    public SqliteInitializationInfo(
        string databasePath,
        int schemaVersion,
        bool wasCreated,
        string? backupPath,
        int previousSchemaVersion)
    {
        DatabasePath = databasePath;
        SchemaVersion = schemaVersion;
        WasCreated = wasCreated;
        BackupPath = backupPath;
        PreviousSchemaVersion = previousSchemaVersion;
    }

    public string DatabasePath { get; }
    public int SchemaVersion { get; }
    public bool WasCreated { get; }
    public string? BackupPath { get; }
    public int PreviousSchemaVersion { get; }
}

public class SqliteDatabaseException : Exception
{
    public SqliteDatabaseException(string message, string databasePath, Exception? innerException = null)
        : base(message, innerException)
    {
        DatabasePath = databasePath;
    }

    public string DatabasePath { get; }
}

public sealed class SqliteDatabaseCorruptedException : SqliteDatabaseException
{
    public SqliteDatabaseCorruptedException(string message, string databasePath, Exception? innerException)
        : base(message, databasePath, innerException)
    {
    }
}

public sealed class SqliteMigrationException : SqliteDatabaseException
{
    public SqliteMigrationException(
        string message,
        string databasePath,
        string? backupPath,
        Exception innerException)
        : base(message, databasePath, innerException)
    {
        BackupPath = backupPath;
    }

    public string? BackupPath { get; }
}

public sealed class SqliteSchemaVersionException : SqliteDatabaseException
{
    public SqliteSchemaVersionException(
        string message,
        string databasePath,
        int currentVersion,
        int supportedVersion)
        : base(message, databasePath)
    {
        CurrentVersion = currentVersion;
        SupportedVersion = supportedVersion;
    }

    public int CurrentVersion { get; }
    public int SupportedVersion { get; }
}
