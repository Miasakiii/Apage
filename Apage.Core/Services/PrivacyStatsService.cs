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
/// 当前只统计广告规则拦截数，按周（周一 00:00 本地）落盘到注入路径。
/// TrackersBlocked 字段仅为旧 JSON 兼容保留，不再增长、不再展示：
/// 事件级第三方 Cookie 删除已被真机探针证伪（docs/spikes/results/cookie-privacy.md）。
/// 写入策略：计数在锁内累加并标脏；首次变更后短周期自动原子落盘，正常退出再显式
/// <see cref="SaveAsync"/>。崩溃最多损失一个自动保存批次。
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

    /// <summary>当前统计快照（广告拦截数, 旧追踪计数字段）；追踪值不再增长，仅兼容旧文件。</summary>
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
        var loaded = AtomicJsonStore.TryReadJson<PrivacyStatsState>(_filePath, JsonOptions, out var value)
            ? value ?? new PrivacyStatsState()
            : new PrivacyStatsState();

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
    /// 保存当前统计。写入委托 <see cref="AtomicJsonStore"/>：Replace 不可用时先备份旧文件，
    /// 不再采用“删除正式文件再移动”的非原子降级。并发保存由信号量串行化。
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
            await AtomicJsonStore.SaveAsync(_filePath, json).ConfigureAwait(false);

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

    /// <summary>旧追踪计数入口，仅为历史数据兼容与回归保留；产品代码不得再调用。</summary>
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
