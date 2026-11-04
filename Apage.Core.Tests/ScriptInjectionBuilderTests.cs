using System;
using System.Collections.Generic;
using System.Text.Json;
using Apage.Core.Services.ScriptManager;
using Xunit;

namespace Apage.Core.Tests;

/// <summary>
/// GM 存储内联注入的结构不变量。真实同步执行、CSP、令牌与快照刷新由 UserScriptInjectionSpike 验证。
/// </summary>
public class ScriptInjectionBuilderTests
{
    private const string Marker = "window.__apageInjectionMarker = 47381;";

    [Fact]
    public void Build_NullRequiredArguments_Throws()
    {
        var metadata = new ScriptMetadata();

        Assert.Throws<ArgumentNullException>(() => ScriptInjectionBuilder.Build(null!, Marker, metadata));
        Assert.Throws<ArgumentNullException>(() => ScriptInjectionBuilder.Build("id", null!, metadata));
        Assert.Throws<ArgumentNullException>(() => ScriptInjectionBuilder.Build("id", Marker, null!));
        Assert.Throws<ArgumentException>(() => ScriptInjectionBuilder.Build("id", Marker, metadata, bridgeToken: "  "));
    }

    [Fact]
    public void Build_StoreAndWrappersPrecedeUserCode_UserCodeAppearsOnce()
    {
        var plan = ScriptInjectionBuilder.Build("script-a", Marker, Metadata("GM_getValue", "GM_setValue"),
            new Dictionary<string, object?> { ["answer"] = 42 });
        var js = plan.JavaScript;

        var storeIndex = js.IndexOf("const __STORE__", StringComparison.Ordinal);
        var getIndex = js.IndexOf("function GM_getValue", StringComparison.Ordinal);
        var setIndex = js.IndexOf("function GM_setValue", StringComparison.Ordinal);
        var sourceIndex = js.IndexOf(JsonSerializer.Serialize(Marker), StringComparison.Ordinal);
        Assert.True(storeIndex >= 0 && storeIndex < getIndex && getIndex < setIndex && setIndex < sourceIndex);
        Assert.Equal(sourceIndex, js.LastIndexOf(JsonSerializer.Serialize(Marker), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("GM_getValue")]
    [InlineData("GM_setValue")]
    public void Build_OnlyDeclaredGrant_EmitsOnlyDeclaredFunction(string grantedApi)
    {
        var js = ScriptInjectionBuilder.Build("id", Marker, Metadata(grantedApi)).JavaScript;

        Assert.Equal(grantedApi == "GM_getValue", js.Contains("function GM_getValue", StringComparison.Ordinal));
        Assert.Equal(grantedApi == "GM_setValue", js.Contains("function GM_setValue", StringComparison.Ordinal));
        Assert.Equal(grantedApi == "GM_getValue", js.Contains("__runUserScript(GM_getValue, undefined);", StringComparison.Ordinal));
        Assert.Equal(grantedApi == "GM_setValue", js.Contains("__runUserScript(undefined, GM_setValue);", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_GrantNone_SuppressesStorageApisAndBridge()
    {
        var js = ScriptInjectionBuilder.Build("id", Marker,
            Metadata("none", "GM_getValue", "GM_setValue")).JavaScript;

        Assert.Contains("__runUserScript(undefined, undefined);", js, StringComparison.Ordinal);
        Assert.DoesNotContain("function GM_getValue", js, StringComparison.Ordinal);
        Assert.DoesNotContain("function GM_setValue", js, StringComparison.Ordinal);
        Assert.DoesNotContain(ScriptInjectionBuilder.BridgeChannel, js, StringComparison.Ordinal);
    }

    [Fact]
    public void GetValue_UsesOwnPropertyLookup_SoExplicitNullDoesNotFallBack()
    {
        var js = ScriptInjectionBuilder.Build("id", Marker, Metadata("GM_getValue"),
            new Dictionary<string, object?> { ["nothing"] = null }).JavaScript;

        Assert.Contains("__hasOwn.call(__STORE__, normalizedKey)", js, StringComparison.Ordinal);
        Assert.DoesNotContain("__STORE__[normalizedKey] || defaultValue", js, StringComparison.Ordinal);
        Assert.Contains("nothing", js, StringComparison.Ordinal);
    }

    [Fact]
    public void SetValue_NormalizesThenUpdatesStoreBeforePostingAuthenticatedMessage()
    {
        var plan = ScriptInjectionBuilder.Build("script-α", Marker, Metadata("GM_setValue"),
            bridgeToken: "fixed-token");
        var js = plan.JavaScript;

        var normalizeIndex = js.IndexOf("const normalizedValue = __normalizeValue(value);", StringComparison.Ordinal);
        var writeIndex = js.IndexOf("__STORE__[normalizedKey] = normalizedValue;", StringComparison.Ordinal);
        var postIndex = js.IndexOf("__postMessage(__jsonStringify(__message));", StringComparison.Ordinal);
        Assert.True(normalizeIndex >= 0 && normalizeIndex < writeIndex && writeIndex < postIndex);
        Assert.Contains("const __message = __objectAssign(__objectCreate(null), {", js, StringComparison.Ordinal);
        Assert.Contains($"channel: {JsonSerializer.Serialize(ScriptInjectionBuilder.BridgeChannel)}", js, StringComparison.Ordinal);
        Assert.Contains("api: 'GM_setValue'", js, StringComparison.Ordinal);
        Assert.Contains("scriptId: __SCRIPT_ID__", js, StringComparison.Ordinal);
        Assert.Contains("bridgeToken: __BRIDGE_TOKEN__", js, StringComparison.Ordinal);
        Assert.Contains("args: __objectAssign(__objectCreate(null), {", js, StringComparison.Ordinal);
        Assert.Contains("key: normalizedKey, value: normalizedValue", js, StringComparison.Ordinal);
        Assert.DoesNotContain("__postMessage({", js, StringComparison.Ordinal);
        Assert.Contains(JsonSerializer.Serialize("script-α"), js, StringComparison.Ordinal);
        Assert.Contains(JsonSerializer.Serialize("fixed-token"), js, StringComparison.Ordinal);
        Assert.Equal("fixed-token", plan.BridgeToken);
    }

    [Fact]
    public void Build_UnsupportedValuesAreRejectedAndBridgeFailureDoesNotAbortUserScript()
    {
        var js = ScriptInjectionBuilder.Build("id", Marker, Metadata("GM_setValue")).JavaScript;

        Assert.Contains("if (value === undefined) return null;", js, StringComparison.Ordinal);
        Assert.Contains("return json === undefined ? null : __jsonParse(json);", js, StringComparison.Ordinal);
        Assert.Contains("throw new __TypeError('GM_setValue only supports JSON-compatible values');", js, StringComparison.Ordinal);
        Assert.Contains("if (__postMessage) {", js, StringComparison.Ordinal);
        Assert.Contains("try {", js, StringComparison.Ordinal);
        Assert.Contains("const __message = __objectAssign(__objectCreate(null), {", js, StringComparison.Ordinal);
        Assert.Contains("__postMessage(__jsonStringify(__message));", js, StringComparison.Ordinal);
        Assert.Contains("catch (error) {", js, StringComparison.Ordinal);
        Assert.Contains("if (__consoleWarn) __consoleWarn('[Apage] GM_setValue persistence failed', error);", js, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_CapturesNativePrimitivesBeforePageScripts()
    {
        var js = ScriptInjectionBuilder.Build("id", Marker, Metadata("GM_getValue", "GM_setValue")).JavaScript;

        Assert.Contains("const __Function = Function;", js, StringComparison.Ordinal);
        Assert.Contains("const __jsonParse = __JSON.parse.bind(__JSON);", js, StringComparison.Ordinal);
        Assert.Contains("__webview.postMessage.bind(__webview)", js, StringComparison.Ordinal);
        Assert.DoesNotContain("window.chrome.webview.postMessage({", js, StringComparison.Ordinal);
        Assert.DoesNotContain("__postMessage({", js, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_UserScriptRunsAsGlobalSloppyFunctionWithoutClosureAccess()
    {
        var plan = ScriptInjectionBuilder.Build("id", "var x = 1;", Metadata("GM_getValue", "GM_setValue"));
        var js = plan.JavaScript;

        Assert.Contains("'use strict';", js, StringComparison.Ordinal);
        Assert.Contains("const __userSource = \"var x = 1;\";", js, StringComparison.Ordinal);
        Assert.Contains("__Function('GM_getValue', 'GM_setValue', __userSource)", js, StringComparison.Ordinal);
        Assert.Contains("__runUserScript(GM_getValue, GM_setValue);", js, StringComparison.Ordinal);
        Assert.DoesNotContain("// === User script ===", js, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_GeneratesUniqueCryptographicBridgeTokens()
    {
        var first = ScriptInjectionBuilder.Build("id", Marker, Metadata("GM_setValue"));
        var second = ScriptInjectionBuilder.Build("id", Marker, Metadata("GM_setValue"));

        Assert.Matches("^[0-9a-f]{64}$", first.BridgeToken);
        Assert.Matches("^[0-9a-f]{64}$", second.BridgeToken);
        Assert.NotEqual(first.BridgeToken, second.BridgeToken);
    }

    [Fact]
    public void Store_UsesJsonParseAndNullPrototype_ForSpecialKeys()
    {
        var js = ScriptInjectionBuilder.Build("id", Marker, Metadata("GM_getValue", "GM_setValue"),
            new Dictionary<string, object?>
            {
                ["__proto__"] = "prototype-value",
                ["constructor"] = "constructor-value"
            }).JavaScript;

        Assert.Contains("__objectAssign(__objectCreate(null), __jsonParse(__STORE_JSON__))", js, StringComparison.Ordinal);
        Assert.Contains("__proto__", js, StringComparison.Ordinal);
        Assert.Contains("constructor", js, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_SpecialCharacters_AreJsonEscapedInsteadOfInterpolatedRaw()
    {
        const string scriptId = "id\"\\\n\u2028\u2029</script>";
        const string value = "引号\" 反斜杠\\ 换行\n 中文 \u2028 \u2029 </script>";

        var js = ScriptInjectionBuilder.Build(scriptId, Marker, Metadata("GM_getValue"),
            new Dictionary<string, object?> { ["complex"] = value }).JavaScript;

        Assert.DoesNotContain('\u2028', js);
        Assert.DoesNotContain('\u2029', js);
        Assert.DoesNotContain("</script>", js, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(JsonSerializer.Serialize(scriptId), js, StringComparison.Ordinal);
        Assert.Contains("\\u2028", js, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\\u2029", js, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_NestedValuesAndArrays_AreSerializedIntoInlineStore()
    {
        var values = new Dictionary<string, object?>
        {
            ["nested"] = new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["items"] = new object[] { 1, "two", null! }
            }
        };

        var js = ScriptInjectionBuilder.Build("id", Marker, Metadata("GM_getValue"), values).JavaScript;

        Assert.Contains("nested", js, StringComparison.Ordinal);
        Assert.Contains("enabled", js, StringComparison.Ordinal);
        Assert.Contains("items", js, StringComparison.Ordinal);
        Assert.Contains("two", js, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_DifferentScriptIds_EmitSeparateScriptIdentities()
    {
        var first = ScriptInjectionBuilder.Build("first-id", Marker, Metadata("GM_setValue")).JavaScript;
        var second = ScriptInjectionBuilder.Build("second-id", Marker, Metadata("GM_setValue")).JavaScript;

        Assert.Contains("\"first-id\"", first, StringComparison.Ordinal);
        Assert.DoesNotContain("\"second-id\"", first, StringComparison.Ordinal);
        Assert.Contains("\"second-id\"", second, StringComparison.Ordinal);
        Assert.DoesNotContain("\"first-id\"", second, StringComparison.Ordinal);
    }

    private static ScriptMetadata Metadata(params string[] grants)
    {
        var metadata = new ScriptMetadata();
        metadata.Grants.AddRange(grants);
        return metadata;
    }
}
