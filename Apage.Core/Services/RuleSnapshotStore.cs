#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Apage.Core.Models;

namespace Apage.Core.Services;

/// <summary>本地更新快照的加载结果；失败只描述原因，不抛异常（宿主回退到随包快照）。</summary>
public sealed class RuleSnapshotLoadResult
{
    private RuleSnapshotLoadResult(
        AdBlockRuleEngine? engine,
        RuleUpdateManifest? manifest,
        string? failureReason)
    {
        Engine = engine;
        Manifest = manifest;
        FailureReason = failureReason;
    }

    public AdBlockRuleEngine? Engine { get; }

    public RuleUpdateManifest? Manifest { get; }

    public string? FailureReason { get; }

    public bool Loaded => Engine != null;

    internal static RuleSnapshotLoadResult Success(AdBlockRuleEngine engine, RuleUpdateManifest manifest) =>
        new RuleSnapshotLoadResult(engine, manifest, null);

    internal static RuleSnapshotLoadResult Missing() => new RuleSnapshotLoadResult(null, null, null);

    internal static RuleSnapshotLoadResult Failed(string reason) => new RuleSnapshotLoadResult(null, null, reason);
}

/// <summary>
/// 本地规则快照存储（A3）：负责 {目录}\rules.txt + rules.json 的读写与校验。
/// 写入流程固定为「暂存 → 回读校验 → 原子替换 → 失败回滚」：
/// 新快照先写 .tmp 并重新解析验证条数，通过后才替换正式文件；
/// 替换阶段任何失败都从 .bak 恢复旧快照，绝不让应用读到半份规则。
/// </summary>
public static class RuleSnapshotStore
{
    public const string RulesFileName = "rules.txt";
    public const string ManifestFileName = "rules.json";

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>读取并校验本地更新快照。目录/文件不存在返回 Missing，不算失败。</summary>
    public static RuleSnapshotLoadResult TryLoad(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return RuleSnapshotLoadResult.Failed("快照目录为空。");

        var rulesPath = Path.Combine(directory, RulesFileName);
        var manifestPath = Path.Combine(directory, ManifestFileName);
        if (!File.Exists(rulesPath) || !File.Exists(manifestPath))
            return RuleSnapshotLoadResult.Missing();

        try
        {
            var manifest = JsonSerializer.Deserialize<RuleUpdateManifest>(
                File.ReadAllText(manifestPath, Encoding.UTF8), JsonOptions);
            if (manifest == null || manifest.SchemaVersion != 1)
                return RuleSnapshotLoadResult.Failed("快照清单缺失或 schema 不受支持。");
            if (string.IsNullOrWhiteSpace(manifest.Rules.Sha256))
                return RuleSnapshotLoadResult.Failed("快照清单缺少 SHA-256。");

            var bytes = File.ReadAllBytes(rulesPath);
            var actual = RuleSnapshotBuilder.Sha256Hex(bytes);
            if (!string.Equals(actual, manifest.Rules.Sha256, StringComparison.OrdinalIgnoreCase))
                return RuleSnapshotLoadResult.Failed("快照文件 SHA-256 与清单不一致。");

            var engine = BuildEngine(bytes, manifest.Rules.AcceptedRules, out var reason);
            return engine == null
                ? RuleSnapshotLoadResult.Failed(reason ?? "快照规则无法解析。")
                : RuleSnapshotLoadResult.Success(engine, manifest);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
        {
            return RuleSnapshotLoadResult.Failed("快照读取失败：" + ex.GetType().Name);
        }
    }

    /// <summary>
    /// 原子保存新快照并返回落盘清单。校验不通过或 IO 失败会抛异常，且原快照保持不变。
    /// </summary>
    public static RuleUpdateManifest Save(
        string directory,
        RuleSnapshotBuildResult result,
        DateTimeOffset generatedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("快照目录不能为空。", nameof(directory));
        if (result == null)
            throw new ArgumentNullException(nameof(result));
        if (result.AcceptedRules < RuleSnapshotBuilder.MinimumAcceptedRules)
        {
            throw new InvalidOperationException(
                $"新快照只有 {result.AcceptedRules} 条可执行规则，低于安全下限 " +
                $"{RuleSnapshotBuilder.MinimumAcceptedRules}，已拒绝替换。");
        }

        Directory.CreateDirectory(directory);

        var skipReasons = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in result.SkipReasons)
            skipReasons[pair.Key] = pair.Value;

        var manifest = new RuleUpdateManifest
        {
            SchemaVersion = 1,
            GeneratedAtUtc = generatedAtUtc,
            Rules = new RuleSnapshotAssetManifest
            {
                File = RulesFileName,
                Sha256 = result.RulesSha256,
                AcceptedRules = result.AcceptedRules,
                SkippedRules = result.SkippedRules,
                DuplicateRules = result.SkipReasons.TryGetValue("duplicate", out var dup) ? dup : 0,
                SkipReasons = skipReasons,
                Sources = new List<RuleSnapshotSourceManifest>(result.Sources),
            },
        };

        var rulesPath = Path.Combine(directory, RulesFileName);
        var manifestPath = Path.Combine(directory, ManifestFileName);
        var rulesTemp = rulesPath + ".tmp";
        var manifestTemp = manifestPath + ".tmp";
        var rulesBackup = rulesPath + ".bak";
        var manifestBackup = manifestPath + ".bak";

        // 1) 暂存并回读校验：条数必须与构建结果一致，避免磁盘写入截断。
        WriteAllBytesDurable(rulesTemp, result.RulesBytes);
        var verify = BuildEngine(File.ReadAllBytes(rulesTemp), result.AcceptedRules, out var reason);
        if (verify == null)
        {
            TryDelete(rulesTemp);
            throw new InvalidDataException(reason ?? "暂存快照校验失败，已放弃替换。");
        }

        WriteAllTextDurable(manifestTemp, JsonSerializer.Serialize(manifest, JsonOptions));

        // 2) 原子替换：先规则、后清单。清单失败则把规则回滚到旧版本。
        var rulesReplaced = ReplaceOrMove(rulesTemp, rulesPath, rulesBackup);
        try
        {
            ReplaceOrMove(manifestTemp, manifestPath, manifestBackup);
        }
        catch
        {
            if (rulesReplaced && File.Exists(rulesBackup))
            {
                try { File.Copy(rulesBackup, rulesPath, overwrite: true); }
                catch { /* 回滚失败时保留 .bak，用户仍可人工恢复 */ }
            }
            TryDelete(rulesTemp);
            TryDelete(manifestTemp);
            throw;
        }

        return manifest;
    }

    private static bool ReplaceOrMove(string tempPath, string targetPath, string backupPath)
    {
        if (File.Exists(targetPath))
        {
            File.Replace(tempPath, targetPath, backupPath, ignoreMetadataErrors: true);
            return true;
        }

        File.Move(tempPath, targetPath);
        return true;
    }

    private static AdBlockRuleEngine? BuildEngine(byte[] rulesBytes, int expectedAccepted, out string? reason)
    {
        reason = null;
        var engine = new AdBlockRuleEngine();
        using (var stream = new MemoryStream(rulesBytes, writable: false))
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
                engine.AddRule(line);
        }

        if (engine.UnsupportedModifierRuleCount != 0 || engine.CosmeticCount != 0 ||
            engine.BlockCount + engine.ExceptionCount != expectedAccepted)
        {
            reason = $"快照条数不一致：expected={expectedAccepted}, " +
                     $"block={engine.BlockCount}, exception={engine.ExceptionCount}, " +
                     $"cosmetic={engine.CosmeticCount}, unsupported={engine.UnsupportedModifierRuleCount}";
            return null;
        }

        return engine;
    }

    private static void WriteAllBytesDurable(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }

    private static void WriteAllTextDurable(string path, string text)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
        WriteAllBytesDurable(path, bytes);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 清理临时文件失败不影响主流程 */ }
    }
}
