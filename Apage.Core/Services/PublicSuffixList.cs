#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace Apage.Core.Services;

/// <summary>第三方判定所依赖的 registrable domain 查询抽象；UI 不直接读取 PSL 文件。</summary>
public interface IPublicSuffixResolver
{
    /// <summary>返回 host 的 registrable domain（eTLD+1）；IP/localhost/非法值/仅公共后缀返回 null。</summary>
    string? GetRegistrableDomain(string? host);
}

/// <summary>
/// 随包 Public Suffix List（MPL 2.0）解析器。不可变、线程安全、纯内存查询。
/// 支持普通规则、`*.` 通配规则、`!` 例外规则、IDN（Unicode ↔ punycode）、大小写和尾点归一化；
/// IP、localhost、单标签、非法输入返回 null，调用方必须按“宁可漏判不可误判”回退。
/// </summary>
public sealed class PublicSuffixList : IPublicSuffixResolver
{
    private readonly HashSet<string> _rules;
    private readonly HashSet<string> _wildcards;
    private readonly HashSet<string> _exceptions;

    private PublicSuffixList(
        HashSet<string> rules,
        HashSet<string> wildcards,
        HashSet<string> exceptions,
        string? version,
        string? commit)
    {
        _rules = rules;
        _wildcards = wildcards;
        _exceptions = exceptions;
        Version = version;
        Commit = commit;
    }

    public int RuleCount => _rules.Count + _wildcards.Count + _exceptions.Count;

    public string? Version { get; }

    public string? Commit { get; }

    /// <summary>读取嵌入 Core 的 `public_suffix_list.dat`，并校验清单版本与 SHA-256；失败由宿主安全回退。</summary>
    public static PublicSuffixList LoadEmbedded()
    {
        var manifest = EmbeddedPrivacyAssets.LoadManifest();
        var bytes = EmbeddedPrivacyAssets.ReadBytes(".public_suffix_list.dat");
        EmbeddedPrivacyAssets.VerifySha256(bytes, manifest.Psl.Sha256, "PSL");

        using var reader = new StreamReader(
            new MemoryStream(bytes, writable: false), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var psl = Parse(reader);
        if (!string.IsNullOrEmpty(manifest.Psl.Version) &&
            !string.Equals(psl.Version, manifest.Psl.Version, StringComparison.Ordinal))
        {
            throw new InvalidDataException("PSL 清单版本与随包文件版本不一致。");
        }
        return psl;
    }

    /// <summary>解析 PSL 文本流；供随包快照和测试使用。</summary>
    public static PublicSuffixList Parse(TextReader reader)
    {
        if (reader == null)
            throw new ArgumentNullException(nameof(reader));

        var rules = new HashSet<string>(StringComparer.Ordinal);
        var wildcards = new HashSet<string>(StringComparer.Ordinal);
        var exceptions = new HashSet<string>(StringComparer.Ordinal);
        string? version = null;
        string? commit = null;

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var text = line.Trim();
            if (text.StartsWith("//", StringComparison.Ordinal))
            {
                const string versionPrefix = "// VERSION:";
                const string commitPrefix = "// COMMIT:";
                if (text.StartsWith(versionPrefix, StringComparison.Ordinal))
                    version = text.Substring(versionPrefix.Length).Trim();
                else if (text.StartsWith(commitPrefix, StringComparison.Ordinal))
                    commit = text.Substring(commitPrefix.Length).Trim();
                continue;
            }
            if (text.Length == 0)
                continue;

            var isException = text[0] == '!';
            if (isException)
                text = text.Substring(1);
            if (text.Length == 0)
                continue;

            if (text.StartsWith("*.", StringComparison.Ordinal))
            {
                var parent = NormalizeRule(text.Substring(2));
                if (parent != null)
                    wildcards.Add(parent);
                continue;
            }

            var normalized = NormalizeRule(text);
            if (normalized == null)
                continue;
            if (isException)
                exceptions.Add(normalized);
            else
                rules.Add(normalized);
        }

        if (rules.Count == 0 && wildcards.Count == 0 && exceptions.Count == 0)
            throw new InvalidDataException("Public Suffix List 为空。");

        return new PublicSuffixList(rules, wildcards, exceptions, version, commit);
    }

    public string? GetRegistrableDomain(string? host)
    {
        var normalized = NormalizeHost(host);
        if (normalized == null)
            return null;

        var labels = normalized.Split('.');
        if (labels.Length <= 1)
            return null;

        var suffix = GetPublicSuffixCore(normalized, labels);
        if (suffix == null)
            return null;

        var suffixLabels = suffix.Split('.').Length;
        if (labels.Length <= suffixLabels)
            return null;

        return string.Join(".", labels.Skip(labels.Length - suffixLabels - 1));
    }

    /// <summary>返回 host 的公共后缀；默认规则 `*` 意味着未知单标签本身即公共后缀。</summary>
    public string? GetPublicSuffix(string? host)
    {
        var normalized = NormalizeHost(host);
        return normalized == null ? null : GetPublicSuffixCore(normalized, normalized.Split('.'));
    }

    /// <summary>归一化 host：去空白/尾点、IDN 转 ASCII、小写；IP、端口、空值返回 null。</summary>
    public static string? NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return null;

        var value = host!.Trim();
        if (value.StartsWith("[", StringComparison.Ordinal) ||
            value.IndexOf(':') >= 0) // IPv6，或误传 host:port
            return null;
        value = value.TrimEnd('.');
        if (value.Length == 0)
            return null;

        if (IPAddress.TryParse(value, out _))
            return null;

        try
        {
            var idn = new IdnMapping { AllowUnassigned = true, UseStd3AsciiRules = false };
            var ascii = idn.GetAscii(value);
            return ascii.ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private string? GetPublicSuffixCore(string host, string[] labels)
    {
        string? normalMatch = null;
        for (var start = 0; start < labels.Length; start++)
        {
            var candidate = string.Join(".", labels.Skip(start));

            // 例外规则优先于普通/通配规则；公共后缀为去掉最左标签后的部分。
            if (_exceptions.Contains(candidate))
                return start < labels.Length - 1 ? string.Join(".", labels.Skip(start + 1)) : null;

            if (normalMatch != null)
                continue;

            if (_rules.Contains(candidate))
            {
                normalMatch = candidate;
                continue;
            }

            if (start < labels.Length - 1)
            {
                var parent = string.Join(".", labels.Skip(start + 1));
                if (_wildcards.Contains(parent))
                    normalMatch = candidate;
            }
        }

        return normalMatch ?? labels[labels.Length - 1];
    }

    private static string? NormalizeRule(string rule)
    {
        var normalized = NormalizeHost(rule.TrimEnd('.'));
        if (normalized == null || normalized.Contains("*"))
            return null;
        return normalized;
    }
}
