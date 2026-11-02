using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Apage.Core.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Apage.Core.Tests;

/// <summary>
/// SQLite 安全基础设施（S0 §4.3 / B1）：
/// PRAGMA 基线、完整性检查、迁移前备份、事务回滚、损坏不删库、版本降级拒绝与并发初始化。
/// </summary>
public sealed class SqliteDatabaseTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly string _backupDir;

    public SqliteDatabaseTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ApageTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "apage.db");
        _backupDir = Path.Combine(_dir, "backups");
        SqliteProvider.EnsureInitialized();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* 清理失败不影响测试结论 */ }
    }

    [Fact]
    public async Task Initialize_NewDatabase_CreatesSchemaAndPragmaBaseline()
    {
        var database = new SqliteDatabase(_dbPath, SqliteSchema.CreateMigrations(), _backupDir);

        var info = await database.InitializeAsync();

        Assert.True(info.WasCreated);
        Assert.Equal(SqliteSchema.CurrentVersion, info.SchemaVersion);
        Assert.Equal(0, info.PreviousSchemaVersion);
        Assert.Null(info.BackupPath);
        Assert.True(File.Exists(_dbPath));

        using var connection = Open();
        Assert.Equal("delete", Scalar(connection, "PRAGMA journal_mode;")!.ToString()!.ToLowerInvariant());
        Assert.Equal("2", Scalar(connection, "PRAGMA synchronous;")!.ToString());
        Assert.Equal("1", Scalar(connection, "PRAGMA foreign_keys;")!.ToString());
        Assert.Equal(SqliteSchema.CurrentVersion.ToString(), Scalar(connection, "PRAGMA user_version;")!.ToString());

        var tables = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                tables.Add(reader.GetString(0));
        }
        Assert.Contains("bookmarks", tables);
        Assert.Contains("history_entries", tables);
        Assert.Contains("downloads", tables);
    }

    [Fact]
    public async Task Initialize_IsIdempotent_AndConcurrentCallersShareResult()
    {
        var database = new SqliteDatabase(_dbPath, SqliteSchema.CreateMigrations(), _backupDir);

        var results = await Task.WhenAll(
            database.InitializeAsync(),
            database.InitializeAsync(),
            database.InitializeAsync());

        Assert.All(results, r => Assert.Equal(SqliteSchema.CurrentVersion, r.SchemaVersion));
        Assert.All(results, r => Assert.Same(results[0], r));
    }

    [Fact]
    public async Task Migration_CreatesBackupBeforeUpgrade()
    {
        var v1 = new SqliteDatabase(_dbPath, SqliteSchema.CreateMigrations(), _backupDir);
        await v1.InitializeAsync();

        var migrations = SqliteSchema.CreateMigrations().Concat(new[]
        {
            new SqliteMigration(2, new[]
            {
                "CREATE TABLE migration_probe (id INTEGER PRIMARY KEY);",
                "INSERT INTO migration_probe(id) VALUES (7);"
            })
        }).ToArray();
        var v2 = new SqliteDatabase(_dbPath, migrations, _backupDir);

        var info = await v2.InitializeAsync();

        Assert.False(info.WasCreated);
        Assert.Equal(2, info.SchemaVersion);
        Assert.Equal(1, info.PreviousSchemaVersion);
        Assert.NotNull(info.BackupPath);
        Assert.True(File.Exists(info.BackupPath));

        using var upgraded = Open();
        Assert.Equal("2", Scalar(upgraded, "PRAGMA user_version;")!.ToString());
        Assert.Equal("7", Scalar(upgraded, "SELECT id FROM migration_probe;")!.ToString());

        using (var backup = new SqliteConnection($"Data Source={info.BackupPath};Mode=ReadOnly;Pooling=false"))
        {
            backup.Open();
            Assert.Equal("1", Scalar(backup, "PRAGMA user_version;")!.ToString());
            Assert.Null(Scalar(backup, "SELECT name FROM sqlite_master WHERE type='table' AND name='migration_probe';"));
        }

        // 备份可恢复：用备份覆盖当前库后，应用应能按 v1 正常打开且新表不存在。
        File.Copy(info.BackupPath!, _dbPath, overwrite: true);
        using var restored = Open();
        Assert.Equal("1", Scalar(restored, "PRAGMA user_version;")!.ToString());
        Assert.Null(Scalar(restored, "SELECT name FROM sqlite_master WHERE type='table' AND name='migration_probe';"));
    }

    [Fact]
    public async Task Migration_Failure_RollsBackSchemaAndUserVersion_PreservesBackup()
    {
        // 先建立一个合法的空数据库（user_version=0），再注入会中途失败的迁移。
        using (var seed = Open()) { /* Open 即创建空库 */ }
        var migrations = new[]
        {
            new SqliteMigration(1, new[]
            {
                "CREATE TABLE should_rollback (id INTEGER PRIMARY KEY);",
                "THIS IS NOT VALID SQL;"
            })
        };
        var database = new SqliteDatabase(_dbPath, migrations, _backupDir);

        var ex = await Assert.ThrowsAsync<SqliteMigrationException>(() => database.InitializeAsync());

        Assert.True(File.Exists(_dbPath));
        Assert.NotNull(ex.BackupPath);
        Assert.True(File.Exists(ex.BackupPath));
        using var connection = Open();
        Assert.Equal("0", Scalar(connection, "PRAGMA user_version;")!.ToString());
        Assert.Null(Scalar(connection, "SELECT name FROM sqlite_master WHERE type='table' AND name='should_rollback';"));
    }

    [Fact]
    public async Task CorruptedDatabase_ThrowsAndNeverDeletesOriginal()
    {
        var original = new byte[] { 0x41, 0x50, 0x41, 0x47, 0x45, 0x00, 0x7F, 0x42 };
        await File.WriteAllBytesAsync(_dbPath, original);

        var database = new SqliteDatabase(_dbPath);

        await Assert.ThrowsAsync<SqliteDatabaseCorruptedException>(() => database.InitializeAsync());
        Assert.True(File.Exists(_dbPath));
        Assert.Equal(original, await File.ReadAllBytesAsync(_dbPath));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(original)), Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(_dbPath))));
    }

    [Fact]
    public async Task NewerSchemaVersion_IsRejectedWithoutModification()
    {
        var seed = new SqliteDatabase(_dbPath, SqliteSchema.CreateMigrations(), _backupDir);
        await seed.InitializeAsync();
        using (var connection = Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            command.ExecuteNonQuery();
        }

        var database = new SqliteDatabase(_dbPath, SqliteSchema.CreateMigrations(), _backupDir);

        var ex = await Assert.ThrowsAsync<SqliteSchemaVersionException>(() => database.InitializeAsync());
        Assert.Equal(99, ex.CurrentVersion);
        Assert.Equal(SqliteSchema.CurrentVersion, ex.SupportedVersion);
        using var check = Open();
        Assert.Equal("99", Scalar(check, "PRAGMA user_version;")!.ToString());
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_dbPath};Mode=ReadWriteCreate;Pooling=false");
        connection.DefaultTimeout = 10;
        connection.Open();
        return connection;
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
