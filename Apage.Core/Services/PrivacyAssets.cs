#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Apage.Core.Models;

namespace Apage.Core.Services;

/// <summary>
/// 随包 PSL + 内置网络规则快照。加载时校验清单与 SHA-256，规则集合只包含
/// <see cref="AdBlockRuleEngine.Classify"/> 判定可正确执行的条目；任何不一致都抛错，
/// 由宿主回退到无内置规则/末两标签，而不是带病运行。
/// </summary>
public sealed class PrivacyAssets
{
    private readonly byte[] _rulesBytes;

    private PrivacyAssets(PrivacyAssetManifest manifest, PublicSuffixList publicSuffix, byte[] rulesBytes)
    {
        Manifest = manifest;
        PublicSuffix = publicSuffix;
        _rulesBytes = rulesBytes;
    }

    public PrivacyAssetManifest Manifest { get; }

    public PublicSuffixList PublicSuffix { get; }

    public static PrivacyAssets LoadEmbedded()
    {
        var manifest = EmbeddedPrivacyAssets.LoadManifest();
        var pslBytes = EmbeddedPrivacyAssets.ReadBytes(".public_suffix_list.dat");
        EmbeddedPrivacyAssets.VerifySha256(pslBytes, manifest.Psl.Sha256, "PSL");
        using (var pslReader = new StreamReader(
            new MemoryStream(pslBytes, writable: false), Encoding.UTF8, true))
        {
            var psl = PublicSuffixList.Parse(pslReader);
            if (!string.IsNullOrEmpty(manifest.Psl.Version) &&
                !string.Equals(psl.Version, manifest.Psl.Version, StringComparison.Ordinal))
            {
                throw new InvalidDataException("PSL 清单版本与随包文件版本不一致。");
            }

            var ruleBytes = EmbeddedPrivacyAssets.ReadBytes(".builtin-rules.txt");
            EmbeddedPrivacyAssets.VerifySha256(ruleBytes, manifest.Rules.Sha256, "规则快照");
            return new PrivacyAssets(manifest, psl, ruleBytes);
        }
    }

    /// <summary>构建内置规则引擎；条数/分类不一致则视为打包错误并抛错。</summary>
    public AdBlockRuleEngine CreateRuleEngine()
    {
        var engine = new AdBlockRuleEngine();
        using var stream = new MemoryStream(_rulesBytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        string? line;
        while ((line = reader.ReadLine()) != null)
            engine.AddRule(line);

        if (engine.UnsupportedModifierRuleCount != 0 || engine.CosmeticCount != 0 ||
            engine.BlockCount + engine.ExceptionCount != Manifest.Rules.AcceptedRules)
        {
            throw new InvalidDataException(
                $"规则快照清单不一致：accepted={Manifest.Rules.AcceptedRules}, " +
                $"block={engine.BlockCount}, exception={engine.ExceptionCount}, " +
                $"cosmetic={engine.CosmeticCount}, unsupported={engine.UnsupportedModifierRuleCount}");
        }

        return engine;
    }
}

internal static class EmbeddedPrivacyAssets
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    public static PrivacyAssetManifest LoadManifest()
    {
        var bytes = ReadBytes(".privacy-assets.json");
        var manifest = JsonSerializer.Deserialize<PrivacyAssetManifest>(
            Encoding.UTF8.GetString(bytes), JsonOptions);
        if (manifest == null || manifest.SchemaVersion != 1)
            throw new InvalidDataException("隐私资产清单缺失或 schema 不受支持。");
        if (string.IsNullOrWhiteSpace(manifest.Psl.Sha256) ||
            string.IsNullOrWhiteSpace(manifest.Rules.Sha256))
            throw new InvalidDataException("隐私资产清单缺少 SHA-256。");
        return manifest;
    }

    public static byte[] ReadBytes(string resourceSuffix)
    {
        var assembly = typeof(PrivacyAssets).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase));
        if (resource == null)
            throw new InvalidOperationException("缺少嵌入资源：" + resourceSuffix);

        using var stream = assembly.GetManifestResourceStream(resource);
        if (stream == null)
            throw new InvalidOperationException("无法打开嵌入资源：" + resourceSuffix);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public static void VerifySha256(byte[] bytes, string expected, string label)
    {
        using var sha = SHA256.Create();
        var actual = ToHex(sha.ComputeHash(bytes));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{label} SHA-256 不匹配。");
    }

    private static string ToHex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
            builder.Append(b.ToString("x2"));
        return builder.ToString();
    }
}
