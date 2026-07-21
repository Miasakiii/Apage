using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Apage.Core.Models;

namespace Apage.Core.Services;

/// <summary>
/// 设置持久化：System.Text.Json 序列化到注入的文件路径（便携数据目录下的 settings.json）。
/// 原子写：先写 .tmp 再替换目标文件，避免崩溃/断电留下半截 JSON。
/// 路径完全由外部注入，本服务不感知 AppPaths。
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
    /// 加载设置。文件缺失返回默认值；JSON 损坏/读取失败同样回落默认值
    /// （便携场景宁可重置设置，也不能启动失败）。
    /// </summary>
    public async Task<AppSettings> LoadAsync()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = await Task.Run(() => File.ReadAllText(_filePath)).ConfigureAwait(false);
                Current = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
        {
            Current = new AppSettings();
        }
        return Current;
    }

    /// <summary>保存当前设置（原子写：.tmp → 替换正式文件）。目录不存在时自动创建。</summary>
    public async Task SaveAsync()
    {
        var json = JsonSerializer.Serialize(Current, JsonOptions);
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmpPath = _filePath + ".tmp";
        await Task.Run(() =>
        {
            File.WriteAllText(tmpPath, json);
            try
            {
                if (File.Exists(_filePath))
                    File.Replace(tmpPath, _filePath, null); // 原子替换
                else
                    File.Move(tmpPath, _filePath);
            }
            catch (Exception ex) when (ex is IOException || ex is PlatformNotSupportedException || ex is UnauthorizedAccessException)
            {
                // 部分文件系统（如 exFAT U 盘）不支持 ReplaceFile，降级为删除后移动
                if (File.Exists(_filePath))
                    File.Delete(_filePath);
                File.Move(tmpPath, _filePath);
            }
        }).ConfigureAwait(false);
    }
}
