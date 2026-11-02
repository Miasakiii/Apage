#nullable enable
using System;
using System.Threading;

namespace Apage.Core.Data;

/// <summary>
/// SQLitePCLRaw 静态 provider 初始化。
/// 单文件场景不能使用 bundle_e_sqlite3（其默认 dynamic provider 依赖 Assembly.Location），
/// 必须先由 Portable 层 NativeBootstrap 预加载 e_sqlite3.dll，再调用本类冻结静态 provider。
/// Lazy 保证全进程只设置一次；初始化失败会缓存异常，绝不静默回落到不可预期的 provider。
/// </summary>
public static class SqliteProvider
{
    private static readonly Lazy<bool> Initialization = new Lazy<bool>(
        InitializeCore,
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>确保静态 e_sqlite3 provider 已设置并冻结。可安全重复调用。</summary>
    public static void EnsureInitialized()
    {
        _ = Initialization.Value;
    }

    private static bool InitializeCore()
    {
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
        SQLitePCL.raw.FreezeProvider();
        return true;
    }
}
