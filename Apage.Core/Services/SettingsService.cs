using System;
using System.Text.Json;
using System.Threading.Tasks;
using Apage.Core.Models;

namespace Apage.Core.Services;

/// <summary>
/// 设置持久化：System.Text.Json 序列化到注入的文件路径（便携数据目录下的 settings.json）。
/// 原子写统一交给 <see cref="AtomicJsonStore"/>：正式文件损坏或缺失时按 .bak/.tmp 恢复，
/// 绝不以“删除唯一正式副本”作为降级策略。路径完全由外部注入，本服务不感知 AppPaths。
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;

    /// <summary>当前内存中的设置；LoadAsync 之前为默认值。</summary>
    public AppSettings Current { get; private set; } = new();

    /// <param name="filePath">settings.json 的完整路径（如 {数据目录}\settings.json）。</param>
    public SettingsService(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("设置文件路径不能为空。", nameof(filePath));
        _filePath = filePath;
    }

    /// <summary>
    /// 加载设置。文件缺失、JSON 损坏或读取失败均回落默认值；
    /// 正式文件不可用时会尝试 .bak / .tmp，避免一次非原子中断导致整份设置丢失。
    /// 设置文件极小且位于首屏关键路径：同步读取，避免首次启动等待线程池调度。
    /// </summary>
    public Task<AppSettings> LoadAsync()
    {
        if (AtomicJsonStore.TryReadJson<AppSettings>(_filePath, JsonOptions, out var loaded) &&
            loaded != null)
        {
            Current = loaded;
        }
        else
        {
            Current = new AppSettings();
        }
        return Task.FromResult(Current);
    }

    /// <summary>保存当前设置（原子写：临时文件 → 原子替换；失败时保留旧文件与 .bak）。</summary>
    public async Task SaveAsync()
    {
        var json = JsonSerializer.Serialize(Current, JsonOptions);
        await AtomicJsonStore.SaveAsync(_filePath, json).ConfigureAwait(false);
    }
}
