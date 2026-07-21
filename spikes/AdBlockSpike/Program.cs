using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

// AdBlockSpike —— 对应产品文档风险 R4：
// 验证 docs/product-design.md §5.3 的「子串索引」方案在 50 万条 ABP 风格规则下的性能。
// 运行方式：dotnet run -c Release --project spikes/AdBlockSpike
// 注意：结论依赖真实实测数据，禁止编造数字。

namespace AdBlockSpike;

/// <summary>一条解析后的网络规则（拦截或例外）。</summary>
internal sealed class AdRule
{
    public required string Raw;        // 原始规则文本
    public required string Pattern;    // 参与 URL 匹配的部分（去掉 @@ 前缀与 $ 修饰符）
    public string? Modifiers;          // $ 之后的修饰符（script,domain=...），本 benchmark 只解析不求值
    public Regex? Matcher;             // 懒编译的校验正则，统一 MatchTimeout=100ms
}

/// <summary>
/// §5.3 子串索引引擎：
/// 每条规则提取一个 3-8 字符的字母数字关键子串作为 Dictionary key；
/// 匹配时枚举 URL 的全部 3-8 字符子串查表，只对命中的候选规则做正则校验，
/// 而不是对 50 万条规则逐条正则。
/// </summary>
internal sealed class SubstringIndexEngine
{
    private const int MinKey = 3;
    private const int MaxKey = 8;

    /// <summary>防 ReDoS：所有规则正则的统一超时（§5.3 要求 ≤100ms）。</summary>
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private readonly Dictionary<string, List<AdRule>> _block = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<AdRule>> _exception = new(StringComparer.Ordinal);
    private readonly List<AdRule> _blockNoKey = new();       // 提取不到子串的规则，退化为逐条校验
    private readonly List<AdRule> _exceptionNoKey = new();
    private readonly List<string> _cosmeticGeneric = new();  // 化妆规则不参与 URL 匹配，仅统计内存
    private readonly Dictionary<string, List<string>> _cosmeticByDomain = new(StringComparer.Ordinal);

    public int BlockCount { get; private set; }
    public int ExceptionCount { get; private set; }
    public int CosmeticCount { get; private set; }
    public int TimeoutCount { get; private set; }            // 匹配中触发 RegexMatchTimeoutException 的次数
    public int DistinctKeys => _block.Count + _exception.Count;
    public int NoKeyCount => _blockNoKey.Count + _exceptionNoKey.Count;

    // 常见通用 token：作为 key 会聚集大量规则，降权处理
    private static readonly HashSet<string> CommonTokens = new(StringComparer.Ordinal)
        { "com", "net", "org", "www", "http", "https", "html", "cdn", "js", "css" };

    public void AddRule(string raw)
    {
        // 化妆规则（##.ad）：独立存储，不进入网络匹配索引
        int hashHash = raw.IndexOf("##", StringComparison.Ordinal);
        if (hashHash >= 0)
        {
            CosmeticCount++;
            string domain = raw[..hashHash];
            string selector = raw[(hashHash + 2)..];
            if (domain.Length == 0) _cosmeticGeneric.Add(selector);
            else
            {
                if (!_cosmeticByDomain.TryGetValue(domain, out var list))
                    _cosmeticByDomain[domain] = list = new List<string>();
                list.Add(selector);
            }
            return;
        }

        bool isException = raw.StartsWith("@@", StringComparison.Ordinal);
        string body = isException ? raw[2..] : raw;

        string pattern = body;
        string? mods = null;
        int dollar = body.IndexOf('$');
        if (dollar >= 0)
        {
            pattern = body[..dollar];
            mods = body[(dollar + 1)..];
        }

        var rule = new AdRule { Raw = raw, Pattern = pattern, Modifiers = mods };
        if (isException) ExceptionCount++; else BlockCount++;

        string? key = ExtractKey(pattern);
        if (key is null)
        {
            (isException ? _exceptionNoKey : _blockNoKey).Add(rule);
            return;
        }
        var dict = isException ? _exception : _block;
        if (!dict.TryGetValue(key, out var rules))
            dict[key] = rules = new List<AdRule>();
        rules.Add(rule);
    }

    /// <summary>
    /// 从规则 pattern 中提取最佳 3-8 字符关键子串：
    /// 扫描字母数字连续段，段内滑窗取评分最高者（越长越好，通用 token 重罚，含数字加分）。
    /// </summary>
    public static string? ExtractKey(string pattern)
    {
        string? best = null;
        int bestScore = int.MinValue;
        int i = 0;
        while (i < pattern.Length)
        {
            if (!char.IsAsciiLetterOrDigit(pattern[i])) { i++; continue; }
            int start = i;
            while (i < pattern.Length && char.IsAsciiLetterOrDigit(pattern[i])) i++;
            int runLen = i - start;
            if (runLen < MinKey) continue;

            int winLen = Math.Min(MaxKey, runLen);
            for (int s = start; s + winLen <= start + runLen; s++)
            {
                string win = pattern.Substring(s, winLen);
                int score = winLen * 10;
                if (CommonTokens.Contains(win)) score -= 1000;
                foreach (char c in win)
                    if (char.IsAsciiDigit(c)) { score += 3; break; }  // 含数字的子串区分度更高
                if (score > bestScore) { bestScore = score; best = win; }
            }
        }
        return best;
    }

    /// <summary>ABP pattern → 正则（|| 域名锚定、^ 分隔符、* 通配），统一 MatchTimeout。</summary>
    public static Regex ToRegex(string pattern)
    {
        var sb = new StringBuilder(pattern.Length + 16);
        int i = 0;
        if (pattern.StartsWith("||", StringComparison.Ordinal))
        {
            sb.Append(@"^[a-z][a-z0-9+.-]*://([a-z0-9-]+\.)*");
            i = 2;
        }
        else if (pattern.StartsWith('|'))
        {
            sb.Append('^');
            i = 1;
        }
        for (; i < pattern.Length; i++)
        {
            char c = pattern[i];
            switch (c)
            {
                case '*': sb.Append(".*"); break;
                case '^': sb.Append("([^a-z0-9_\\-.%]|$)"); break;
                case '|' when i == pattern.Length - 1: sb.Append('$'); break;
                default: sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    }

    /// <summary>判定 URL 是否应被拦截。candidatesChecked 输出实际做了正则校验的规则数。</summary>
    public bool IsBlocked(string url, ref long candidatesChecked)
    {
        string u = url.ToLowerInvariant();

        foreach (string key in EnumerateKeys(u))
        {
            if (!_block.TryGetValue(key, out var rules)) continue;
            foreach (var rule in rules)
            {
                candidatesChecked++;
                if (Match(rule, u))
                    return !IsExcepted(u, ref candidatesChecked);
            }
        }
        foreach (var rule in _blockNoKey)
        {
            candidatesChecked++;
            if (Match(rule, u))
                return !IsExcepted(u, ref candidatesChecked);
        }
        return false;
    }

    private bool IsExcepted(string u, ref long candidatesChecked)
    {
        foreach (string key in EnumerateKeys(u))
        {
            if (!_exception.TryGetValue(key, out var rules)) continue;
            foreach (var rule in rules)
            {
                candidatesChecked++;
                if (Match(rule, u)) return true;
            }
        }
        foreach (var rule in _exceptionNoKey)
        {
            candidatesChecked++;
            if (Match(rule, u)) return true;
        }
        return false;
    }

    private bool Match(AdRule rule, string url)
    {
        rule.Matcher ??= ToRegex(rule.Pattern);  // 懒编译：只有真正成为候选的规则才编译正则
        try
        {
            return rule.Matcher.IsMatch(url);
        }
        catch (RegexMatchTimeoutException)
        {
            // 命中 100ms 超时：视为不匹配，绝不允许卡死
            TimeoutCount++;
            return false;
        }
    }

    /// <summary>枚举 URL 中全部 3-8 字符的纯字母数字子串（key 只可能是这种形态）。</summary>
    private static IEnumerable<string> EnumerateKeys(string url)
    {
        for (int len = MinKey; len <= MaxKey && len <= url.Length; len++)
        {
            for (int i = 0; i + len <= url.Length; i++)
            {
                bool ok = true;
                for (int j = i; j < i + len; j++)
                {
                    if (!char.IsAsciiLetterOrDigit(url[j])) { ok = false; break; }
                }
                if (ok) yield return url.Substring(i, len);
            }
        }
    }
}

/// <summary>合成规则与测试 URL 生成器（固定 seed，可复现）。</summary>
internal static class RuleGenerator
{
    private static readonly string[] Subs = ["ads", "ad", "track", "tracking", "analytics", "banner", "stat", "stats", "adserver", "doubleclick", "pixel", "metrics"];
    private static readonly string[] Tlds = ["com", "net", "io", "xyz", "info"];
    private static readonly string[] Sites = ["news-site", "myblog", "shop24", "video-hub", "forum-x", "game-zone"];
    private static readonly string[] Syl = ["ba", "du", "mi", "ko", "ra", "ze", "ti", "lo", "vi", "nu", "ga", "pe"];

    public static string Domain(Random rng)
    {
        var sb = new StringBuilder();
        int n = 2 + rng.Next(2);
        for (int i = 0; i < n; i++) sb.Append(Syl[rng.Next(Syl.Length)]);
        sb.Append(rng.Next(10, 99999));
        return sb.ToString();
    }

    /// <summary>生成 total 条规则（70% 网络 / 15% 修饰 / 10% 化妆 / 5% 例外），并附带 hitUrlCount 个必命中的测试 URL。</summary>
    public static (List<string> rules, List<string> hitUrls) Generate(int total, int hitUrlCount, int seed)
    {
        var rng = new Random(seed);
        var rules = new List<string>(total);
        var hitUrls = new List<string>(hitUrlCount);

        int network = (int)(total * 0.70);
        int modifier = (int)(total * 0.15);
        int cosmetic = (int)(total * 0.10);
        int exception = total - network - modifier - cosmetic;

        // —— 70% 网络拦截规则 ——
        for (int i = 0; i < network; i++)
        {
            string sub = Subs[rng.Next(Subs.Length)];
            string d = Domain(rng);
            string tld = Tlds[rng.Next(Tlds.Length)];
            string rule, hit;
            switch (rng.Next(4))
            {
                case 0:
                    rule = $"||{sub}.{d}.{tld}^";
                    hit = $"https://{sub}.{d}.{tld}/static/ad{rng.Next(100)}.js";
                    break;
                case 1:
                    // 带路径的规则：ABP 习惯写法是域名后直接跟路径（^ 会消费掉路径的首个 /）
                    rule = $"||{sub}.{d}.{tld}/banners/*";
                    hit = $"https://{sub}.{d}.{tld}/banners/leaderboard{rng.Next(100)}.gif";
                    break;
                case 2:
                    rule = $"{d}.{tld}/ads/";
                    hit = $"https://www.{d}.{tld}/ads/pop{rng.Next(100)}.html";
                    break;
                default:
                    rule = $"||{d}.{tld}/adserver^";
                    hit = $"https://{d}.{tld}/adserver?zone={rng.Next(1000)}";
                    break;
            }
            rules.Add(rule);
            if (hitUrls.Count < hitUrlCount) hitUrls.Add(hit);
        }

        // —— 15% 带 $ 修饰符的规则 ——
        for (int i = 0; i < modifier; i++)
        {
            string sub = Subs[rng.Next(Subs.Length)];
            string d = Domain(rng);
            string tld = Tlds[rng.Next(Tlds.Length)];
            string site = Sites[rng.Next(Sites.Length)];
            string mod = rng.Next(6) switch
            {
                0 => "script",
                1 => "image",
                2 => "script,third-party",
                3 => $"script,domain={site}.com",
                4 => $"image,domain=~{site}.com",
                _ => "stylesheet,third-party",
            };
            rules.Add($"||{sub}.{d}.{tld}^${mod}");
        }

        // —— 10% 化妆隐藏规则 ——
        for (int i = 0; i < cosmetic; i++)
        {
            rules.Add(rng.Next(4) switch
            {
                0 => $"##.ad-{i}",
                1 => $"##div[class*=\"sponsor-{i}\"]",
                2 => $"{Sites[rng.Next(Sites.Length)]}.com##.banner-{i}",
                _ => "##.ad.banner.adsbox",
            });
        }

        // —— 5% 例外规则 ——
        for (int i = 0; i < exception; i++)
        {
            string sub = Subs[rng.Next(Subs.Length)];
            string d = Domain(rng);
            string tld = Tlds[rng.Next(Tlds.Length)];
            rules.Add(rng.Next(2) == 0
                ? $"@@||{sub}.{d}.{tld}^"
                : $"@@||{d}.{tld}/whitelist.js$script");
        }

        return (rules, hitUrls);
    }

    /// <summary>生成 count 个不应命中的良性 URL（域名与路径用词均避开拦截词）。</summary>
    public static List<string> BenignUrls(int count, int seed)
    {
        var rng = new Random(seed);
        string[] names = ["dailynews", "techblog", "travelnotes", "cookingfan", "photostream", "localforum", "bookclub", "gardenlife"];
        string[] paths = ["articles", "posts", "guide", "review", "about", "archive", "topics", "stories"];
        string[] tlds = ["org", "dev", "app"];
        var urls = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            string host = $"{names[rng.Next(names.Length)]}{rng.Next(1000)}";
            urls.Add($"https://www.{host}.{tlds[rng.Next(tlds.Length)]}/{paths[rng.Next(paths.Length)]}/item-{rng.Next(100000)}");
        }
        return urls;
    }
}

internal static class Program
{
    private const int RuleTotal = 500_000;
    private const int UrlTotal = 10_000;

    private static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== AdBlockSpike：50 万条 ABP 规则子串索引 benchmark（R4） ===");
        Console.WriteLine($"运行时: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");

        // 1) 生成规则与测试 URL（生成耗时/内存不计入引擎指标）
        var (rules, hitUrls) = RuleGenerator.Generate(RuleTotal, UrlTotal / 2, seed: 42);
        var benign = RuleGenerator.BenignUrls(UrlTotal / 2, seed: 43);
        var testUrls = new List<string>(UrlTotal);
        testUrls.AddRange(hitUrls);
        testUrls.AddRange(benign);
        var shuffleRng = new Random(44);
        for (int i = testUrls.Count - 1; i > 0; i--)
        {
            int j = shuffleRng.Next(i + 1);
            (testUrls[i], testUrls[j]) = (testUrls[j], testUrls[i]);
        }
        Console.WriteLine($"规则生成: {rules.Count:N0} 条; 测试 URL: {testUrls.Count:N0} 个（其中 {hitUrls.Count:N0} 个应命中）");

        // 2) 构建索引：测耗时与内存
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long managedBefore = GC.GetTotalMemory(true);
        long wsBefore = Environment.WorkingSet;

        var engine = new SubstringIndexEngine();
        var swBuild = Stopwatch.StartNew();
        foreach (var r in rules) engine.AddRule(r);
        swBuild.Stop();

        long managedAfter = GC.GetTotalMemory(true);
        long wsAfter = Environment.WorkingSet;

        Console.WriteLine();
        Console.WriteLine("--- 索引构建 ---");
        Console.WriteLine($"耗时:            {swBuild.Elapsed.TotalMilliseconds:N1} ms");
        Console.WriteLine($"托管堆增量:      {(managedAfter - managedBefore) / 1048576.0:N1} MB（索引结构+AdRule 对象，不含原始规则字符串）");
        Console.WriteLine($"进程工作集增量:  {(wsAfter - wsBefore) / 1048576.0:N1} MB");
        Console.WriteLine($"索引 key 数:     {engine.DistinctKeys:N0}");
        Console.WriteLine($"无 key 退化规则: {engine.NoKeyCount:N0}");
        Console.WriteLine($"分类: 拦截 {engine.BlockCount:N0} / 例外 {engine.ExceptionCount:N0} / 化妆 {engine.CosmeticCount:N0}");

        // 3) 热身轮：触发 JIT 与候选规则的正则懒编译（耗时单独记录，不进正式统计）
        long cand = 0;
        int hitsWarm = 0;
        var swWarm = Stopwatch.StartNew();
        foreach (var url in testUrls)
            if (engine.IsBlocked(url, ref cand)) hitsWarm++;
        swWarm.Stop();
        Console.WriteLine();
        Console.WriteLine("--- 热身轮（含懒编译，不计入正式数据） ---");
        Console.WriteLine($"总耗时: {swWarm.Elapsed.TotalMilliseconds:N1} ms; 候选校验 {cand:N0} 次; 命中 {hitsWarm:N0}/{testUrls.Count:N0}");

        // 4) 正式测量：逐 URL 计时
        var ticks = new long[testUrls.Count];
        cand = 0;
        int hits = 0;
        for (int i = 0; i < testUrls.Count; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            bool blocked = engine.IsBlocked(testUrls[i], ref cand);
            ticks[i] = Stopwatch.GetTimestamp() - t0;
            if (blocked) hits++;
        }

        double TickToUs(long t) => t * 1_000_000.0 / Stopwatch.Frequency;
        var sorted = (long[])ticks.Clone();
        Array.Sort(sorted);
        double avg = 0;
        foreach (var t in ticks) avg += TickToUs(t);
        avg /= ticks.Length;
        double P(double p) => TickToUs(sorted[Math.Min((int)(p * sorted.Length), sorted.Length - 1)]);

        Console.WriteLine();
        Console.WriteLine("--- 单 URL 匹配耗时（微秒, 稳态） ---");
        Console.WriteLine($"平均:  {avg:N2} us");
        Console.WriteLine($"P50:   {P(0.50):N2} us");
        Console.WriteLine($"P95:   {P(0.95):N2} us");
        Console.WriteLine($"P99:   {P(0.99):N2} us");
        Console.WriteLine($"最大:  {TickToUs(sorted[^1]):N2} us");
        Console.WriteLine($"命中率: {hits}/{testUrls.Count} = {hits * 100.0 / testUrls.Count:N1}%");
        Console.WriteLine($"平均每 URL 候选校验: {cand / (double)testUrls.Count:N2} 次");
        Console.WriteLine($"匹配期 RegexMatchTimeoutException: {engine.TimeoutCount} 次");

        // 5) 基线对照：逐条正则扫描（抽样 20 URL × 5000 规则，外推到全量）
        var sampleRules = rules.Where(r => !r.Contains("##") && !r.StartsWith("@@")).Take(5000)
            .Select(r =>
            {
                string p = r;
                int d = p.IndexOf('$');
                if (d >= 0) p = p[..d];
                return SubstringIndexEngine.ToRegex(p);
            }).ToArray();
        var sampleUrls = testUrls.Take(20).ToArray();
        long tBase = Stopwatch.GetTimestamp();
        int baseHits = 0;
        foreach (var u in sampleUrls)
            foreach (var re in sampleRules)
                if (re.IsMatch(u)) { baseHits++; break; }
        double basePerUrlMs = (Stopwatch.GetTimestamp() - tBase) * 1000.0 / Stopwatch.Frequency / sampleUrls.Length;
        Console.WriteLine();
        Console.WriteLine("--- 基线：逐条正则扫描（抽样外推） ---");
        Console.WriteLine($"抽样: 20 URL × {sampleRules.Length:N0} 规则, 平均 {basePerUrlMs:N2} ms/URL");
        Console.WriteLine($"外推 50 万条: 约 {basePerUrlMs * 100:N0} ms/URL（×100 规则数）");

        // 6) 防 ReDoS 验证：灾难性回溯规则必须在 100ms 超时内抛异常并被捕获
        Console.WriteLine();
        Console.WriteLine("--- 防 ReDoS 验证 ---");
        var evil = new Regex("(a+)+$", RegexOptions.None, SubstringIndexEngine.RegexTimeout);
        string redosInput = new string('a', 45) + "!";   // 非匹配输入，无超时时将指数级回溯
        bool caught = false;
        long tStart = Stopwatch.GetTimestamp();
        try { evil.IsMatch(redosInput); }
        catch (RegexMatchTimeoutException) { caught = true; }
        double redosMs = (Stopwatch.GetTimestamp() - tStart) * 1000.0 / Stopwatch.Frequency;
        Console.WriteLine($"规则 (a+)+$ 对 45×'a'+\"!\" 匹配: 捕获 RegexMatchTimeoutException = {caught}, 耗时 {redosMs:N1} ms（超时上限 100ms）");
        Console.WriteLine(caught ? "=> 灾难性回溯不会卡死浏览器线程，§5.3 防护有效" : "=> 警告：未触发超时！");

        Console.WriteLine();
        Console.WriteLine("=== benchmark 结束 ===");
    }
}
