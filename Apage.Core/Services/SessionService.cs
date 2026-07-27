#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Apage.Core.Models;

namespace Apage.Core.Services;

/// <summary>
/// 会话持久化：把 <see cref="SessionState"/> 序列化到注入的文件路径（便携数据目录下的 session.json）。
/// 与 SettingsService 一致的原子写（.tmp → 替换）；损坏/缺失一律回落空会话（宁可丢会话，不能启动失败）。
/// URL 可恢复过滤与索引收敛（<see cref="IsRestorableUrl"/> / <see cref="Normalize"/>）为纯逻辑，便于单测（决策 #18）。
/// </summary>
public sealed class SessionService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;

    /// <param name="filePath">session.json 的完整路径（如 {数据目录}\session.json）。</param>
    public SessionService(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("会话文件路径不能为空。", nameof(filePath));
        _filePath = filePath;
    }

    /// <summary>加载上次会话。文件缺失或 JSON 损坏均返回空会话（不抛异常，不写盘）。</summary>
    public async Task<SessionState> LoadAsync()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = await Task.Run(() => File.ReadAllText(_filePath)).ConfigureAwait(false);
                return JsonSerializer.Deserialize<SessionState>(json) ?? new SessionState();
            }
        }
        catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
        {
            // 损坏/读取失败：宁可丢失会话，也不能启动失败
        }
        return new SessionState();
    }

    /// <summary>保存会话（原子写：.tmp → 替换正式文件）。目录不存在时自动创建。</summary>
    public async Task SaveAsync(SessionState state)
    {
        if (state == null)
            throw new ArgumentNullException(nameof(state));

        var json = JsonSerializer.Serialize(state, JsonOptions);
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

    /// <summary>URL 是否可恢复：仅 http/https/file；空白与 about:（新标签页等）一律不恢复。</summary>
    public static bool IsRestorableUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        var u = url!.Trim();
        return u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || u.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 收敛会话：剔除不可恢复的标签，并把 ActiveIndex 夹到有效范围（空会话则为 0）。纯逻辑，便于单测。
    /// </summary>
    public static SessionState Normalize(SessionState? state)
    {
        var result = new SessionState();

        if (state?.Tabs != null)
        {
            foreach (var t in state.Tabs)
            {
                if (t != null && IsRestorableUrl(t.Url))
                    result.Tabs.Add(new SessionTab { Url = t.Url.Trim(), Title = t.Title ?? string.Empty });
            }
        }

        result.ActiveIndex = result.Tabs.Count == 0
            ? 0
            : Math.Min(Math.Max(state?.ActiveIndex ?? 0, 0), result.Tabs.Count - 1);

        return result;
    }
}
