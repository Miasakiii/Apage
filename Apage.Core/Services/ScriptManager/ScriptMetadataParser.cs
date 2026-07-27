#nullable enable
using System;
using System.Collections.Generic;

namespace Apage.Core.Services.ScriptManager;

/// <summary>
/// Tampermonkey 兼容元数据块解析器（docs/script-manager.md §2、§7.2）。
/// 纯逻辑、无 IO：输入脚本全文，输出 <see cref="ScriptParseResult"/>；便于单测（决策 #18）。
/// </summary>
public static class ScriptMetadataParser
{
    private const string BlockStart = "==UserScript==";
    private const string BlockEnd = "==/UserScript==";

    /// <summary>
    /// 解析 .user.js 全文。校验顺序与导入流程一致（§7.2）：
    /// 元数据块缺失 → @name 缺失 → @match/@include 全缺失，任一失败即返回错误。
    /// </summary>
    public static ScriptParseResult Parse(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return ScriptParseResult.Fail("未找到有效的元数据块");

        var lines = ExtractMetadataLines(source!);
        if (lines == null)
            return ScriptParseResult.Fail("未找到有效的元数据块");

        var meta = new ScriptMetadata();
        foreach (var line in lines)
        {
            if (!TryParseDirective(line, out var key, out var value))
                continue;
            ApplyDirective(meta, key, value);
        }

        if (string.IsNullOrWhiteSpace(meta.Name))
            return ScriptParseResult.Fail("脚本缺少 @name");
        if (meta.Matches.Count == 0 && meta.Includes.Count == 0)
            return ScriptParseResult.Fail("脚本缺少匹配规则");

        return ScriptParseResult.Ok(meta);
    }

    /// <summary>
    /// 提取 // ==UserScript== 与 // ==/UserScript== 之间的注释行；块不完整返回 null。
    /// </summary>
    private static List<string>? ExtractMetadataLines(string source)
    {
        var lines = source.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        List<string>? collected = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (!line.StartsWith("//", StringComparison.Ordinal))
            {
                // 元数据块必须是连续注释；块开始后遇到非注释行即视为块被打断
                if (collected != null) return null;
                continue;
            }

            var comment = line.Substring(2).Trim();
            if (collected == null)
            {
                if (comment == BlockStart)
                    collected = new List<string>();
                continue;
            }
            if (comment == BlockEnd)
                return collected;
            collected.Add(comment);
        }
        return null; // 无起始标记，或有始无终
    }

    /// <summary>解析一行「@key value」指令；非 @ 开头的行忽略。</summary>
    private static bool TryParseDirective(string comment, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;
        if (comment.Length < 2 || comment[0] != '@')
            return false;

        int sep = comment.IndexOfAny(new[] { ' ', '\t' });
        if (sep < 0)
        {
            key = comment.Substring(1);
            return key.Length > 0;
        }
        key = comment.Substring(1, sep - 1);
        value = comment.Substring(sep + 1).Trim();
        return key.Length > 0;
    }

    private static void ApplyDirective(ScriptMetadata meta, string key, string value)
    {
        // 多语言字段：@name:zh-CN / @description:zh-CN（§2.2）
        int colon = key.IndexOf(':');
        if (colon > 0)
        {
            var baseKey = key.Substring(0, colon);
            var locale = key.Substring(colon + 1);
            if (locale.Length > 0 && value.Length > 0)
            {
                if (string.Equals(baseKey, "name", StringComparison.OrdinalIgnoreCase))
                    meta.LocalizedNames[locale] = value;
                else if (string.Equals(baseKey, "description", StringComparison.OrdinalIgnoreCase))
                    meta.LocalizedDescriptions[locale] = value;
            }
            return;
        }

        switch (key.ToLowerInvariant())
        {
            case "name": meta.Name = value; break;
            case "namespace": meta.Namespace = value; break;
            case "version": meta.Version = value; break;
            case "description": meta.Description = value; break;
            case "author": meta.Author = value; break;
            case "icon": meta.Icon = value; break;
            case "match": AddIfNotEmpty(meta.Matches, value); break;
            case "include": AddIfNotEmpty(meta.Includes, value); break;
            case "exclude": AddIfNotEmpty(meta.Excludes, value); break;
            case "exclude_match": AddIfNotEmpty(meta.ExcludeMatches, value); break;
            case "grant": AddIfNotEmpty(meta.Grants, value); break;
            case "require": AddIfNotEmpty(meta.Requires, value); break;
            case "resource": AddIfNotEmpty(meta.Resources, value); break;
            case "connect": AddIfNotEmpty(meta.Connects, value); break;
            case "noframes": meta.NoFrames = true; break;
            case "run-at": meta.RunAt = ParseRunAt(value); break;
            case "updateurl":
            case "downloadurl":
                // 零网络原则：忽略远程更新字段，但记录供管理界面提示（§2.2）
                meta.IgnoredKeys.Add(key);
                break;
            default:
                break; // 未知字段静默忽略（TM 兼容行为）
        }
    }

    private static void AddIfNotEmpty(List<string> list, string value)
    {
        if (value.Length > 0)
            list.Add(value);
    }

    /// <summary>@run-at 解析：非法值回落默认 document-end（§2.2）。</summary>
    public static RunAt ParseRunAt(string? value)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "document-start": return RunAt.DocumentStart;
            case "document-idle": return RunAt.DocumentIdle;
            default: return RunAt.DocumentEnd;
        }
    }
}

/// <summary>解析结果：成功携带 <see cref="Metadata"/>，失败携带面向用户的错误文案（§7.2）。</summary>
public sealed class ScriptParseResult
{
    public bool Success { get; }
    public string Error { get; }
    public ScriptMetadata? Metadata { get; }

    private ScriptParseResult(bool success, string error, ScriptMetadata? metadata)
    {
        Success = success;
        Error = error;
        Metadata = metadata;
    }

    public static ScriptParseResult Ok(ScriptMetadata metadata) => new ScriptParseResult(true, string.Empty, metadata);

    public static ScriptParseResult Fail(string error) => new ScriptParseResult(false, error, null);
}
