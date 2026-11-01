#nullable enable
using System;
using System.Threading;

namespace Apage.Core.Services;

/// <summary>当前生效规则集的来源信息（设置面板展示与诊断用）。</summary>
public sealed class AdBlockRuleSnapshotInfo
{
    public AdBlockRuleSnapshotInfo(
        string origin,
        int acceptedRules,
        int skippedRules,
        DateTimeOffset? generatedAtUtc,
        string? path)
    {
        Origin = origin;
        AcceptedRules = acceptedRules;
        SkippedRules = skippedRules;
        GeneratedAtUtc = generatedAtUtc;
        Path = path;
    }

    /// <summary>builtin = 随包快照；updated = 用户手动更新后的本地快照。</summary>
    public string Origin { get; }

    public int AcceptedRules { get; }

    public int SkippedRules { get; }

    public DateTimeOffset? GeneratedAtUtc { get; }

    public string? Path { get; }

    public bool IsBuiltin => string.Equals(Origin, "builtin", StringComparison.Ordinal);
}

/// <summary>
/// 规则引擎的共享持有者（A3）：每个请求读取 <see cref="Current"/>，
/// 手动更新在后台构建新引擎后一次性替换引用，正在进行的请求继续用旧引擎，
/// 新请求立即用新规则——不重建标签、不中断浏览。
/// </summary>
public sealed class AdBlockRuleProvider
{
    private AdBlockRuleEngine? _current;
    private AdBlockRuleSnapshotInfo _info;

    public AdBlockRuleProvider(AdBlockRuleEngine? initial, AdBlockRuleSnapshotInfo info)
    {
        _current = initial;
        _info = info ?? throw new ArgumentNullException(nameof(info));
    }

    /// <summary>当前引擎；无可用规则时为 null（调用方据此跳过拦截）。</summary>
    public AdBlockRuleEngine? Current => Volatile.Read(ref _current);

    public AdBlockRuleSnapshotInfo Info => Volatile.Read(ref _info);

    /// <summary>替换规则集（允许替换为 null 表示禁用拦截）。</summary>
    public void Replace(AdBlockRuleEngine? engine, AdBlockRuleSnapshotInfo info)
    {
        if (info == null)
            throw new ArgumentNullException(nameof(info));

        Volatile.Write(ref _current, engine);
        Volatile.Write(ref _info, info);
    }
}
