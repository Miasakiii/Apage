#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Apage.Core.Services;

/// <summary>
/// ABP 风格广告拦截引擎（docs/product-design.md §5.3，实现移植自 spikes/AdBlockSpike 的已验证方案）。
/// 每条网络规则提取一个 3-8 字符字母数字关键子串作为索引 key，匹配时枚举 URL 子串查表，
/// 只对命中的候选规则做正则校验（50 万条规则实测平均 24µs/URL，见 spikes/results/adblock.md）。
/// 供应链安全：规则视为不可信输入——正则懒编译、统一 MatchTimeout=100ms（防 ReDoS）、单条规则长度上限。
/// 纯逻辑、无 IO，便于单测（决策 #18）。
/// </summary>
public sealed class AdBlockRuleEngine
{
    private const int MinKey = 3;
    private const int MaxKey = 8;

    /// <summary>单条规则长度上限（§5.3 供应链安全）。超长规则直接拒绝。</summary>
    public const int MaxRuleLength = 4096;

    /// <summary>防 ReDoS：所有规则正则的统一超时（§5.3 要求 ≤100ms）。</summary>
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>一条解析后的网络规则（拦截或例外）。</summary>
    private sealed class AdRule
    {
        public string Pattern = string.Empty;   // 参与 URL 匹配的部分（去掉 @@ 前缀与 $ 修饰符）
        public string? Modifiers;               // $ 之后的修饰符（script,domain=...），当前只解析不求值
        public Regex? Matcher;                  // 懒编译：只有成为候选的规则才编译正则
    }

    private readonly Dictionary<string, List<AdRule>> _block = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<AdRule>> _exception = new(StringComparer.Ordinal);
    private readonly List<AdRule> _blockNoKey = new();       // 提取不到子串的规则，退化为逐条校验
    private readonly List<AdRule> _exceptionNoKey = new();
    private readonly List<string> _cosmeticGeneric = new();  // 化妆规则不参与 URL 匹配，按域名分桶（spike 建议 #4）
    private readonly Dictionary<string, List<string>> _cosmeticByDomain = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>已入索引的拦截规则数。</summary>
    public int BlockCount { get; private set; }

    /// <summary>已入索引的例外（@@）规则数。</summary>
    public int ExceptionCount { get; private set; }

    /// <summary>化妆隐藏（##）规则数。</summary>
    public int CosmeticCount { get; private set; }

    /// <summary>匹配中触发 RegexMatchTimeoutException 的累计次数（超时视为不匹配）。</summary>
    public int TimeoutCount { get; private set; }

    // 常见通用 token：作为 key 会聚集大量规则，降权处理（spike 建议 #1）
    private static readonly HashSet<string> CommonTokens = new(StringComparer.Ordinal)
        { "com", "net", "org", "www", "http", "https", "html", "cdn", "js", "css" };

    /// <summary>
    /// 添加一条规则。注释（! 开头）、列表头（[ 开头]）、空行与超长规则返回 false（被忽略）。
    /// </summary>
    public bool AddRule(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        var rule = raw!.Trim();
        if (rule.Length > MaxRuleLength)
            return false; // §5.3 单条规则长度上限
        if (rule[0] == '!' || rule[0] == '[')
            return false; // 注释行 / 列表头（[Adblock Plus 2.0]）

        // 化妆规则（##.ad）：独立存储，不进入网络匹配索引
        int hashHash = rule.IndexOf("##", StringComparison.Ordinal);
        if (hashHash >= 0)
        {
            CosmeticCount++;
            string domain = rule.Substring(0, hashHash);
            string selector = rule.Substring(hashHash + 2);
            if (domain.Length == 0)
                _cosmeticGeneric.Add(selector);
            else
            {
                if (!_cosmeticByDomain.TryGetValue(domain, out var list))
                    _cosmeticByDomain[domain] = list = new List<string>();
                list.Add(selector);
            }
            return true;
        }

        bool isException = rule.StartsWith("@@", StringComparison.Ordinal);
        string body = isException ? rule.Substring(2) : rule;
        if (body.Length == 0)
            return false;

        string pattern = body;
        string? mods = null;
        int dollar = body.IndexOf('$');
        if (dollar >= 0)
        {
            pattern = body.Substring(0, dollar);
            mods = body.Substring(dollar + 1);
        }
        if (pattern.Length == 0)
            return false; // 纯修饰符规则（$popup 等）暂不支持 URL 匹配

        var parsed = new AdRule { Pattern = pattern.ToLowerInvariant(), Modifiers = mods };
        if (isException) ExceptionCount++; else BlockCount++;

        string? key = ExtractKey(parsed.Pattern);
        if (key == null)
        {
            (isException ? _exceptionNoKey : _blockNoKey).Add(parsed);
            return true;
        }
        var dict = isException ? _exception : _block;
        if (!dict.TryGetValue(key, out var rules))
            dict[key] = rules = new List<AdRule>();
        rules.Add(parsed);
        return true;
    }

    /// <summary>批量加载规则列表，返回实际入库条数。</summary>
    public int AddRules(IEnumerable<string> rules)
    {
        if (rules == null)
            throw new ArgumentNullException(nameof(rules));
        int added = 0;
        foreach (var r in rules)
            if (AddRule(r)) added++;
        return added;
    }

    /// <summary>判定 URL 是否应被拦截：先查拦截索引，命中后再查例外（@@）索引豁免。</summary>
    public bool IsBlocked(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        string u = url!.ToLowerInvariant();

        foreach (string key in EnumerateKeys(u))
        {
            if (!_block.TryGetValue(key, out var rules)) continue;
            foreach (var rule in rules)
            {
                if (Match(rule, u))
                    return !IsExcepted(u);
            }
        }
        foreach (var rule in _blockNoKey)
        {
            if (Match(rule, u))
                return !IsExcepted(u);
        }
        return false;
    }

    /// <summary>取某域名生效的化妆隐藏选择器（通用规则 + 域名专属规则）。</summary>
    public IReadOnlyList<string> GetCosmeticSelectors(string? domain)
    {
        var result = new List<string>(_cosmeticGeneric);
        if (!string.IsNullOrWhiteSpace(domain) && _cosmeticByDomain.TryGetValue(domain!.Trim(), out var list))
            result.AddRange(list);
        return result;
    }

    private bool IsExcepted(string u)
    {
        foreach (string key in EnumerateKeys(u))
        {
            if (!_exception.TryGetValue(key, out var rules)) continue;
            foreach (var rule in rules)
                if (Match(rule, u)) return true;
        }
        foreach (var rule in _exceptionNoKey)
            if (Match(rule, u)) return true;
        return false;
    }

    private bool Match(AdRule rule, string url)
    {
        if (rule.Matcher == null)
            rule.Matcher = ToRegex(rule.Pattern);  // 懒编译：禁止预热编译全量规则（spike 建议 #2）
        try
        {
            return rule.Matcher.IsMatch(url);
        }
        catch (RegexMatchTimeoutException)
        {
            // 命中 100ms 超时：视为不匹配，绝不允许卡死浏览器线程
            TimeoutCount++;
            return false;
        }
    }

    /// <summary>
    /// 从规则 pattern 中提取最佳 3-8 字符关键子串：
    /// 扫描字母数字连续段，段内滑窗取评分最高者（越长越好，通用 token 重罚，含数字加分）。
    /// 提取不到返回 null（规则进退化桶）。
    /// </summary>
    public static string? ExtractKey(string pattern)
    {
        if (pattern == null)
            throw new ArgumentNullException(nameof(pattern));

        string? best = null;
        int bestScore = int.MinValue;
        int i = 0;
        while (i < pattern.Length)
        {
            if (!IsAsciiLetterOrDigit(pattern[i])) { i++; continue; }
            int start = i;
            while (i < pattern.Length && IsAsciiLetterOrDigit(pattern[i])) i++;
            int runLen = i - start;
            if (runLen < MinKey) continue;

            int winLen = Math.Min(MaxKey, runLen);
            for (int s = start; s + winLen <= start + runLen; s++)
            {
                string win = pattern.Substring(s, winLen);
                int score = winLen * 10;
                if (CommonTokens.Contains(win)) score -= 1000;
                foreach (char c in win)
                    if (c >= '0' && c <= '9') { score += 3; break; }  // 含数字的子串区分度更高
                if (score > bestScore) { bestScore = score; best = win; }
            }
        }
        return best;
    }

    /// <summary>
    /// ABP pattern → 正则（|| 域名锚定、| 首尾锚定、^ 分隔符、* 通配），统一 MatchTimeout。
    /// 注意 ^ 会消费一个字符（spike 建议 #3 的坑位）。
    /// </summary>
    public static Regex ToRegex(string pattern)
    {
        if (pattern == null)
            throw new ArgumentNullException(nameof(pattern));

        var sb = new StringBuilder(pattern.Length + 16);
        int i = 0;
        if (pattern.StartsWith("||", StringComparison.Ordinal))
        {
            sb.Append(@"^[a-z][a-z0-9+.-]*://([a-z0-9-]+\.)*");
            i = 2;
        }
        else if (pattern.Length > 0 && pattern[0] == '|')
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

    /// <summary>枚举 URL 中全部 3-8 字符的纯字母数字子串（索引 key 只可能是这种形态）。</summary>
    private static IEnumerable<string> EnumerateKeys(string url)
    {
        for (int len = MinKey; len <= MaxKey && len <= url.Length; len++)
        {
            for (int i = 0; i + len <= url.Length; i++)
            {
                bool ok = true;
                for (int j = i; j < i + len; j++)
                {
                    if (!IsAsciiLetterOrDigit(url[j])) { ok = false; break; }
                }
                if (ok) yield return url.Substring(i, len);
            }
        }
    }

    // netstandard2.0 无 char.IsAsciiLetterOrDigit，手写等价判断
    private static bool IsAsciiLetterOrDigit(char c)
        => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
}
