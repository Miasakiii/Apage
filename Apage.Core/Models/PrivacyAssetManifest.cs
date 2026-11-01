#nullable enable
using System;
using System.Collections.Generic;

namespace Apage.Core.Models;

/// <summary>
/// 随包隐私资产的单一清单：PSL 与网络规则快照的来源、日期、SHA-256、许可和筛选统计。
/// 运行时只读；生成由 tools/SnapshotBuilder 完成。
/// </summary>
public sealed class PrivacyAssetManifest
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public PublicSuffixAssetManifest Psl { get; set; } = new PublicSuffixAssetManifest();
    public RuleSnapshotAssetManifest Rules { get; set; } = new RuleSnapshotAssetManifest();
}

public sealed class PublicSuffixAssetManifest
{
    public string File { get; set; } = "public_suffix_list.dat";
    public string Sha256 { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Commit { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = "https://publicsuffix.org/list/public_suffix_list.dat";
    public DateTimeOffset RetrievedAtUtc { get; set; }
    public string License { get; set; } = "MPL-2.0";
    public string LicenseUrl { get; set; } = "https://mozilla.org/MPL/2.0/";
}

public sealed class RuleSnapshotAssetManifest
{
    public string File { get; set; } = "builtin-rules.txt";
    public string Sha256 { get; set; } = string.Empty;
    public int AcceptedRules { get; set; }
    public int SkippedRules { get; set; }
    public int DuplicateRules { get; set; }
    public Dictionary<string, int> SkipReasons { get; set; } = new Dictionary<string, int>();
    public string License { get; set; } = "CC BY-SA 3.0";
    public string LicenseUrl { get; set; } = "https://creativecommons.org/licenses/by-sa/3.0/";
    public string Attribution { get; set; } = "The EasyList authors (https://easylist.to/)";
    public List<RuleSnapshotSourceManifest> Sources { get; set; } = new List<RuleSnapshotSourceManifest>();
}

public sealed class RuleSnapshotSourceManifest
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public DateTimeOffset RetrievedAtUtc { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public int TotalLines { get; set; }
    public int AcceptedRules { get; set; }
    public int SkippedRules { get; set; }
    public string License { get; set; } = "CC BY-SA 3.0";
    public string LicenseUrl { get; set; } = "https://creativecommons.org/licenses/by-sa/3.0/";
}
