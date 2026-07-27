using Apage.Core.Services.ScriptManager;
using Xunit;

namespace Apage.Core.Tests;

/// <summary>
/// ScriptMetadataParser 核心不变量（docs/script-manager.md §2、§7.2，决策 #18）：
/// TM 兼容元数据块解析、必填字段校验顺序、零网络字段忽略、@run-at 回落默认。
/// </summary>
public class ScriptMetadataParserTests
{
    /// <summary>§2.1 的标准示例脚本，覆盖全部第一优先级字段。</summary>
    private const string FullScript = """
        // ==UserScript==
        // @name         示例脚本
        // @name:zh-CN   示例脚本中文名
        // @namespace    https://example.com/
        // @version      1.0.0
        // @description  一个示例用户脚本
        // @author       作者名
        // @match        https://*.example.com/*
        // @match        https://example.org/page*
        // @include      /^https?://.*\.test\.com/
        // @exclude      https://example.com/admin*
        // @grant        GM_getValue
        // @grant        GM_setValue
        // @run-at       document-end
        // @noframes
        // @updateURL    https://remote.example.com/update.user.js
        // ==/UserScript==

        (function() { 'use strict'; })();
        """;

    [Fact]
    public void Parse_FullScript_ExtractsAllFields()
    {
        var result = ScriptMetadataParser.Parse(FullScript);

        Assert.True(result.Success);
        var meta = result.Metadata!;
        Assert.Equal("示例脚本", meta.Name);
        Assert.Equal("示例脚本中文名", meta.LocalizedNames["zh-CN"]);
        Assert.Equal("https://example.com/", meta.Namespace);
        Assert.Equal("1.0.0", meta.Version);
        Assert.Equal("一个示例用户脚本", meta.Description);
        Assert.Equal("作者名", meta.Author);
        Assert.Equal(new[] { "https://*.example.com/*", "https://example.org/page*" }, meta.Matches);
        Assert.Equal(new[] { @"/^https?://.*\.test\.com/" }, meta.Includes);
        Assert.Equal(new[] { "https://example.com/admin*" }, meta.Excludes);
        Assert.Equal(new[] { "GM_getValue", "GM_setValue" }, meta.Grants);
        Assert.Equal(RunAt.DocumentEnd, meta.RunAt);
        Assert.True(meta.NoFrames);
    }

    [Fact]
    public void Parse_UpdateUrl_IsIgnoredButRecorded()
    {
        var meta = ScriptMetadataParser.Parse(FullScript).Metadata!;

        // 零网络原则：@updateURL 不进任何功能字段，仅记录供管理界面提示
        Assert.Contains("updateURL", meta.IgnoredKeys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("var x = 1; // 普通脚本，无元数据块")]
    public void Parse_NoMetadataBlock_Fails(string? source)
    {
        var result = ScriptMetadataParser.Parse(source);

        Assert.False(result.Success);
        Assert.Equal("未找到有效的元数据块", result.Error);
        Assert.Null(result.Metadata);
    }

    [Fact]
    public void Parse_UnterminatedBlock_Fails()
    {
        // 有始无终：块被非注释行打断，视为无效（§7.2 步骤 2）
        var result = ScriptMetadataParser.Parse("// ==UserScript==\n// @name x\nvar a = 1;");

        Assert.False(result.Success);
        Assert.Equal("未找到有效的元数据块", result.Error);
    }

    [Fact]
    public void Parse_MissingName_Fails()
    {
        var result = ScriptMetadataParser.Parse("""
            // ==UserScript==
            // @match https://example.com/*
            // ==/UserScript==
            """);

        Assert.False(result.Success);
        Assert.Equal("脚本缺少 @name", result.Error);
    }

    [Fact]
    public void Parse_MissingMatchAndInclude_Fails()
    {
        var result = ScriptMetadataParser.Parse("""
            // ==UserScript==
            // @name 无匹配规则脚本
            // @grant GM_getValue
            // ==/UserScript==
            """);

        Assert.False(result.Success);
        Assert.Equal("脚本缺少匹配规则", result.Error);
    }

    [Fact]
    public void Parse_IncludeAlone_SatisfiesMatchRequirement()
    {
        // §7.2：@match / @include 全缺失才报错，只有 @include 也合法
        var result = ScriptMetadataParser.Parse("""
            // ==UserScript==
            // @name 仅 include 脚本
            // @include https://*.example.com/*
            // ==/UserScript==
            """);

        Assert.True(result.Success);
        Assert.Empty(result.Metadata!.Matches);
        Assert.Single(result.Metadata.Includes);
    }

    [Fact]
    public void Parse_CrlfLineEndings_Works()
    {
        var source = "// ==UserScript==\r\n// @name win 脚本\r\n// @match https://a.com/*\r\n// ==/UserScript==\r\n";

        var result = ScriptMetadataParser.Parse(source);

        Assert.True(result.Success);
        Assert.Equal("win 脚本", result.Metadata!.Name);
    }

    [Theory]
    [InlineData("document-start", RunAt.DocumentStart)]
    [InlineData("document-end", RunAt.DocumentEnd)]
    [InlineData("document-idle", RunAt.DocumentIdle)]
    [InlineData("DOCUMENT-START", RunAt.DocumentStart)]
    [InlineData("bogus-value", RunAt.DocumentEnd)]   // 非法值回落默认（§2.2）
    [InlineData(null, RunAt.DocumentEnd)]
    public void ParseRunAt_MapsAndFallsBackToDocumentEnd(string? value, RunAt expected)
    {
        Assert.Equal(expected, ScriptMetadataParser.ParseRunAt(value));
    }

    [Fact]
    public void Parse_DefaultRunAt_IsDocumentEnd()
    {
        var result = ScriptMetadataParser.Parse("""
            // ==UserScript==
            // @name 默认时机脚本
            // @match https://a.com/*
            // ==/UserScript==
            """);

        Assert.Equal(RunAt.DocumentEnd, result.Metadata!.RunAt);
        Assert.False(result.Metadata.NoFrames);
    }
}
