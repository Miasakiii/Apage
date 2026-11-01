using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Apage.Core.Models;
using Apage.Core.Services;

var sourceDir = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/snapshot-source");
var outputDir = Path.GetFullPath(args.Length > 1 ? args[1] : "Apage.Core/Resources/Privacy");
Directory.CreateDirectory(outputDir);

var pslPath = Path.Combine(sourceDir, "public_suffix_list.dat");
if (!File.Exists(pslPath))
    throw new FileNotFoundException("PSL source not found.", pslPath);

var pslBytes = File.ReadAllBytes(pslPath);
var pslText = Encoding.UTF8.GetString(pslBytes);
var version = Regex.Match(pslText, @"^// VERSION:\s*(.+)$", RegexOptions.Multiline).Groups[1].Value.Trim();
var commit = Regex.Match(pslText, @"^// COMMIT:\s*(.+)$", RegexOptions.Multiline).Groups[1].Value.Trim();

var outputPslPath = Path.Combine(outputDir, "public_suffix_list.dat");
File.WriteAllBytes(outputPslPath, pslBytes);

var sources = new[]
{
    new SourceSpec("EasyList", "easylist.txt", "https://easylist.to/easylist/easylist.txt"),
    new SourceSpec("EasyPrivacy", "easyprivacy.txt", "https://easylist.to/easylist/easyprivacy.txt")
};

var seen = new HashSet<string>(StringComparer.Ordinal);
var uniqueRules = new List<string>();
var skipTotals = new Dictionary<string, int>(StringComparer.Ordinal)
{
    ["empty"] = 0,
    ["comment"] = 0,
    ["tooLong"] = 0,
    ["cosmetic"] = 0,
    ["cosmeticUnsupported"] = 0,
    ["unsupportedModifier"] = 0,
    ["unsupportedRegex"] = 0,
    ["invalid"] = 0,
    ["duplicate"] = 0
};
var sourceManifests = new List<RuleSnapshotSourceManifest>();

foreach (var source in sources)
{
    var path = Path.Combine(sourceDir, source.FileName);
    if (!File.Exists(path))
        throw new FileNotFoundException($"Rule source not found: {source.Name}", path);

    var bytes = File.ReadAllBytes(path);
    var lines = File.ReadLines(path, Encoding.UTF8).ToArray();
    var accepted = 0;
    var skipped = 0;
    foreach (var raw in lines)
    {
        var reason = AdBlockRuleEngine.Classify(raw) switch
        {
            AdBlockRuleKind.Empty => "empty",
            AdBlockRuleKind.Comment => "comment",
            AdBlockRuleKind.TooLong => "tooLong",
            AdBlockRuleKind.Cosmetic => "cosmetic",
            AdBlockRuleKind.CosmeticUnsupported => "cosmeticUnsupported",
            AdBlockRuleKind.UnsupportedModifier => "unsupportedModifier",
            AdBlockRuleKind.UnsupportedRegex => "unsupportedRegex",
            AdBlockRuleKind.Invalid => "invalid",
            _ => null
        };

        var rule = raw.Trim();
        if (reason == null && seen.Contains(rule))
            reason = "duplicate";

        if (reason != null)
        {
            skipTotals[reason]++;
            skipped++;
            continue;
        }

        seen.Add(rule);
        uniqueRules.Add(rule);
        accepted++;
    }

    sourceManifests.Add(new RuleSnapshotSourceManifest
    {
        Name = source.Name,
        Url = source.Url,
        RetrievedAtUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero),
        Sha256 = Sha256Hex(bytes),
        TotalLines = lines.Length,
        AcceptedRules = accepted,
        SkippedRules = skipped,
        License = "CC BY-SA 3.0",
        LicenseUrl = "https://creativecommons.org/licenses/by-sa/3.0/"
    });

    Console.WriteLine($"{source.Name}: lines={lines.Length}, accepted={accepted}, skipped={skipped}");
}

uniqueRules.Sort(StringComparer.Ordinal);
var rulesBuilder = new StringBuilder(uniqueRules.Sum(r => r.Length + 1));
foreach (var rule in uniqueRules)
    rulesBuilder.Append(rule).Append('\n');

var rulesBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(rulesBuilder.ToString());
var outputRulesPath = Path.Combine(outputDir, "builtin-rules.txt");
File.WriteAllBytes(outputRulesPath, rulesBytes);

var skippedTotal = skipTotals.Values.Sum();
if (uniqueRules.Count + skippedTotal != sourceManifests.Sum(s => s.TotalLines))
    throw new InvalidOperationException("Rule accounting mismatch: accepted + skipped != total lines.");

var manifest = new PrivacyAssetManifest
{
    SchemaVersion = 1,
    GeneratedAtUtc = DateTimeOffset.UtcNow,
    Psl = new PublicSuffixAssetManifest
    {
        File = "public_suffix_list.dat",
        Sha256 = Sha256Hex(pslBytes),
        Version = version,
        Commit = commit,
        SourceUrl = "https://publicsuffix.org/list/public_suffix_list.dat",
        RetrievedAtUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(pslPath), TimeSpan.Zero),
        License = "MPL-2.0",
        LicenseUrl = "https://mozilla.org/MPL/2.0/"
    },
    Rules = new RuleSnapshotAssetManifest
    {
        File = "builtin-rules.txt",
        Sha256 = Sha256Hex(rulesBytes),
        AcceptedRules = uniqueRules.Count,
        SkippedRules = skippedTotal,
        DuplicateRules = skipTotals["duplicate"],
        SkipReasons = skipTotals,
        License = "CC BY-SA 3.0",
        LicenseUrl = "https://creativecommons.org/licenses/by-sa/3.0/",
        Attribution = "The EasyList authors (https://easylist.to/)",
        Sources = sourceManifests
    }
};

var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
});
File.WriteAllText(
    Path.Combine(outputDir, "privacy-assets.json"),
    json + "\n",
    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

Console.WriteLine(
    $"Snapshot: accepted={manifest.Rules.AcceptedRules}, skipped={manifest.Rules.SkippedRules}, " +
    $"bytes={rulesBytes.Length}, psl={pslBytes.Length}, version={version}");
foreach (var reason in skipTotals.OrderBy(kv => kv.Key, StringComparer.Ordinal))
    Console.WriteLine($"  skip {reason.Key}: {reason.Value}");

static string Sha256Hex(byte[] bytes)
{
    using var sha = SHA256.Create();
    return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
}

internal sealed record SourceSpec(string Name, string FileName, string Url);
