using System;
using System.IO;
using System.Threading.Tasks;
using Apage.Core.Data;

namespace Apage.Core.Services;

/// <summary>
/// 服务聚合根（docs/product-design.md §4.3）。
/// 冷启动关键路径只初始化 Settings（<1s 窗口可见的目标所迫）；
/// 后续服务（书签/历史/广告拦截/壁纸/会话等）落地后在此聚合，并接入下方懒加载位。
/// </summary>
public sealed class AppServices : IDisposable
{
    /// <summary>数据目录全路径（便携版通常为程序旁的 data\）。</summary>
    public string DataDirectory { get; }

    /// <summary>设置服务（{数据目录}\settings.json）。</summary>
    public SettingsService Settings { get; }

    /// <summary>会话服务（{数据目录}\session.json）：退出保存打开的标签，启动恢复。</summary>
    public SessionService Session { get; }

    /// <summary>隐私拦截统计服务（{数据目录}\privacy-stats.json）：按周统计广告/追踪拦截数（§3.2）。</summary>
    public PrivacyStatsService PrivacyStats { get; }

    /// <summary>单实例服务（按数据目录判定，R13）。</summary>
    public SingleInstanceService SingleInstance { get; }

    /// <summary>本地滚动诊断日志（{数据目录}\logs）：默认不含 URL/搜索词/Cookie，只手动导出。</summary>
    public LocalLogService LocalLog { get; }

    /// <summary>SQLite 安全初始化入口（{数据目录}\apage.db）；首个标签创建后后台执行，不阻塞首屏。</summary>
    public SqliteDatabase Database { get; }

    /// <summary>书签仓储（SQLite）。须在 <see cref="Database"/> 初始化成功后使用。</summary>
    public BookmarkRepository Bookmarks { get; }

    /// <summary>历史记录仓储（SQLite）：仅记录成功的 http/https 顶层导航。</summary>
    public HistoryRepository History { get; }

    /// <summary>下载记录仓储（SQLite）。</summary>
    public DownloadRepository Downloads { get; }

    /// <summary>规则快照目录（{数据目录}\privacy）：手动更新后的 rules.txt + rules.json。</summary>
    public string PrivacyDirectory { get; }

    /// <param name="dataDirectory">数据目录；settings.json 位于其下。</param>
    public AppServices(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("数据目录不能为空。", nameof(dataDirectory));

        DataDirectory = Path.GetFullPath(dataDirectory);
        PrivacyDirectory = Path.Combine(DataDirectory, "privacy");
        Settings = new SettingsService(Path.Combine(DataDirectory, "settings.json"));
        Session = new SessionService(Path.Combine(DataDirectory, "session.json"));
        PrivacyStats = new PrivacyStatsService(Path.Combine(DataDirectory, "privacy-stats.json"));
        SingleInstance = new SingleInstanceService(DataDirectory);
        LocalLog = new LocalLogService(Path.Combine(DataDirectory, "logs"));
        Database = new SqliteDatabase(Path.Combine(DataDirectory, "apage.db"));
        Bookmarks = new BookmarkRepository(Database);
        History = new HistoryRepository(Database);
        Downloads = new DownloadRepository(Database);
    }

    /// <summary>
    /// 启动关键路径：只加载设置（懒加载原则，其余服务一律后置）。
    /// 周统计与会话由首个标签初始化阶段加载，避免阻塞主窗口首次呈现。
    /// </summary>
    public async Task InitializeAsync()
    {
        await Settings.LoadAsync().ConfigureAwait(false);

        // 懒加载位 ①（冷启动第二批，后续服务落地后接入）：
        // await Task.WhenAll(Bookmarks.LoadAsync(), Wallpaper.LoadAsync()).ConfigureAwait(false);
    }

    // 懒加载位 ②（后台加载，历史/广告规则等大件，启动后另行触发）：
    // public Task EnsureBackgroundAsync() => Task.Run(async () =>
    // {
    //     await Task.WhenAll(History.LoadAsync(), AdBlock.LoadAsync());
    // });

    public void Dispose()
    {
        PrivacyStats.Dispose();
        SingleInstance.Dispose();
    }
}
