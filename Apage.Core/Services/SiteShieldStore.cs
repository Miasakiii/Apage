#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apage.Core.Models;

namespace Apage.Core.Services;

/// <summary>
/// 按站点隐私覆盖存储（R5）。落 <c>{数据目录}\site-shields.json</c>，经
/// <see cref="AtomicJsonStore"/> 原子写；损坏/缺失回落空表（绝不抛异常，
/// 对齐 SettingsService / SessionService 容错范式）。
/// <para>
/// 红线约束（单测把守）：
/// ① 站点级关闭绝不回写全局 <see cref="AppSettings"/>——本存储与设置文件物理隔离；
/// ② 覆盖查询在拦截管线与头注入<b>之前</b>；彻底关闭的站点跳过整个 Web 管道；
/// ③ host 归一化与 FaviconStore 同构，非法主机拒绝入表。
/// </para>
/// </summary>
public sealed class SiteShieldStore
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private Dictionary<string, SiteShieldEntry> _sites =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private bool _loaded;

    /// <param name="dataDirectory">数据目录（与 settings.json 同级）。</param>
    public SiteShieldStore(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("数据目录不能为空。", nameof(dataDirectory));
        _filePath = Path.Combine(dataDirectory, "site-shields.json");
    }

    /// <summary>文件路径（测试与诊断用）。</summary>
    public string FilePath => _filePath;

    /// <summary>
    /// 加载覆盖表（幂等；重复调用只读一次盘）。损坏文件回落空表并记警告——
    /// 站点覆盖是增强数据，丢失只意味着回到全局默认，不得阻断浏览。
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded) return;
            if (AtomicJsonStore.TryReadJson(_filePath, _jsonOptions, out SiteShieldFile? file)
                && file?.Sites != null)
            {
                _sites = new Dictionary<string, SiteShieldEntry>(file.Sites, StringComparer.Ordinal);
            }
            else
            {
                _sites = new Dictionary<string, SiteShieldEntry>(StringComparer.Ordinal);
            }
            _loaded = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>查询站点是否彻底关闭防护。未加载/无记录 = false（全局默认生效）。</summary>
    public bool IsDisabled(string? host)
    {
        var key = SiteShieldState.NormalizeHost(host);
        if (key == null) return false;
        lock (_sites)
        {
            return _sites.TryGetValue(key, out var entry) && entry.Disabled;
        }
    }

    /// <summary>全部覆盖条目（设置页管理用；按主机名排序，快照语义）。</summary>
    public IReadOnlyList<(string Host, SiteShieldEntry Entry)> Snapshot()
    {
        lock (_sites)
        {
            return _sites
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => (kv.Key, kv.Value))
                .ToList();
        }
    }

    /// <summary>覆盖条目数量。</summary>
    public int Count
    {
        get
        {
            lock (_sites) { return _sites.Count; }
        }
    }

    /// <summary>
    /// 设置或清除某个主机的覆盖。host 非法时拒绝（返回 false）——与 FaviconStore
    /// 「非法输入一律拒绝」同向，避免把奇怪字符串写进持久化文件。
    /// </summary>
    /// <param name="disabled">true = 彻底关闭；false = 删除该主机的覆盖（回到全局默认）。</param>
    public async Task<bool> SetAsync(string? host, bool disabled, string? note = null,
        CancellationToken cancellationToken = default)
    {
        var key = SiteShieldState.NormalizeHost(host);
        if (key == null) return false;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!disabled)
            {
                if (!_sites.Remove(key)) return true; // 本就没覆盖，视为成功
            }
            else
            {
                _sites[key] = new SiteShieldEntry
                {
                    Disabled = true,
                    Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                    UpdatedAt = DateTime.Now.ToString("o", CultureInfo.InvariantCulture),
                };
            }
            await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>移除某个主机的覆盖（回到全局默认）。</summary>
    public Task<bool> RemoveAsync(string? host, CancellationToken cancellationToken = default) =>
        SetAsync(host, disabled: false, note: null, cancellationToken);

    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, SiteShieldEntry> copy;
        lock (_sites)
        {
            copy = new Dictionary<string, SiteShieldEntry>(_sites, StringComparer.Ordinal);
        }
        var file = new SiteShieldFile { Version = 1, Sites = copy };
        var json = JsonSerializer.Serialize(file, _jsonOptions);
        await AtomicJsonStore.SaveAsync(_filePath, json, cancellationToken).ConfigureAwait(false);
    }
}
