using System;
using System.IO;
using System.Threading.Tasks;

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

    /// <summary>单实例服务（按数据目录判定，R13）。</summary>
    public SingleInstanceService SingleInstance { get; }

    /// <param name="dataDirectory">数据目录；settings.json 位于其下。</param>
    public AppServices(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("数据目录不能为空。", nameof(dataDirectory));

        DataDirectory = Path.GetFullPath(dataDirectory);
        Settings = new SettingsService(Path.Combine(DataDirectory, "settings.json"));
        Session = new SessionService(Path.Combine(DataDirectory, "session.json"));
        SingleInstance = new SingleInstanceService(DataDirectory);
    }

    /// <summary>
    /// 启动初始化：只加载设置（懒加载原则，其余服务一律后置）。
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
        SingleInstance.Dispose();
    }
}
