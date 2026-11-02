#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Apage.Core.Services;

/// <summary>一个可更新的规则来源（名称 + HTTPS 地址）。</summary>
public sealed class RuleUpdateSource
{
    public RuleUpdateSource(string name, string url)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Url = url ?? throw new ArgumentNullException(nameof(url));
    }

    public string Name { get; }

    public string Url { get; }
}

/// <summary>默认规则来源；与 tools/SnapshotBuilder 的随包快照保持同一组列表。</summary>
public static class RuleUpdateSources
{
    public static IReadOnlyList<RuleUpdateSource> Default { get; } = new[]
    {
        new RuleUpdateSource("EasyList", "https://easylist.to/easylist/easylist.txt"),
        new RuleUpdateSource("EasyPrivacy", "https://easylist.to/easylist/easyprivacy.txt"),
    };
}

/// <summary>一次手动更新的结果；失败时 Message 可直接展示给用户。</summary>
public sealed class RuleUpdateOutcome
{
    private RuleUpdateOutcome(bool succeeded, string message, int accepted, int skipped, DateTimeOffset? generatedAtUtc)
    {
        Succeeded = succeeded;
        Message = message;
        AcceptedRules = accepted;
        SkippedRules = skipped;
        GeneratedAtUtc = generatedAtUtc;
    }

    public bool Succeeded { get; }

    public string Message { get; }

    public int AcceptedRules { get; }

    public int SkippedRules { get; }

    public DateTimeOffset? GeneratedAtUtc { get; }

    internal static RuleUpdateOutcome Ok(int accepted, int skipped, DateTimeOffset generatedAtUtc) =>
        new RuleUpdateOutcome(
            true,
            $"规则已更新：{accepted} 条可执行 / {skipped} 条跳过，立即生效。",
            accepted,
            skipped,
            generatedAtUtc);

    internal static RuleUpdateOutcome Fail(string message) =>
        new RuleUpdateOutcome(false, message, 0, 0, null);
}

/// <summary>
/// 手动更新编排（A3）：下载 → 共用筛选 → 原子替换 → 热替换引擎。
/// 传输由调用方注入（Core 不引用任何 HTTP 客户端类型，保持零网络能力）；
/// 任何一步失败都不改动现有快照，用户继续用旧规则。
/// </summary>
public sealed class RuleUpdateService
{
    private readonly string _snapshotDirectory;
    private readonly AdBlockRuleProvider _provider;
    private readonly Func<RuleUpdateSource, CancellationToken, Task<string>> _download;
    private readonly Action<string>? _log;
    private readonly IReadOnlyList<RuleUpdateSource> _sources;
    private readonly string? _userRuleDirectory;
    private readonly SemaphoreSlim _updateGate = new SemaphoreSlim(1, 1);

    public RuleUpdateService(
        string snapshotDirectory,
        AdBlockRuleProvider provider,
        Func<RuleUpdateSource, CancellationToken, Task<string>> download,
        Action<string>? log = null,
        IReadOnlyList<RuleUpdateSource>? sources = null,
        string? userRuleDirectory = null)
    {
        _snapshotDirectory = string.IsNullOrWhiteSpace(snapshotDirectory)
            ? throw new ArgumentException("快照目录不能为空。", nameof(snapshotDirectory))
            : snapshotDirectory;
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _download = download ?? throw new ArgumentNullException(nameof(download));
        _log = log;
        _sources = sources ?? RuleUpdateSources.Default;
        _userRuleDirectory = userRuleDirectory;
    }

    /// <summary>
    /// 手动更新入口。整个“读用户规则 → 下载 → 构建 → 落盘 → 回读 → 热替换”流程串行化：
    /// 多个设置窗口或其他调用方不能交错替换 rules.txt / rules.json。
    /// </summary>
    public async Task<RuleUpdateOutcome> UpdateAsync(CancellationToken cancellationToken = default)
    {
        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await UpdateCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    private async Task<RuleUpdateOutcome> UpdateCoreAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> userRules;
        try
        {
            // 在下载和替换之前读取：后续只做纯内存合并，不会在提交后因文件 IO 失败而丢规则。
            userRules = UserRuleStore.LoadAll(_userRuleDirectory);
        }
        catch (Exception ex)
        {
            _log?.Invoke("rule-update-user-rules-failed:" + ex.GetType().Name);
            return RuleUpdateOutcome.Fail("读取本地用户规则失败，现有规则未改动。");
        }

        var texts = new List<RuleTextSource>(_sources.Count);
        foreach (var source in _sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var text = await _download(source, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(text))
                    return RuleUpdateOutcome.Fail($"{source.Name} 返回空内容，已放弃更新。");
                texts.Add(new RuleTextSource(source.Name, source.Url, text));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"rule-update-download-failed:{source.Name}:{ex.GetType().Name}");
                return RuleUpdateOutcome.Fail($"{source.Name} 下载失败（{ex.GetType().Name}），现有规则未改动。");
            }
        }

        var generatedAt = DateTimeOffset.UtcNow;
        var result = RuleSnapshotBuilder.Build(texts, generatedAt);
        if (result.AcceptedRules < RuleSnapshotBuilder.MinimumAcceptedRules)
        {
            _log?.Invoke($"rule-update-too-few:{result.AcceptedRules}");
            return RuleUpdateOutcome.Fail(
                $"只解析出 {result.AcceptedRules} 条可执行规则（安全下限 " +
                $"{RuleSnapshotBuilder.MinimumAcceptedRules}），现有规则未改动。");
        }

        var hadPreviousSnapshot = RuleSnapshotStore.HasSnapshot(_snapshotDirectory);
        try
        {
            RuleSnapshotStore.Save(_snapshotDirectory, result, generatedAt);
        }
        catch (Exception ex)
        {
            _log?.Invoke("rule-update-save-failed:" + ex.GetType().Name);
            return RuleUpdateOutcome.Fail("写入新规则失败，现有规则未改动。");
        }

        var reloaded = RuleSnapshotStore.TryLoad(_snapshotDirectory);
        if (!reloaded.Loaded || reloaded.Engine == null)
        {
            var rolledBack = RuleSnapshotStore.TryRestorePrevious(
                _snapshotDirectory, hadPreviousSnapshot);
            _log?.Invoke("rule-update-verify-failed:rolledBack=" + rolledBack);
            return RuleUpdateOutcome.Fail(rolledBack
                ? "新规则回读校验失败，已回滚到更新前快照。"
                : "新规则回读校验失败，回滚未完成；请检查本地规则目录。");
        }

        // 热替换的引擎必须重新叠加用户本地规则，否则一次成功更新会把 adblock/*.txt
        // 中已生效的规则从当前会话里静默移除。用户规则已在下载前读取，不会在此处再触 IO。
        if (userRules.Count > 0)
        {
            var userAdded = reloaded.Engine.AddRules(userRules);
            _log?.Invoke($"rule-update-user-rules:{userAdded}");
        }

        _provider.Replace(
            reloaded.Engine,
            new AdBlockRuleSnapshotInfo(
                "updated",
                reloaded.Manifest!.Rules.AcceptedRules,
                reloaded.Manifest.Rules.SkippedRules,
                reloaded.Manifest.GeneratedAtUtc,
                _snapshotDirectory));

        _log?.Invoke(
            $"rule-update-ok:accepted={result.AcceptedRules};skipped={result.SkippedRules}");
        return RuleUpdateOutcome.Ok(result.AcceptedRules, result.SkippedRules, generatedAt);
    }
}
