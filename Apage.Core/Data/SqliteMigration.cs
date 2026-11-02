#nullable enable
using System;
using System.Collections.Generic;

namespace Apage.Core.Data;

/// <summary>
/// 一次单步 schema 迁移。版本必须从 1 连续递增，所有语句在同一个事务中执行；
/// 任一语句失败即整体回滚，不允许半迁移数据库进入运行态。
/// </summary>
public sealed class SqliteMigration
{
    public SqliteMigration(int version, IReadOnlyList<string> statements)
    {
        if (version <= 0)
            throw new ArgumentOutOfRangeException(nameof(version));
        if (statements == null)
            throw new ArgumentNullException(nameof(statements));
        if (statements.Count == 0)
            throw new ArgumentException("迁移必须至少包含一条 SQL。", nameof(statements));

        Version = version;
        Statements = statements;
    }

    public int Version { get; }

    public IReadOnlyList<string> Statements { get; }
}
