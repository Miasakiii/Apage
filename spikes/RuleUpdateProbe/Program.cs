using Apage.Core.Services;
using Apage.Portable.Services;

var root = Path.Combine(Path.GetTempPath(), "ApageRuleUpdateProbe", Guid.NewGuid().ToString("N"));
var privacyDir = Path.Combine(root, "privacy");
var userRuleDir = Path.Combine(root, "adblock");
Directory.CreateDirectory(privacyDir);
Directory.CreateDirectory(userRuleDir);
await File.WriteAllTextAsync(Path.Combine(userRuleDir, "custom.txt"), "||user-rule-probe.example^\n");

try
{
    var provider = new AdBlockRuleProvider(
        new AdBlockRuleEngine(),
        new AdBlockRuleSnapshotInfo("builtin", 0, 0, null, null));
    var client = new RuleUpdateClient();
    var service = new RuleUpdateService(
        privacyDir,
        provider,
        client.DownloadAsync,
        message => Console.WriteLine("[log] " + message),
        userRuleDirectory: userRuleDir);

    Console.WriteLine("== RuleUpdateProbe: 真实 HTTPS 规则更新 ==");
    Console.WriteLine("easyList : https://easylist.to/easylist/easylist.txt");
    Console.WriteLine("easyPrivacy: https://easylist.to/easylist/easyprivacy.txt");

    var started = DateTimeOffset.UtcNow;
    var outcome = await service.UpdateAsync();
    var elapsed = DateTimeOffset.UtcNow - started;

    Console.WriteLine($"success={outcome.Succeeded}");
    Console.WriteLine($"message={outcome.Message}");
    Console.WriteLine($"accepted={outcome.AcceptedRules}; skipped={outcome.SkippedRules}; elapsedMs={(int)elapsed.TotalMilliseconds}");

    var loaded = RuleSnapshotStore.TryLoad(privacyDir);
    var userRuleKept = provider.Current?.IsBlocked("https://user-rule-probe.example/banner.js") == true;
    Console.WriteLine($"diskLoaded={loaded.Loaded}; origin={provider.Info.Origin}; userRuleKept={userRuleKept}");
    if (loaded.Manifest != null)
        Console.WriteLine($"manifestAccepted={loaded.Manifest.Rules.AcceptedRules}; sha256={loaded.Manifest.Rules.Sha256}");

    return outcome.Succeeded && loaded.Loaded && userRuleKept ? 0 : 1;
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}
