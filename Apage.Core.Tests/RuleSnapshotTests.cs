using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Apage.Core.Services;
using Xunit;

namespace Apage.Core.Tests;

/// <summary>
/// A3 规则快照：共用筛选口径、原子存储与回滚、热替换 provider、手动更新编排。
/// </summary>
public sealed class RuleSnapshotTests : IDisposable
{
    private readonly string _dir;

    public RuleSnapshotTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ApageTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* 清理失败不影响测试结论 */ }
    }

    // ===== 构建器 =====

    [Fact]
    public void Build_FiltersUnsupportedAndDeduplicatesAcrossSources()
    {
        var sourceA = new RuleTextSource("A", "https://a.example/list.txt", string.Join("\n", new[]
        {
            "! comment",
            "",
            "||ads.example.com^",
            "||ads.example.com^",           // 同源重复
            "example.com##.ad-banner",       // 化妆规则
            "||x.example.com^$third-party",  // 修饰符
            "@@||ok.example.com^",
        }));
        var sourceB = new RuleTextSource("B", "https://b.example/list.txt", "||ads.example.com^\n||b.example.net^");

        var result = RuleSnapshotBuilder.Build(new[] { sourceA, sourceB }, DateTimeOffset.UtcNow);

        Assert.Equal(3, result.AcceptedRules); // ads(A) + exception(A) + b
        Assert.Equal(2, result.SkipReasons["duplicate"]); // A 内部重复 + B 与 A 重复
        Assert.Equal(1, result.SkipReasons["cosmetic"]);
        Assert.Equal(1, result.SkipReasons["unsupportedModifier"]);
        Assert.Equal(1, result.SkipReasons["comment"]);
        Assert.Equal(2, result.Sources.Count);
        Assert.Equal(2, result.Sources[0].AcceptedRules);
        Assert.Equal(1, result.Sources[1].AcceptedRules);
        Assert.Equal(64, result.RulesSha256.Length);
    }

    [Fact]
    public void Build_EmptySource_ProducesEmptySnapshot()
    {
        var result = RuleSnapshotBuilder.Build(
            new[] { new RuleTextSource("empty", "https://e.example", string.Empty) },
            DateTimeOffset.UtcNow);

        Assert.True(result.IsEmpty);
        Assert.Empty(result.RulesBytes);
        Assert.Equal(0, result.Sources[0].TotalLines);
    }

    // ===== 存储 =====

    [Fact]
    public void Store_SaveThenLoad_RoundTripsManifestAndEngine()
    {
        var result = BuildSnapshot(1200);
        var generatedAt = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

        var manifest = RuleSnapshotStore.Save(_dir, result, generatedAt);
        var loaded = RuleSnapshotStore.TryLoad(_dir);

        Assert.True(loaded.Loaded);
        Assert.Equal(result.AcceptedRules, loaded.Manifest!.Rules.AcceptedRules);
        Assert.Equal(result.RulesSha256, manifest.Rules.Sha256);
        Assert.Equal(generatedAt, loaded.Manifest.GeneratedAtUtc);
        Assert.NotNull(loaded.Engine);
        Assert.True(loaded.Engine!.BlockCount >= 1200);
        Assert.False(File.Exists(Path.Combine(_dir, RuleSnapshotStore.RulesFileName + ".tmp")));
    }

    [Fact]
    public void Store_RejectsSnapshotBelowSafetyFloor()
    {
        var small = RuleSnapshotBuilder.Build(
            new[] { new RuleTextSource("tiny", "https://t.example", "||a.example^\n||b.example^") },
            DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() =>
            RuleSnapshotStore.Save(_dir, small, DateTimeOffset.UtcNow));
        Assert.False(File.Exists(Path.Combine(_dir, RuleSnapshotStore.RulesFileName)));
    }

    [Fact]
    public void Store_CorruptedFile_IsReportedAndPreviousSnapshotKept()
    {
        RuleSnapshotStore.Save(_dir, BuildSnapshot(1200), DateTimeOffset.UtcNow);
        var rulesPath = Path.Combine(_dir, RuleSnapshotStore.RulesFileName);
        var original = File.ReadAllBytes(rulesPath);

        // 只改内容不改清单：SHA-256 校验必须失败，且不删除文件
        File.WriteAllText(rulesPath, "||tampered.example^\n", Encoding.UTF8);
        var loaded = RuleSnapshotStore.TryLoad(_dir);

        Assert.False(loaded.Loaded);
        Assert.Contains("SHA-256", loaded.FailureReason);
        Assert.True(File.Exists(rulesPath));

        File.WriteAllBytes(rulesPath, original);
        Assert.True(RuleSnapshotStore.TryLoad(_dir).Loaded);
    }

    [Fact]
    public void Store_MissingFiles_ReportsMissingNotFailure()
    {
        var loaded = RuleSnapshotStore.TryLoad(_dir);

        Assert.False(loaded.Loaded);
        Assert.Null(loaded.FailureReason);
    }

    [Fact]
    public void Store_SecondSave_ReplacesAndKeepsBackup()
    {
        RuleSnapshotStore.Save(_dir, BuildSnapshot(1100, "first"), DateTimeOffset.UtcNow);
        var firstHash = RuleSnapshotStore.TryLoad(_dir).Manifest!.Rules.Sha256;

        RuleSnapshotStore.Save(_dir, BuildSnapshot(1300, "second"), DateTimeOffset.UtcNow);
        var second = RuleSnapshotStore.TryLoad(_dir);

        Assert.True(second.Loaded);
        Assert.NotEqual(firstHash, second.Manifest!.Rules.Sha256);
        Assert.Equal(1300, second.Manifest.Rules.AcceptedRules);
        Assert.True(File.Exists(Path.Combine(_dir, RuleSnapshotStore.RulesFileName + ".bak")));
    }

    [Fact]
    public void Store_RestorePrevious_RollsBackWholePair()
    {
        RuleSnapshotStore.Save(_dir, BuildSnapshot(1100, "first"), DateTimeOffset.UtcNow);
        RuleSnapshotStore.Save(_dir, BuildSnapshot(1300, "second"), DateTimeOffset.UtcNow);

        Assert.True(RuleSnapshotStore.TryRestorePrevious(_dir, hadPrevious: true));
        var restored = RuleSnapshotStore.TryLoad(_dir);

        Assert.True(restored.Loaded);
        Assert.Equal(1100, restored.Manifest!.Rules.AcceptedRules);
    }

    [Fact]
    public void Store_RestorePrevious_WithNoPriorSnapshot_RemovesNewPair()
    {
        RuleSnapshotStore.Save(_dir, BuildSnapshot(1200), DateTimeOffset.UtcNow);

        Assert.True(RuleSnapshotStore.TryRestorePrevious(_dir, hadPrevious: false));
        Assert.False(RuleSnapshotStore.HasSnapshot(_dir));
    }

    // ===== provider =====

    [Fact]
    public void Provider_ReplaceSwapsEngineAndInfo()
    {
        var provider = new AdBlockRuleProvider(null, new AdBlockRuleSnapshotInfo("builtin", 0, 0, null, null));
        Assert.Null(provider.Current);

        var engine = new AdBlockRuleEngine();
        engine.AddRule("||ads.example.com^");
        provider.Replace(engine, new AdBlockRuleSnapshotInfo("updated", 1, 0, DateTimeOffset.UtcNow, _dir));

        Assert.Same(engine, provider.Current);
        Assert.Equal("updated", provider.Info.Origin);
        Assert.False(provider.Info.IsBuiltin);
    }

    // ===== 更新编排 =====

    [Fact]
    public async Task Update_Success_ReplacesProviderAndPersistsSnapshot()
    {
        var provider = new AdBlockRuleProvider(
            new AdBlockRuleEngine(),
            new AdBlockRuleSnapshotInfo("builtin", 0, 0, null, null));
        var service = BuildService(provider, (source, ct) => Task.FromResult(RuleText(1200, source.Name)));

        var outcome = await service.UpdateAsync();

        Assert.True(outcome.Succeeded);
        Assert.Equal(1200, outcome.AcceptedRules);
        Assert.Equal("updated", provider.Info.Origin);
        Assert.True(provider.Current!.BlockCount >= 1200);
        Assert.True(RuleSnapshotStore.TryLoad(Path.Combine(_dir, "privacy")).Loaded);
    }

    [Fact]
    public async Task Update_DownloadFailure_LeavesProviderAndDiskUntouched()
    {
        var original = new AdBlockRuleEngine();
        original.AddRule("||keep.example.com^");
        var provider = new AdBlockRuleProvider(
            original,
            new AdBlockRuleSnapshotInfo("builtin", 1, 0, null, null));
        var service = BuildService(provider,
            (source, ct) => throw new InvalidOperationException("network down"));

        var outcome = await service.UpdateAsync();

        Assert.False(outcome.Succeeded);
        Assert.Same(original, provider.Current);
        Assert.Equal("builtin", provider.Info.Origin);
        Assert.False(File.Exists(Path.Combine(_dir, "privacy", RuleSnapshotStore.RulesFileName)));
    }

    [Fact]
    public async Task Update_TooFewRules_IsRejectedWithoutReplacing()
    {
        var original = new AdBlockRuleEngine();
        original.AddRule("||keep.example.com^");
        var provider = new AdBlockRuleProvider(
            original,
            new AdBlockRuleSnapshotInfo("builtin", 1, 0, null, null));
        var service = BuildService(provider, (source, ct) => Task.FromResult("||only.example^\n"));

        var outcome = await service.UpdateAsync();

        Assert.False(outcome.Succeeded);
        Assert.Contains("安全下限", outcome.Message);
        Assert.Same(original, provider.Current);
    }

    [Fact]
    public async Task Update_EmptyResponse_IsRejected()
    {
        var provider = new AdBlockRuleProvider(null, new AdBlockRuleSnapshotInfo("builtin", 0, 0, null, null));
        var service = BuildService(provider, (source, ct) => Task.FromResult("   "));

        var outcome = await service.UpdateAsync();

        Assert.False(outcome.Succeeded);
        Assert.Null(provider.Current);
    }

    [Fact]
    public async Task Update_Success_PreservesUserRules()
    {
        var userRuleDirectory = Path.Combine(_dir, "adblock");
        Directory.CreateDirectory(userRuleDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(userRuleDirectory, "custom.txt"),
            "||custom-ads.example^\n");

        var provider = new AdBlockRuleProvider(
            new AdBlockRuleEngine(),
            new AdBlockRuleSnapshotInfo("builtin", 0, 0, null, null));
        var service = BuildService(
            provider,
            (source, ct) => Task.FromResult(RuleText(1200, source.Name)),
            userRuleDirectory);

        var outcome = await service.UpdateAsync();

        Assert.True(outcome.Succeeded);
        Assert.True(provider.Current!.IsBlocked("https://custom-ads.example/banner.js"));
    }

    [Fact]
    public async Task Update_UnreadableUserRules_FailsBeforeReplacingProvider()
    {
        var userRuleDirectory = Path.Combine(_dir, "adblock");
        Directory.CreateDirectory(userRuleDirectory);
        var rulePath = Path.Combine(userRuleDirectory, "custom.txt");
        await File.WriteAllTextAsync(rulePath, "||custom.example^\n");

        var original = new AdBlockRuleEngine();
        original.AddRule("||keep.example^");
        var provider = new AdBlockRuleProvider(
            original,
            new AdBlockRuleSnapshotInfo("builtin", 1, 0, null, null));
        var downloadCalled = false;
        var service = BuildService(provider, (source, ct) =>
        {
            downloadCalled = true;
            return Task.FromResult(RuleText(1200, source.Name));
        }, userRuleDirectory);

        RuleUpdateOutcome outcome;
        using (File.Open(rulePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            outcome = await service.UpdateAsync();
        }

        Assert.False(outcome.Succeeded);
        Assert.False(downloadCalled);
        Assert.Same(original, provider.Current);
    }

    [Fact]
    public async Task Update_ConcurrentCalls_AreSerializedEndToEnd()
    {
        var provider = new AdBlockRuleProvider(
            new AdBlockRuleEngine(),
            new AdBlockRuleSnapshotInfo("builtin", 0, 0, null, null));
        var concurrent = 0;
        var maxConcurrent = 0;
        var service = BuildService(provider, async (source, ct) =>
        {
            var current = Interlocked.Increment(ref concurrent);
            InterlockedExtensions.Max(ref maxConcurrent, current);
            await Task.Delay(30, ct);
            Interlocked.Decrement(ref concurrent);
            return RuleText(1200, source.Name);
        });

        var results = await Task.WhenAll(service.UpdateAsync(), service.UpdateAsync());

        Assert.All(results, x => Assert.True(x.Succeeded));
        Assert.Equal(1, maxConcurrent);
    }

    private RuleUpdateService BuildService(
        AdBlockRuleProvider provider,
        Func<RuleUpdateSource, CancellationToken, Task<string>> download,
        string? userRuleDirectory = null)
    {
        // 单来源，避免默认双列表把同一份假数据算两次
        var sources = new[] { new RuleUpdateSource("Test", "https://test.example/list.txt") };
        return new RuleUpdateService(
            Path.Combine(_dir, "privacy"), provider, download, null, sources, userRuleDirectory);
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int target, int value)
        {
            var current = Volatile.Read(ref target);
            while (value > current)
            {
                var seen = Interlocked.CompareExchange(ref target, value, current);
                if (seen == current)
                    return;
                current = seen;
            }
        }
    }

    private static string RuleText(int count, string prefix = "r")
    {
        var builder = new StringBuilder(count * 32);
        for (var i = 0; i < count; i++)
            builder.Append("||").Append(prefix).Append("-ads-").Append(i).Append(".example.com^\n");
        return builder.ToString();
    }

    private static RuleSnapshotBuildResult BuildSnapshot(int count, string prefix = "r")
    {
        return RuleSnapshotBuilder.Build(
            new[] { new RuleTextSource("synthetic", "https://synthetic.example", RuleText(count, prefix)) },
            DateTimeOffset.UtcNow);
    }
}
