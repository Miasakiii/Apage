#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apage.Core.Models;

namespace Apage.Core.Services;

/// <summary>
/// 隐私拦截统计持久化（docs/design/2026-10-23-privacy-stats-ntp.md §3.2）。
/// 按周统计广告规则拦截数与第三方 Cookie 剥离数，JSON 落盘到注入路径（{数据目录}\privacy-stats.json）。
/// 原子写与损坏回落同 <see cref="SettingsService"/> / <see cref="SessionService"/>：
/// 便携场景宁可丢一次统计，也不能启动失败。
/// 写入策略：计数在锁内累加并标脏；首次变更后短周期自动原子落盘，正常退出再显式
/// <see cref="SaveAsync"/>。崩溃最多损失一个自动保存批次，而不是整周统计。
/// </summary>
public sealed class PrivacyStatsService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly TimeSpan DefaultAutoSaveDelay = TimeSpan.FromSeconds(2);

    private readonly string _filePath;
    private readonly TimeSpan _autoSaveDelay;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly Timer _autoSaveTimer;

    private PrivacyStatsState _state;
    private int _changeVersion;
    private bool _dirty;
    private bool _disposed;
    private int _timerArmed;

    /// <param name="filePath">privacy-stats.json 的完整路径（如 {数据目录}\privacy-stats.json）。</param>
    public PrivacyStatsService(string filePath)
        : this(filePath, DefaultAutoSaveDelay)
    {
    }

    /// <summary>测试可注入较短的自动保存间隔；生产使用默认 2 秒。</summary>
    public PrivacyStatsService(string filePath, TimeSpan autoSaveDelay)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("隐私统计文件路径不能为空。", nameof(filePath));
        if (autoSaveDelay <= TimeSpan.Zero || autoSaveDelay.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(autoSaveDelay));

        _filePath = filePath;
        _autoSaveDelay = autoSaveDelay;
        _state = new PrivacyStatsState();
        _autoSaveTimer = new Timer(_ => _ = FlushDirtyAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>当前统计快照 (广告拦截数, 第三方 Cookie 剥离数)，供 WebMessage 回复（§3.2）。</summary>
    public (long Ads, long Trackers) Snapshot
    {
        get
        {
            lock (_gate)
            {
                return (_state.AdsBlocked, _state.TrackersBlocked);
            }
        }
    }

    /// <summary>是否存在尚未落盘的计数变更（诊断与测试用）。</summary>
    public bool HasPendingWrites
    {
        get
        {
            lock (_gate)
            {
                return _dirty;
            }
        }
    }

    /// <summary>
    /// 加载统计。文件缺失 / JSON 损坏 / 读取失败一律回落全新状态，随后按当前时间滚存
    /// （<see cref="PrivacyStatsState.RolledOver"/>）；跨周则计数归零。全程不抛异常。
    /// </summary>
    public async Task LoadAsync()
    {
        PrivacyStatsState loaded;
        try
        {
            if (File.Exists(_filePath))
            {
                var json = await Task.Run(() => File.ReadAllText(_filePath)).ConfigureAwait(false);
                loaded = JsonSerializer.Deserialize<PrivacyStatsState>(json) ?? new PrivacyStatsState();
            }
            else
            {
                loaded = new PrivacyStatsState();
            }
        }
        catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
        {
            // 损坏/读取失败：宁可回落全新状态，也不能启动失败
            loaded = new PrivacyStatsState();
        }

        var rolled = loaded.RolledOver(DateTime.Now);
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                _state = rolled;
                _dirty = false;
                _changeVersion++;
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>
    /// 保存当前统计（原子写：.tmp → 替换正式文件，exFAT 降级同 <see cref="SettingsService"/>）。
    /// 多次并发保存由信号量串行化，避免共享 .tmp 互相覆盖。目录不存在时自动创建。
    /// </summary>
    public async Task SaveAsync()
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            PrivacyStatsState snapshot;
            int version;
            lock (_gate)
            {
                snapshot = _state;
                version = _changeVersion;
            }

            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
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

            lock (_gate)
            {
                // 保存期间若又有新计数，保持脏标记，等待下一批
                if (_changeVersion == version)
                    _dirty = false;
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>记录广告规则拦截次数（锁内自增；先按当前时间滚存，跨周则归零重启）。</summary>
    public void RecordAdBlocked(int count = 1)
    {
        if (count <= 0)
            return;
        MarkDirty(state => state with { AdsBlocked = state.AdsBlocked + count });
    }

    /// <summary>记录第三方 Cookie 剥离次数（锁内自增；先按当前时间滚存，跨周则归零重启）。</summary>
    public void RecordTrackerBlocked(int count = 1)
    {
        if (count <= 0)
            return;
        MarkDirty(state => state with { TrackersBlocked = state.TrackersBlocked + count });
    }

    private void MarkDirty(Func<PrivacyStatsState, PrivacyStatsState> update)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _state = update(_state.RolledOver(DateTime.Now));
            _dirty = true;
            _changeVersion++;
        }

        // 只武装一个一次性 Timer；后续计数合并进同一批次。
        if (Interlocked.CompareExchange(ref _timerArmed, 1, 0) == 0)
        {
            try
            {
                _autoSaveTimer.Change(_autoSaveDelay, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                Interlocked.Exchange(ref _timerArmed, 0);
            }
        }
    }

    private async Task FlushDirtyAsync()
    {
        try
        {
            lock (_gate)
            {
                if (_disposed || !_dirty)
                    return;
            }
            await SaveAsync().ConfigureAwait(false);
        }
        catch
        {
            // 自动保存失败不能拖垮浏览；脏标记保留，下一次计数或退出保存会重试。
        }
        finally
        {
            Interlocked.Exchange(ref _timerArmed, 0);
            lock (_gate)
            {
                if (_dirty && !_disposed &&
                    Interlocked.CompareExchange(ref _timerArmed, 1, 0) == 0)
                {
                    try { _autoSaveTimer.Change(_autoSaveDelay, Timeout.InfiniteTimeSpan); }
                    catch (ObjectDisposedException) { Interlocked.Exchange(ref _timerArmed, 0); }
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _autoSaveTimer.Dispose();
    }
}
