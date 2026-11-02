#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Apage.Core.Services;

/// <summary>
/// 用户本地规则（{数据目录}\adblock\*.txt）的唯一读取入口。
/// 启动加载与手动更新共用同一实现，避免更新后热替换引擎时漏掉用户规则。
/// 文件按名称排序后读取；任何 IO 失败向上抛出，由调用方决定回退或放弃更新。
/// </summary>
public static class UserRuleStore
{
    public static IReadOnlyList<string> LoadAll(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return Array.Empty<string>();

        var lines = new List<string>();
        foreach (var file in Directory.GetFiles(directory, "*.txt").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            lines.AddRange(File.ReadLines(file));

        return lines;
    }
}
