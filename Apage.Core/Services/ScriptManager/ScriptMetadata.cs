#nullable enable
using System;
using System.Collections.Generic;

namespace Apage.Core.Services.ScriptManager;

/// <summary>脚本注入时机（@run-at，docs/script-manager.md §3.4）。默认 document-end。</summary>
public enum RunAt
{
    DocumentStart,
    DocumentEnd,
    DocumentIdle,
}

/// <summary>
/// 解析后的用户脚本元数据（docs/script-manager.md §2.2 支持的字段）。
/// 由 <see cref="ScriptMetadataParser.Parse"/> 产出；@updateURL / @downloadURL 按零网络原则忽略，
/// 但记录在 <see cref="IgnoredKeys"/> 中供管理界面提示。
/// </summary>
public sealed class ScriptMetadata
{
    /// <summary>@name（必须）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>多语言名称（@name:zh-CN 等），key 为 locale。</summary>
    public Dictionary<string, string> LocalizedNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>@namespace，用于 GM_* 存储隔离与脚本 ID 生成。</summary>
    public string Namespace { get; set; } = string.Empty;

    /// <summary>@version（语义化版本号，可选）。</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>@description（可选）。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>多语言描述（@description:zh-CN 等）。</summary>
    public Dictionary<string, string> LocalizedDescriptions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>@author（可选）。</summary>
    public string Author { get; set; } = string.Empty;

    /// <summary>@icon（本地路径或 Data URL，可选）。</summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>@match（Chrome match pattern 语法）。</summary>
    public List<string> Matches { get; } = new();

    /// <summary>@include（通配符或 /正则/，TM 扩展语法）。</summary>
    public List<string> Includes { get; } = new();

    /// <summary>@exclude（同 @include 语法）。</summary>
    public List<string> Excludes { get; } = new();

    /// <summary>@exclude_match（match pattern 语法）。</summary>
    public List<string> ExcludeMatches { get; } = new();

    /// <summary>@grant 声明的 GM_* API 权限。</summary>
    public List<string> Grants { get; } = new();

    /// <summary>@require 依赖（仅本地文件，原样保留声明值）。</summary>
    public List<string> Requires { get; } = new();

    /// <summary>@resource 资源声明（仅本地，原样保留声明值）。</summary>
    public List<string> Resources { get; } = new();

    /// <summary>@connect 域名白名单（GM_xmlhttpRequest 用）。</summary>
    public List<string> Connects { get; } = new();

    /// <summary>@run-at 注入时机，默认 document-end。</summary>
    public RunAt RunAt { get; set; } = RunAt.DocumentEnd;

    /// <summary>@noframes：仅顶层窗口执行。</summary>
    public bool NoFrames { get; set; }

    /// <summary>被忽略的字段原始 key（updateURL / downloadURL），管理界面据此提示「不支持远程更新」。</summary>
    public List<string> IgnoredKeys { get; } = new();
}
