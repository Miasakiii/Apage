#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Apage.Core.Services.ScriptManager;

/// <summary>
/// 用户脚本 URL 匹配引擎（docs/script-manager.md §4）。
/// @match / @exclude_match 用 Chrome match pattern 语法；@include / @exclude 用 TM 扩展语法
/// （/正则/ 或 * 通配符）。全部模式在构造时预编译为正则并统一 MatchTimeout=100ms（防 ReDoS，
/// 与广告拦截引擎一致）；非法模式静默丢弃（视为永不匹配）。纯逻辑，便于单测（决策 #18）。
/// </summary>
public sealed class ScriptMatcher
{
    /// <summary>所有模式正则的统一超时（§4.4 性能策略）。</summary>
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private readonly List<Regex> _matchPatterns = new();
    private readonly List<Regex> _includePatterns = new();
    private readonly List<Regex> _excludePatterns = new();
    private readonly List<Regex> _excludeMatchPatterns = new();

    public ScriptMatcher(ScriptMetadata metadata)
        : this(metadata?.Matches ?? throw new ArgumentNullException(nameof(metadata)),
               metadata.Includes, metadata.Excludes, metadata.ExcludeMatches)
    {
    }

    public ScriptMatcher(
        IEnumerable<string> matches,
        IEnumerable<string>? includes = null,
        IEnumerable<string>? excludes = null,
        IEnumerable<string>? excludeMatches = null)
    {
        if (matches == null)
            throw new ArgumentNullException(nameof(matches));

        foreach (var p in matches) AddCompiled(_matchPatterns, CompileMatchPattern(p));
        if (includes != null) foreach (var p in includes) AddCompiled(_includePatterns, CompileIncludePattern(p));
        if (excludes != null) foreach (var p in excludes) AddCompiled(_excludePatterns, CompileIncludePattern(p));
        if (excludeMatches != null) foreach (var p in excludeMatches) AddCompiled(_excludeMatchPatterns, CompileMatchPattern(p));
    }

    private static void AddCompiled(List<Regex> target, Regex? compiled)
    {
        if (compiled != null)
            target.Add(compiled);
    }

    /// <summary>匹配优先级（§4.3）：排除优先于匹配，全部未命中则不注入。</summary>
    public bool Matches(Uri uri)
    {
        if (uri == null)
            throw new ArgumentNullException(nameof(uri));
        return Matches(uri.AbsoluteUri);
    }

    /// <summary>字符串重载：WebView2 事件给的是字符串 URI，避免调用侧重复构造 Uri。</summary>
    public bool Matches(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (AnyMatch(_excludeMatchPatterns, url)) return false;
        if (AnyMatch(_excludePatterns, url)) return false;
        if (AnyMatch(_matchPatterns, url)) return true;
        if (AnyMatch(_includePatterns, url)) return true;
        return false;
    }

    private static bool AnyMatch(List<Regex> patterns, string url)
    {
        foreach (var re in patterns)
        {
            try
            {
                if (re.IsMatch(url)) return true;
            }
            catch (RegexMatchTimeoutException)
            {
                // 超时视为不匹配，绝不允许卡住导航（与广告拦截引擎策略一致）
            }
        }
        return false;
    }

    /// <summary>
    /// 编译 Chrome match pattern（§4.1）：&lt;scheme&gt;://&lt;host&gt;&lt;path&gt;。
    /// scheme '*' 仅匹配 http/https；host '*.example.com' 匹配 example.com 及其子域；
    /// 非法模式返回 null（丢弃）。
    /// </summary>
    public static Regex? CompileMatchPattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return null;
        var p = pattern!.Trim();

        if (p == "<all_urls>")
            return CreateRegex("^(https?|file|ftp)://.*$");

        int schemeEnd = p.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
            return null;
        string scheme = p.Substring(0, schemeEnd);
        string rest = p.Substring(schemeEnd + 3);

        int pathStart = rest.IndexOf('/');
        if (pathStart < 0)
            return null; // path 必须以 / 开头（§4.1 语法）
        string host = rest.Substring(0, pathStart);
        string path = rest.Substring(pathStart);

        string schemeRe;
        switch (scheme)
        {
            case "*": schemeRe = "https?"; break;
            case "http": case "https": case "file": case "ftp": schemeRe = scheme; break;
            default: return null;
        }

        string hostRe;
        if (host == "*")
            hostRe = @"[^/]*";
        else if (host.StartsWith("*.", StringComparison.Ordinal))
        {
            var baseHost = host.Substring(2);
            if (baseHost.Length == 0 || baseHost.IndexOf('*') >= 0)
                return null; // host 通配只允许前缀 *.
            hostRe = @"([^./]+\.)*" + Regex.Escape(baseHost);
        }
        else if (host.IndexOf('*') >= 0)
            return null; // host 中不允许其他位置的 *
        else
            hostRe = Regex.Escape(host);

        var sb = new StringBuilder("^").Append(schemeRe).Append("://").Append(hostRe);
        AppendGlob(sb, path);
        sb.Append('$');
        return CreateRegex(sb.ToString());
    }

    /// <summary>
    /// 编译 @include / @exclude 模式（§4.2）：/包裹/ 视为正则，否则整串按 * 通配符处理。
    /// 非法正则返回 null（丢弃）。
    /// </summary>
    public static Regex? CompileIncludePattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return null;
        var p = pattern!.Trim();

        if (p.Length >= 2 && p[0] == '/' && p[p.Length - 1] == '/')
        {
            // TM 语义：/re/ 是对 URL 的非锚定正则
            return CreateRegex(p.Substring(1, p.Length - 2));
        }

        var sb = new StringBuilder("^");
        AppendGlob(sb, p);
        sb.Append('$');
        return CreateRegex(sb.ToString());
    }

    /// <summary>把含 * 的通配串追加为正则片段（* → .*，其余转义）。</summary>
    private static void AppendGlob(StringBuilder sb, string glob)
    {
        foreach (char c in glob)
        {
            if (c == '*') sb.Append(".*");
            else sb.Append(Regex.Escape(c.ToString()));
        }
    }

    private static Regex? CreateRegex(string pattern)
    {
        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException)
        {
            return null; // 用户写坏的正则：丢弃该条模式，不拖垮整个脚本
        }
    }
}
