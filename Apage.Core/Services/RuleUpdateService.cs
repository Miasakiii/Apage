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

    public RuleUpdateService(
        string snapshotDirectory,
        AdBlockRuleProvider provider,
        Func<RuleUpdateSource, CancellationToken, Task<string>> download,
        Action<string>? log = null,
        IReadOnlyList<RuleUpdateSource>? sources = null)
    {
        _snapshotDirectory = string.IsNullOrWhiteSpace(snapshotDirectory)
            ? throw new ArgumentException("快照目录不能为空。", nameof(snapshotDirectory))
            : snapshotDirectory;
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _download = download ?? throw new ArgumentNullException(nameof(download));
        _log = log;
        _sources = sources ?? RuleUpdateSources.Default;
    }

    public async Task<RuleUpdateOutcome> UpdateAsync(CancellationToken cancellationToken = default)
    {
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
        if (!reloaded.Loaded)
        {
            _log?.Invoke("rule-update-verify-failed");
            return RuleUpdateOutcome.Fail("新规则回读校验失败，已保留磁盘上的旧快照。");
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
