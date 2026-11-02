#nullable enable
using System;

namespace Apage.Core.Models;

/// <summary>
/// 用户手动更新后的规则快照清单（{数据目录}\privacy\rules.json）。
/// 结构与随包清单的 Rules 段一致，便于同一套校验逻辑复用；
/// 只有用户点“立即更新”才会产生，运行时不会自动刷新。
/// </summary>
public sealed class RuleUpdateManifest
{
    public int SchemaVersion { get; set; } = 1;

    public DateTimeOffset GeneratedAtUtc { get; set; }

    /// <summary>规则快照（文件名固定为 rules.txt，SHA-256 必须与文件一致）。</summary>
    public RuleSnapshotAssetManifest Rules { get; set; } = new RuleSnapshotAssetManifest();
}
