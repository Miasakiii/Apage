#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Apage.Core.Services.ScriptManager;

/// <summary>单个用户脚本的注入产物。BridgeToken 用于拒绝无凭据的页面消息伪造，不是安全沙箱或密码学隔离。</summary>
public sealed class ScriptInjectionPlan
{
    public ScriptInjectionPlan(string javaScript, string bridgeToken)
    {
        JavaScript = javaScript;
        BridgeToken = bridgeToken;
    }

    public string JavaScript { get; }
    public string BridgeToken { get; }
}

/// <summary>
/// 生成单个用户脚本的注入源码。存储在注入时同步内联，避免 document-start 阶段
/// 因 WebMessage 异步往返而错误返回默认值。
/// </summary>
/// <remarks>
/// 边界：本实现提供 GM 存储语义与脚本命名空间隔离，不是安全沙箱；用户脚本仍与页面共享 DOM/全局对象。
/// 宿主必须把 <see cref="ScriptInjectionPlan.BridgeToken"/> 登记为当次注册凭证，用于拒绝无凭据的页面伪造；
/// 页面消息中的 scriptId 一律不可信。生产桥接还应校验消息 Source/frame，并不得把 token 当作密码学隔离。
/// 持久化消息使用 null-prototype 对象预序列化为字符串，避免页面后置 Object.prototype.toJSON 钩子嗅探凭证。
/// 内联 KV 只是 Build 时的快照；WebView2 会保留 document-created 脚本直至移除，因此宿主在持久化值更新后、
/// 下一次导航前必须移除旧注册并重新 Build。GM_setValue 当前为 fire-and-forget：postMessage 失败时内存值仍更新，
/// 但持久化可能丢失；生产接入需补 ACK/重试，不能把该路径视为已确认落盘。
/// 见 https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/win32/icorewebview2?view=webview2-1.0.4078.44#addscripttoexecuteondocumentcreated
/// </remarks>
public static class ScriptInjectionBuilder
{
    public const string BridgeChannel = "script-bridge";

    /// <summary>
    /// 生成可由 AddScriptToExecuteOnDocumentCreatedAsync 注册的 JavaScript。
    /// 当前仅实现声明了对应 <c>@grant</c> 的 GM_getValue / GM_setValue。
    /// </summary>
    /// <param name="bridgeToken">仅供确定性测试显式传入；生产调用应省略，由本方法生成 256-bit 随机值。</param>
    public static ScriptInjectionPlan Build(
        string scriptId,
        string source,
        ScriptMetadata metadata,
        IReadOnlyDictionary<string, object?>? initialValues = null,
        string? bridgeToken = null)
    {
        if (scriptId == null) throw new ArgumentNullException(nameof(scriptId));
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (metadata == null) throw new ArgumentNullException(nameof(metadata));
        if (bridgeToken != null && string.IsNullOrWhiteSpace(bridgeToken))
            throw new ArgumentException("Bridge token must not be empty.", nameof(bridgeToken));

        var token = bridgeToken ?? CreateBridgeToken();
        var storeJson = JsonSerializer.Serialize(initialValues ?? new Dictionary<string, object?>());
        var grants = new HashSet<string>(metadata.Grants, StringComparer.Ordinal);
        var grantNone = grants.Contains("none");
        var canGet = !grantNone && grants.Contains("GM_getValue");
        var canSet = !grantNone && grants.Contains("GM_setValue");

        var js = new StringBuilder(source.Length + storeJson.Length + 2600);
        js.AppendLine(";(function (__SCRIPT_ID__, __STORE_JSON__, __BRIDGE_TOKEN__) {");
        js.AppendLine("    'use strict';");
        // 在页面脚本运行前捕获原生能力，避免页面后续 monkey-patch 影响 GM 包装器。
        js.AppendLine("    const __Function = Function;");
        js.AppendLine("    const __TypeError = TypeError;");
        js.AppendLine("    const __String = String;");
        js.AppendLine("    const __Object = Object;");
        js.AppendLine("    const __JSON = JSON;");
        js.AppendLine("    const __jsonParse = __JSON.parse.bind(__JSON);");
        js.AppendLine("    const __jsonStringify = __JSON.stringify.bind(__JSON);");
        js.AppendLine("    const __objectCreate = __Object.create.bind(__Object);");
        js.AppendLine("    const __objectAssign = __Object.assign.bind(__Object);");
        js.AppendLine("    const __hasOwn = __Object.prototype.hasOwnProperty;");
        // JSON.parse + null prototype 保留 "__proto__" 等普通数据键，并避免原型 setter 污染。
        js.AppendLine("    const __STORE__ = __objectAssign(__objectCreate(null), __jsonParse(__STORE_JSON__));");
        js.AppendLine("    const __webview = window.chrome && window.chrome.webview;");
        js.AppendLine("    const __postMessage = __webview && typeof __webview.postMessage === 'function'");
        js.AppendLine("        ? __webview.postMessage.bind(__webview)");
        js.AppendLine("        : null;");
        js.AppendLine("    const __consoleWarn = window.console && typeof window.console.warn === 'function'");
        js.AppendLine("        ? window.console.warn.bind(window.console)");
        js.AppendLine("        : null;");
        js.AppendLine("    // JSON-compatible value domain: undefined/NaN/Infinity -> null, Date -> ISO string, BigInt/cycles -> TypeError.");
        js.AppendLine("    function __normalizeValue(value) {");
        js.AppendLine("        if (value === undefined) return null;");
        js.AppendLine("        try {");
        js.AppendLine("            const json = __jsonStringify(value);");
        js.AppendLine("            return json === undefined ? null : __jsonParse(json);");
        js.AppendLine("        } catch (error) {");
        js.AppendLine("            throw new __TypeError('GM_setValue only supports JSON-compatible values');");
        js.AppendLine("        }");
        js.AppendLine("    }");

        if (canGet)
        {
            js.AppendLine("    function GM_getValue(key, defaultValue) {");
            js.AppendLine("        const normalizedKey = __String(key);");
            js.AppendLine("        return __hasOwn.call(__STORE__, normalizedKey) ? __STORE__[normalizedKey] : defaultValue;");
            js.AppendLine("    }");
        }

        if (canSet)
        {
            js.AppendLine("    function GM_setValue(key, value) {");
            js.AppendLine("        const normalizedKey = __String(key);");
            js.AppendLine("        const normalizedValue = __normalizeValue(value);");
            js.AppendLine("        __STORE__[normalizedKey] = normalizedValue;");
            js.AppendLine("        if (__postMessage) {");
            js.AppendLine("            try {");
            js.AppendLine("                const __message = __objectAssign(__objectCreate(null), {");
            js.AppendLine($"                    channel: {JsonSerializer.Serialize(BridgeChannel)},");
            js.AppendLine("                    api: 'GM_setValue',");
            js.AppendLine("                    scriptId: __SCRIPT_ID__,");
            js.AppendLine("                    bridgeToken: __BRIDGE_TOKEN__,");
            js.AppendLine("                    args: __objectAssign(__objectCreate(null), {");
            js.AppendLine("                        key: normalizedKey, value: normalizedValue");
            js.AppendLine("                    })");
            js.AppendLine("                });");
            // 预序列化为字符串并发送：避免页面后置 Object.prototype.toJSON 钩子嗅探 bridgeToken。
            js.AppendLine("                __postMessage(__jsonStringify(__message));");
            js.AppendLine("            } catch (error) {");
            js.AppendLine("                if (__consoleWarn) __consoleWarn('[Apage] GM_setValue persistence failed', error);");
            js.AppendLine("            }");
            js.AppendLine("        }");
            js.AppendLine("    }");
        }

        // Function 构造函数在全局作用域解析用户源码：既保持非严格模式的 TM 兼容语义，
        // 也不能从闭包读取 __STORE__ / __BRIDGE_TOKEN__。严格的外层 IIFE 阻断 caller 链回溯。
        js.AppendLine("    const __userSource = " + JsonSerializer.Serialize(source) + ";");
        js.AppendLine("    const __runUserScript = __Function('GM_getValue', 'GM_setValue', __userSource);");
        js.Append("    __runUserScript(");
        js.Append(canGet ? "GM_getValue" : "undefined");
        js.Append(", ");
        js.Append(canSet ? "GM_setValue" : "undefined");
        js.AppendLine(");");
        js.Append("})(");
        js.Append(JsonSerializer.Serialize(scriptId));
        js.Append(", ");
        js.Append(JsonSerializer.Serialize(storeJson));
        js.Append(", ");
        js.Append(JsonSerializer.Serialize(token));
        js.AppendLine(");");

        return new ScriptInjectionPlan(js.ToString(), token);
    }

    /// <summary>生成 256-bit 桥接令牌；每次注册及快照刷新都应重新生成。</summary>
    public static string CreateBridgeToken()
    {
        var bytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }
        return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
    }
}
