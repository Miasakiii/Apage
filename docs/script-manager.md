# Apage 脚本管理器设计文档

> 补充设计文档 | 版本：v0.1 | 创建：2026-07-19
>
> 关联：product-design.md §5（功能设计）、roadmap.md Phase 7+

---

## 一、定位与目标

### 为什么脚本管理器是 Apage 的核心差异化

Apage 在浏览器壳层面与 ZZZ 的技术差距有限（WebMessage 异步通信是优势，但不构成壁垒）。真正的差异化在于**用户生态**——ZZZ 有 GM_* 运行时但没有管理界面，Min/Helium 完全没有用户脚本能力。

Apage 的脚本管理器目标：**完全离线、本地优先的用户脚本全生命周期管理**，让用户在不联网的情况下也能拥有完整的脚本安装、编辑、调试体验。

### 设计原则

| 原则 | 说明 |
|------|------|
| Tampermonkey 兼容 | 元数据格式、GM_* API 行为与 TM 对齐，社区脚本可直接导入 |
| 零网络依赖 | 脚本导入仅通过本地文件，不内置远程订阅（与零遥测定位一致） |
| 透明可控 | 用户能看到每个脚本的权限、匹配规则、存储数据，随时启用/禁用/删除 |
| 安全隔离 | 脚本在受限环境中执行，权限由元数据声明，未声明的 API 不可调用 |

---

## 二、脚本格式

### 2.1 Tampermonkey 兼容元数据块

采用 `// ==UserScript==` 标准格式，确保 Greasy Fork、OpenUserJS 等平台的脚本可直接导入：

```javascript
// ==UserScript==
// @name         示例脚本
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
// @grant        GM_xmlhttpRequest
// @grant        GM_addStyle
// @run-at       document-end
// @noframes
// ==/UserScript==

(function() {
    'use strict';
    // 脚本主体
    const val = GM_getValue('counter', 0);
    GM_setValue('counter', val + 1);
    console.log(`访问次数: ${val + 1}`);
})();
```

### 2.2 支持的元数据字段

| 字段 | 支持 | 说明 |
|------|------|------|
| `@name` | 必须 | 脚本名称，支持多语言 `@name:zh-CN` |
| `@namespace` | 可选 | 脚本命名空间，用于 GM_* 存储隔离 |
| `@version` | 可选 | 语义化版本号 |
| `@description` | 可选 | 脚本描述，支持多语言 |
| `@author` | 可选 | 作者名 |
| `@match` | 必须（至少一个） | URL 匹配模式（Chrome match pattern 语法） |
| `@include` | 可选 | URL 匹配（支持正则，TM 扩展语法） |
| `@exclude` | 可选 | URL 排除模式 |
| `@exclude_match` | 可选 | 同 `@exclude`，match pattern 语法 |
| `@grant` | 必须 | 声明需要的 GM_* API 权限 |
| `@run-at` | 可选 | `document-start` / `document-end`（默认） / `document-idle` |
| `@noframes` | 可选 | 仅在顶层窗口执行，不在 iframe 中执行 |
| `@require` | 可选 | 依赖的外部 JS 库（**仅支持本地文件，不支持远程 URL**） |
| `@resource` | 可选 | 外部资源声明（同上，仅本地） |
| `@icon` | 可选 | 脚本图标 |
| `@connect` | 可选 | `GM_xmlhttpRequest` 允许的域名白名单 |
| `@updateURL` | 忽略 | Apage 不做远程更新，此字段被忽略并在管理界面提示 |
| `@downloadURL` | 忽略 | 同上 |

### 2.3 不支持的字段（明确标注）

| 字段 | 原因 |
|------|------|
| `@updateURL` / `@downloadURL` | 与零网络原则冲突 |
| `@antifeature` | 低优先级，后续考虑 |

---

## 三、GM_* API 运行时

### 3.1 架构总览

```
┌─────────────────────────────────────────────────────────────┐
│  WebView2 (渲染进程)                                         │
│                                                             │
│  ┌─────────────┐    ┌──────────────────────────────────────┐│
│  │  网页 JS     │    │  用户脚本沙箱（IIFE 包裹）             ││
│  │  (不可访问   │    │  ┌────────────────────────────────┐  ││
│  │   GM_* API)  │    │  │  GM_getValue / GM_setValue     │  ││
│  └─────────────┘    │  │  GM_xmlhttpRequest              │  ││
│                     │  │  GM_addStyle / GM_addElement     │  ││
│                     │  │  GM_notification / GM_openInTab   │  ││
│                     │  │  ...                              │  ││
│                     │  └─────────┬────────────────────────┘  ││
│                     │           │ WebMessage (postMessage)    ││
│                     └───────────┼────────────────────────────┘│
│                                 │                              │
├─────────────────────────────────┼──────────────────────────────┤
│  C# Backend                     │                              │
│  ┌──────────────────────────────▼────────────────────────────┐│
│  │  ScriptBridgeService                                      ││
│  │  ├─ 请求分发（按 API 类型路由）                             ││
│  │  ├─ 权限校验（对照 @grant 声明）                            ││
│  │  ├─ 命名空间隔离（按 @namespace + @name 隔离存储）          ││
│  │  └─ 结果回调（通过 id 关联返回）                            ││
│  └───────────────────────────────────────────────────────────┘│
│  ┌────────────────────┐  ┌──────────────────────────────────┐│
│  │  ScriptStorage      │  │  ScriptHttpClient               ││
│  │  (SQLite per-script)│  │  (GM_xmlhttpRequest 代理)       ││
│  └────────────────────┘  └──────────────────────────────────┘│
└─────────────────────────────────────────────────────────────┘
```

### 3.2 WebMessage 桥接协议

#### 请求格式（JS → C#）

```javascript
// 随 AddScriptToExecuteOnDocumentCreated 注入的桥接层
const ScriptBridge = {
    _seq: 0,
    _pending: new Map(),

    call(api, args) {
        return new Promise((resolve, reject) => {
            const id = ++this._seq;
            this._pending.set(id, { resolve, reject });
            window.chrome.webview.postMessage({
                channel: 'script-bridge',   // 与 TabService 消息区分
                id,
                api,                        // "GM_getValue" | "GM_setValue" | ...
                scriptId: __SCRIPT_ID__,    // 注入时替换为脚本唯一标识
                args                        // API 参数数组
            });
        });
    },

    _handleResponse(msg) {
        const p = this._pending.get(msg.id);
        if (!p) return;
        this._pending.delete(msg.id);
        msg.ok ? p.resolve(msg.data) : p.reject(new Error(msg.error));
    }
};

// 监听响应
window.chrome.webview.addEventListener('message', e => {
    if (e.data?.channel === 'script-bridge') {
        ScriptBridge._handleResponse(e.data);
    }
});
```

#### 响应格式（C# → JS）

```csharp
// ScriptBridgeService.cs
public async Task HandleBridgeMessage(BridgeMessage msg)
{
    // 1. 权限校验
    if (!script.HasGrant(msg.Api))
    {
        PostError(msg.Id, $"API {msg.Api} not granted in @grant");
        return;
    }

    // 2. 路由分发
    object result = msg.Api switch
    {
        "GM_getValue"          => await Storage.GetValueAsync(scriptId, msg.Args),
        "GM_setValue"          => await Storage.SetValueAsync(scriptId, msg.Args),
        "GM_deleteValue"       => await Storage.DeleteValueAsync(scriptId, msg.Args),
        "GM_listValues"        => await Storage.ListValuesAsync(scriptId),
        "GM_xmlhttpRequest"    => await HttpClient.SendAsync(scriptId, msg.Args),
        "GM_addStyle"          => await InjectStyleAsync(msg.Args),
        "GM_addElement"        => await InjectElementAsync(msg.Args),
        "GM_openInTab"         => OpenInTab(msg.Args),
        "GM_notification"      => ShowNotification(msg.Args),
        "GM_getResourceText"   => GetResourceText(scriptId, msg.Args),
        "GM_getResourceURL"    => GetResourceUrl(scriptId, msg.Args),
        "GM_info"              => GetScriptInfo(scriptId),
        "GM_setClipboard"      => SetClipboard(msg.Args),
        _ => throw new UnsupportedApiException(msg.Api)
    };

    // 3. 返回结果
    PostResponse(msg.Id, ok: true, data: result);
}
```

> 注：上方 `msg.Api switch { ... }` 使用 C# 8 switch 表达式。Apage.Core 的 LangVersion 已放开为 `latest`（product-design v0.4 §4.1），netstandard2.0 目标框架下该语法可正常编译；此前 v0.3 锁 C# 7.3 的矛盾已解除。

### 3.3 支持的 GM_* API 清单

#### 第一优先级（v0.1 脚本管理器首版）

| API | 说明 | 实现要点 |
|-----|------|---------|
| `GM_getValue(key, default)` | 读取脚本存储值 | SQLite per-script 键值表 |
| `GM_setValue(key, value)` | 写入脚本存储值 | 同上 |
| `GM_deleteValue(key)` | 删除存储值 | 同上 |
| `GM_listValues()` | 列出所有键 | 同上 |
| `GM_addStyle(css)` | 注入 CSS | `AddScriptToExecuteOnDocumentCreated` 或 DOM 操作 |
| `GM_info` | 脚本元信息对象 | 返回只读对象 |
| `GM_openInTab(url, options)` | 新标签打开 URL | 调用 TabService |
| `GM_setClipboard(data, type)` | 写入剪贴板 | WPF Clipboard API |

#### 第二优先级（v0.2 迭代）

| API | 说明 | 实现要点 |
|-----|------|---------|
| `GM_xmlhttpRequest(details)` | 跨域 HTTP 请求 | C# HttpClient 代理，遵守 `@connect` 白名单 |
| `GM_notification(details)` | 系统通知 | Windows Toast Notification |
| `GM_addElement(tag, attrs)` | 注入 DOM 元素 | DOM 操作注入 |
| `GM_getResourceText(name)` | 读取 @resource 文本 | 本地文件读取 |
| `GM_getResourceURL(name)` | 读取 @resource Data URL | 本地文件 base64 编码 |
| `GM_registerMenuCommand` | 注册脚本菜单项 | WPF 菜单集成 |
| `GM_unregisterMenuCommand` | 注销脚本菜单项 | 同上 |

#### 不实现的 API

| API | 原因 |
|-----|------|
| `GM_download` | 安全风险高，与下载管理器冲突 |
| `GM_log` | 已废弃，使用 `console.log` 替代 |

### 3.4 脚本注入时机

```csharp
// 三种注入时机映射到 WebView2 事件
switch (script.RunAt)
{
    case RunAt.DocumentStart:
        // NavigationStarting 事件中注入，在页面 DOM 构建前执行
        coreWebView2.NavigationStarting += async (s, e) =>
        {
            if (matcher.Matches(e.Uri))
                await InjectScriptsAsync(coreWebView2, RunAt.DocumentStart, e.Uri);
        };
        break;

    case RunAt.DocumentEnd:
        // DOMContentLoaded 后注入，DOM 已就绪但子资源可能未加载完
        coreWebView2.DOMContentLoaded += async (s, e) =>
        {
            if (matcher.Matches(uri))
                await InjectScriptsAsync(coreWebView2, RunAt.DocumentEnd, uri);
        };
        break;

    case RunAt.DocumentIdle:
        // 页面完全加载后注入（window.onload 之后）
        coreWebView2.NavigationCompleted += async (s, e) =>
        {
            if (matcher.Matches(uri))
                await InjectScriptsAsync(coreWebView2, RunAt.DocumentIdle, uri);
        };
        break;
}
```

### 3.5 脚本沙箱与隔离

每个脚本在独立的 IIFE 中执行，通过注入的桥接层与 C# 通信。脚本之间、脚本与页面之间**不共享作用域**：

```javascript
// 注入模板（每个脚本独立包裹）
;(function(__SCRIPT_ID__, __SCRIPT_META__, __STORE__) {
    'use strict';

    // === 桥接层（上方 3.2 的 ScriptBridge 代码）===

    // === GM_* 同步封装 ===
    // 注入时 C# 端已把该脚本的全部存储 KV 序列化内联为 __STORE__，
    // 因此 GM_getValue/GM_listValues 是真同步（直接读内联对象），
    // 不存在「缓存未就绪」窗口——这是相比运行时异步预加载的关键改进。
    function GM_getValue(key, defaultVal) {
        return (key in __STORE__) ? __STORE__[key] : defaultVal;
    }
    function GM_setValue(key, value) {
        __STORE__[key] = value;                           // 先改内联对象（同步可见）
        ScriptBridge.call('GM_setValue', { key, value }); // 再异步回写后端（fire-and-forget）
    }
    function GM_deleteValue(key) {
        delete __STORE__[key];
        ScriptBridge.call('GM_deleteValue', { key });
    }
    function GM_listValues() { return Object.keys(__STORE__); }

    // ... 其他 GM_* 封装（异步 API 如 GM_xmlhttpRequest 保持 Promise 风格）...

    // === 用户脚本主体 ===
    ${scriptCode}
})('${scriptId}', ${scriptMetaJson}, ${scriptStoreJson});
```

**同步 API 的处理**：Tampermonkey 的 `GM_getValue` / `GM_listValues` 是同步的，但 WebMessage 是异步的。解决方案：

**注入时内联存储 KV（决策 #14）**：C# 端在生成注入代码时，已知要注入哪个脚本，直接查询该脚本的全部存储 KV（见 §5.2），序列化为 `__STORE__` 内联进 IIFE 头部。`GM_getValue` 直接读 `__STORE__`（真同步），`GM_setValue` 先改 `__STORE__` 再异步回写后端。

**为什么不用运行时异步预加载缓存**：预加载方案在 `document-start` 阶段（脚本最需要早执行、也最需要读配置的时刻）缓存最可能尚未就绪，此时只能返回 `defaultVal`——这不是「警告级」问题，而是脚本误以为从未存过配置、逻辑静默跑偏的错误行为。内联注入彻底消除这个窗口，代价是注入前多一次按 script_id 的批量读（一次 SQLite 查询，<1ms）。

---

## 四、URL 匹配引擎

### 4.1 @match 语法（Chrome Match Pattern）

```
<url-pattern> := <scheme>://<host><path>
<scheme>      := '*' | 'http' | 'https' | 'file' | 'ftp'
<host>        := '*' | '*.' <any char except '/' and '*'> | <any char except '/' and '*'>
<path>        := '/' <any chars>
```

示例：

| 模式 | 匹配 | 不匹配 |
|------|------|--------|
| `https://*.example.com/*` | `https://www.example.com/page` | `http://www.example.com/page` |
| `*://example.com/*` | `https://example.com/` | `https://sub.example.com/` |
| `https://example.com/path*` | `https://example.com/path/to/page` | `https://example.com/other` |

### 4.2 @include / @exclude（Tampermonkey 扩展语法）

支持正则表达式（以 `/` 包裹）和通配符 `*`：

```
@include  https://*.example.com/*        // 通配符
@include  /^https?://.*\.example\.com/   // 正则
@exclude  https://example.com/admin*     // 排除
```

### 4.3 匹配优先级

```
if (any @exclude_match matches) → 不注入
if (any @exclude matches)       → 不注入
if (any @match matches)         → 注入
if (any @include matches)       → 注入
else                            → 不注入
```

### 4.4 匹配引擎实现

```csharp
public class ScriptMatcher
{
    private readonly List<CompiledPattern> _matchPatterns;
    private readonly List<CompiledPattern> _includePatterns;
    private readonly List<CompiledPattern> _excludePatterns;
    private readonly List<CompiledPattern> _excludeMatchPatterns;

    public bool Matches(Uri uri)
    {
        // 排除优先
        if (_excludeMatchPatterns.Any(p => p.IsMatch(uri))) return false;
        if (_excludePatterns.Any(p => p.IsMatch(uri))) return false;

        // 匹配
        if (_matchPatterns.Any(p => p.IsMatch(uri))) return true;
        if (_includePatterns.Any(p => p.IsMatch(uri))) return true;

        return false;
    }
}
```

**性能策略**：
- `@match` 模式在脚本加载时预编译为正则
- `@include` 的正则同样预编译，并设置 `MatchTimeout = 100ms`（与广告拦截引擎一致，防 ReDoS）
- 每次页面导航时，遍历所有启用脚本的匹配器，O(N) 复杂度（N = 启用脚本数）
- 脚本数量预期 <100，匹配性能不是瓶颈

---

## 五、脚本存储

### 5.1 文件结构

```
data/
├── scripts/
│   ├── scripts.db              # SQLite：脚本索引 + 元数据 + 启停状态 + 存储数据
│   ├── installed/              # 脚本源文件（.user.js）
│   │   ├── {scriptId}.user.js
│   │   └── ...
│   ├── requires/               # @require 依赖库（本地缓存）
│   │   ├── jquery-3.7.1.min.js
│   │   └── ...
│   └── resources/              # @resource 资源文件（本地缓存）
│       └── ...
```

### 5.2 SQLite 表结构

```sql
-- 脚本注册表
CREATE TABLE scripts (
    id              TEXT PRIMARY KEY,          -- 由 @namespace + @name 生成的 SHA256 前 16 位
    name            TEXT NOT NULL,             -- @name
    namespace       TEXT DEFAULT '',           -- @namespace
    version         TEXT DEFAULT '',           -- @version
    description     TEXT DEFAULT '',           -- @description
    author          TEXT DEFAULT '',           -- @author
    icon            TEXT DEFAULT '',           -- @icon（本地路径或 Data URL）
    enabled         INTEGER DEFAULT 1,         -- 启用/禁用
    run_at          TEXT DEFAULT 'document-end',
    noframes        INTEGER DEFAULT 0,
    file_path       TEXT NOT NULL,             -- installed/ 下的文件路径
    installed_at    INTEGER NOT NULL,          -- Unix 时间戳
    updated_at      INTEGER NOT NULL,
    metadata_hash   TEXT NOT NULL              -- 元数据块的 SHA256，用于检测篡改
);

-- @match / @include / @exclude 规则表
CREATE TABLE script_patterns (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    script_id   TEXT NOT NULL REFERENCES scripts(id) ON DELETE CASCADE,
    type        TEXT NOT NULL,                 -- 'match' | 'include' | 'exclude' | 'exclude_match'
    pattern     TEXT NOT NULL
);
CREATE INDEX idx_script_patterns_script ON script_patterns(script_id);

-- @grant 权限表
CREATE TABLE script_grants (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    script_id   TEXT NOT NULL REFERENCES scripts(id) ON DELETE CASCADE,
    api         TEXT NOT NULL                  -- 'GM_getValue' | 'GM_setValue' | ...
);
CREATE INDEX idx_script_grants_script ON script_grants(script_id);

-- @require 依赖表
CREATE TABLE script_requires (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    script_id   TEXT NOT NULL REFERENCES scripts(id) ON DELETE CASCADE,
    name        TEXT NOT NULL,                 -- 依赖标识名
    file_path   TEXT NOT NULL                  -- requires/ 下的本地文件路径
);

-- @resource 资源表
CREATE TABLE script_resources (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    script_id   TEXT NOT NULL REFERENCES scripts(id) ON DELETE CASCADE,
    name        TEXT NOT NULL,                 -- 资源标识名
    file_path   TEXT NOT NULL                  -- resources/ 下的本地文件路径
);

-- @connect 域名白名单
CREATE TABLE script_connects (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    script_id   TEXT NOT NULL REFERENCES scripts(id) ON DELETE CASCADE,
    domain      TEXT NOT NULL
);

-- 脚本键值存储（GM_getValue / GM_setValue）
CREATE TABLE script_storage (
    script_id   TEXT NOT NULL REFERENCES scripts(id) ON DELETE CASCADE,
    key         TEXT NOT NULL,
    value       TEXT,                          -- JSON 序列化
    PRIMARY KEY (script_id, key)
);
```

> **注入时的批量读取路径（配合决策 #14 内联注入）**：脚本注入前，C# 端按 `script_id` 一次性 `SELECT key, value FROM script_storage WHERE script_id = ?`，把结果反序列化为字典并 JSON 序列化为 `__STORE__` 内联进注入模板（见 §3.5）。这样 `GM_getValue` 在 JS 侧是真同步读，无需运行时异步往返。1000 行量级查询 <1ms（见 §9.3）。

### 5.3 脚本 ID 生成

```csharp
public static string GenerateScriptId(string namespace_, string name)
{
    var input = $"{namespace_}/{name}";
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
    return Convert.ToHexString(hash)[..16].ToLowerInvariant();
}
```

同 `@namespace` + `@name` 的脚本视为同一脚本（重复导入时覆盖更新）。

---

## 六、脚本管理 UI

### 6.1 脚本列表页

```
┌────────────────────────────────────────────────────────────────────┐
│  脚本管理器                                        [+ 导入脚本]     │
├────────────────────────────────────────────────────────────────────┤
│                                                                    │
│  ┌──────────────────────────────────────────────────────────────┐  │
│  │ 🟢 知乎排版优化                          v2.1.0   [编辑][禁用]│  │
│  │    优化知乎阅读体验，移除广告卡片                              │  │
│  │    匹配: zhihu.com · 权限: GM_addStyle, GM_getValue          │  │
│  │    存储: 3 项 · 安装于 2026-07-10                            │  │
│  └──────────────────────────────────────────────────────────────┘  │
│                                                                    │
│  ┌──────────────────────────────────────────────────────────────┐  │
│  │ 🟢 B站播放器增强                         v1.3.2   [编辑][禁用]│  │
│  │    自动宽屏、倍速记忆、弹幕过滤                                │  │
│  │    匹配: bilibili.com · 权限: GM_setValue, GM_xmlhttpRequest │  │
│  │    存储: 12 项 · 安装于 2026-07-05                           │  │
│  └──────────────────────────────────────────────────────────────┘  │
│                                                                    │
│  ┌──────────────────────────────────────────────────────────────┐  │
│  │ ⚪ GitHub 暗色补丁                         v1.0.0  [编辑][启用]│  │
│  │    修复 GitHub 暗色模式的几个 Bug                              │  │
│  │    匹配: github.com · 权限: GM_addStyle                      │  │
│  │    存储: 0 项 · 安装于 2026-06-20                            │  │
│  └──────────────────────────────────────────────────────────────┘  │
│                                                                    │
└────────────────────────────────────────────────────────────────────┘
```

### 6.2 脚本编辑器

```
┌────────────────────────────────────────────────────────────────────┐
│  编辑: 知乎排版优化 v2.1.0              [保存] [重置] [删除脚本]    │
├──────────────────────────────┬─────────────────────────────────────┤
│  基本信息                     │  代码编辑                            │
│                              │                                     │
│  名称: 知乎排版优化           │  // ==UserScript==                  │
│  版本: 2.1.0                 │  // @name         知乎排版优化       │
│  作者: user                  │  // @match        *://*.zhihu.com/*  │
│                              │  // @grant        GM_addStyle        │
│  匹配规则:                   │  // @run-at       document-end       │
│  ☑ *://*.zhihu.com/*        │  // ==/UserScript==                  │
│                              │                                     │
│  权限声明:                   │  GM_addStyle(`                       │
│  ☑ GM_addStyle              │    .Banner, .AdblockBanner {         │
│  ☑ GM_getValue              │      display: none !important;       │
│                              │    }                                 │
│  运行时机:                   │  `);                                 │
│  ○ document-start           │                                     │
│  ● document-end             │  // ...                              │
│  ○ document-idle            │                                     │
│                              │                                     │
│  ☐ 仅在顶层窗口执行          │                                     │
│                              │                                     │
│  存储数据 (3 项):            │                                     │
│  ├ fontSize: "16px"         │                                     │
│  ├ hideBanner: true         │                                     │
│  └ theme: "dark"            │                                     │
│  [清空存储]                  │                                     │
└──────────────────────────────┴─────────────────────────────────────┘
```

### 6.3 编辑器实现

WPF 端使用轻量代码编辑器控件：

| 方案 | 优劣 |
|------|------|
| **AvalonEdit**（推荐） | 开源、轻量、支持 JS 语法高亮、代码折叠、行号，NuGet 包 ~2MB |
| ICSharpCode.TextEditor | 老旧，不再维护 |
| 自绘 TextBox | 工作量大，效果差 |

AvalonEdit 是 .NET 生态中最适合的轻量代码编辑器，不增加显著体积（~2MB 在 <5MB 目标中需权衡，见下方「体积影响」）。

#### 体积影响评估

| 组件 | 体积增量 | 影响 |
|------|---------|------|
| AvalonEdit NuGet | ~2MB | 超出 <5MB 目标 |
| 替代方案：WPF TextBox + 基础行号 | 0MB | 功能有限但够用 |

**建议**：v0.1 使用 WPF TextBox + 等宽字体 + 行号装饰（自绘），v0.2 再评估是否引入 AvalonEdit。或者将编辑器做成分步下载——首次打开编辑器时提示下载 AvalonEdit DLL 到 `data/plugins/`。

---

## 七、脚本导入流程

### 7.1 导入方式

| 方式 | 说明 |
|------|------|
| 文件拖拽 | 拖 `.user.js` 文件到管理界面或浏览器窗口 |
| 文件选择器 | 管理界面「导入脚本」按钮，选择 `.user.js` 文件 |
| 右键菜单 | 网页中右键 → 「安装此脚本」（如果页面包含用户脚本代码） |

### 7.2 导入校验流程

```
用户选择 .user.js 文件
  │
  ├─ 1. 读取文件内容，UTF-8 解码
  │
  ├─ 2. 解析元数据块（正则匹配 // ==UserScript== ... // ==/UserScript==）
  │     └─ 解析失败 → 提示「未找到有效的元数据块」
  │
  ├─ 3. 校验必填字段
  │     ├─ @name 缺失 → 提示「脚本缺少 @name」
  │     └─ @match / @include 全缺失 → 提示「脚本缺少匹配规则」
  │
  ├─ 4. 安全审查
  │     ├─ 检查 @grant 列表，生成权限摘要
  │     └─ 检查是否声明高危 API（GM_xmlhttpRequest、GM_setClipboard）
  │
  ├─ 5. 显示安装确认对话框
  │     │
  │     │  ┌──────────────────────────────────────┐
  │     │  │  安装脚本: 知乎排版优化 v2.1.0        │
  │     │  │                                      │
  │     │  │  作者: user                          │
  │     │  │  描述: 优化知乎阅读体验               │
  │     │  │                                      │
  │     │  │  此脚本将在以下网站运行:              │
  │     │  │  • *://*.zhihu.com/*                 │
  │     │  │                                      │
  │     │  │  此脚本请求以下权限:                  │
  │     │  │  • 修改页面样式 (GM_addStyle)         │
  │     │  │  • 读写本地数据 (GM_getValue)         │
  │     │  │                                      │
  │     │  │         [取消]        [安装]           │
  │     │  └──────────────────────────────────────┘
  │     │
  │     └─ 用户取消 → 中止
  │
  ├─ 6. 复制文件到 data/scripts/installed/{scriptId}.user.js
  │
  ├─ 7. 写入 SQLite 索引（scripts 表 + 关联表）
  │
  ├─ 8. 处理 @require（如有）
  │     └─ 仅支持本地文件：提示用户手动放置依赖文件到 requires/
  │
  └─ 9. 安装完成，提示成功，脚本立即生效
```

### 7.3 @require 依赖处理

由于零网络原则，`@require` 不支持远程 URL 自动下载。处理策略：

1. 如果 `@require` 是本地路径（相对路径），直接从脚本同目录读取
2. 如果是 URL，在导入时提示用户：「此脚本依赖外部库 xxx，请手动下载到 `data/scripts/requires/` 目录」
3. 管理界面显示缺失依赖警告

---

## 八、安全模型

### 8.1 权限声明制

脚本**只能调用在 `@grant` 中声明的 GM_* API**。未声明的 API 调用会被 ScriptBridgeService 拒绝，并在浏览器控制台输出警告：

```
[Apage] Script "知乎排版优化" attempted to call GM_xmlhttpRequest
        but this API is not declared in @grant. Call rejected.
```

### 8.2 GM_xmlhttpRequest 安全约束

这是最高危的 API（可绕过 CORS 发起任意 HTTP 请求），需要额外约束：

| 约束 | 说明 |
|------|------|
| `@connect` 白名单 | 只允许请求 `@connect` 声明的域名，未声明的域名请求被拒绝 |
| 禁止本地文件 | 禁止 `file://` 协议请求 |
| 禁止内网地址 | 默认拒绝 `127.0.0.1`、`192.168.x.x`、`10.x.x.x` 等内网地址（防止 SSRF） |
| 超时限制 | 单次请求超时 30 秒 |
| 响应体积限制 | 单次响应体 ≤ 50MB |

### 8.3 脚本存储隔离

每个脚本的 `GM_getValue` / `GM_setValue` 操作被限制在自己的 `script_id` 命名空间内。脚本 A 无法读写脚本 B 的存储数据（通过 SQLite 表的 `script_id` 字段强制隔离）。

### 8.4 脚本代码不可修改页面全局变量

注入的 IIFE 使用 `'use strict'` 模式，且 GM_* 函数通过桥接层实现而非直接注入全局作用域。脚本无法覆盖页面的 `window` 属性（除非显式通过 `unsafeWindow`，此为 TM 兼容行为，需 `@grant unsafeWindow`）。

### 8.5 威胁模型

| 威胁 | 防御 | 不防御 |
|------|------|--------|
| 恶意脚本窃取数据 | @grant 权限声明、存储隔离、@connect 白名单 | 脚本自身代码逻辑漏洞 |
| 脚本间数据泄露 | script_id 命名空间隔离 | — |
| 脚本通过 GM_xmlhttpRequest 做 SSRF | 内网地址禁止、@connect 白名单 | — |
| 篡改已安装脚本 | metadata_hash 校验，启动时比对文件 SHA256 | 用户自己编辑的脚本 |
| 供应链攻击（恶意 .user.js 导入） | 安装确认对话框显示权限和匹配范围 | 用户主动安装恶意脚本 |

---

## 九、性能考量

### 9.1 脚本匹配开销

每次页面导航需遍历所有启用脚本的匹配规则。假设 50 个启用脚本、每个平均 3 条匹配规则：

- 150 次正则匹配，每次 <0.1ms → 总计 <15ms
- 可接受，不影响页面加载体验

### 9.2 脚本注入开销

- `AddScriptToExecuteOnDocumentCreatedAsync` 在 WebView2 内部执行，无额外进程开销
- 每个脚本的桥接层代码约 2KB，50 个脚本 ≈ 100KB 注入量
- 建议：合并同一页面的所有脚本为一次注入调用，减少 WebView2 IPC 次数

### 9.3 存储性能

- SQLite 单表键值查询，50 个脚本 × 平均 20 个存储项 = 1000 行，查询 <1ms
- `GM_setValue` 使用 WAL 模式（与主数据库分离），写操作不阻塞读操作

---

## 十、路线图集成

### Phase 7 拆分建议

将原路线图 Phase 7+ 中的「用户脚本」拆分为三个子阶段：

#### Phase 7a：运行时核心（1.5 周）

> **最小内核（可提前至 v0.1）**：脚本导入 + ScriptMatcher URL 匹配 + 基础 GM_（getValue/setValue/addStyle）。条件成熟时前移，让首发即带可见的脚本能力（见 roadmap Phase 7）。

- [ ] ScriptBridgeService 实现（WebMessage 桥接层）
- [ ] ScriptMatcher URL 匹配引擎（@match + @include + @exclude）
- [ ] 脚本注入管线（document-start / document-end / document-idle）
- [ ] 第一优先级 GM_* API（GM_get/set/delete/listValue、GM_addStyle、GM_info、GM_openInTab）
- [ ] 内联注入方案（GM_getValue 同步语义：注入时把脚本存储 KV 内联为 __STORE__，见 §3.5、决策 #14）

**交付物**：用户脚本可执行，核心 GM_* API 可用

#### Phase 7b：管理器 UI + 存储（1 周）

- [ ] SQLite 脚本表初始化与迁移
- [ ] 脚本导入流程（文件拖拽 + 文件选择器 + 元数据解析 + 安装确认）
- [ ] 脚本列表页（启用/禁用/删除/信息展示）
- [ ] 脚本编辑器（WPF TextBox 基础版 + 行号）
- [ ] 脚本存储管理（查看/清空存储数据）

**交付物**：完整的脚本导入、管理、编辑体验

#### Phase 7c：高级 API + 打磨（1 周）

- [ ] 第二优先级 GM_* API（GM_xmlhttpRequest、GM_notification、GM_registerMenuCommand 等）
- [ ] @require / @resource 本地依赖管理
- [ ] @connect 域名白名单校验
- [ ] metadata_hash 完整性校验
- [ ] 脚本执行错误捕获与控制台输出

**交付物**：GM_* API 全覆盖，安全模型完整

---

## 附录 A：与 Tampermonkey 的行为差异

| 行为 | Tampermonkey | Apage |
|------|-------------|-------|
| `@updateURL` / `@downloadURL` | 自动检查更新 | 忽略，管理界面提示「Apage 不支持远程更新」 |
| `@require` 远程 URL | 自动下载 | 不支持，需手动放置本地文件 |
| `GM_download` | 支持 | 不支持（安全风险） |
| 脚本更新方式 | 远程拉取 | 用户手动重新导入 .user.js 文件 |
| 脚本商店 | 内建 Greasy Fork 搜索 | 无，用户自行从外部获取脚本 |

## 附录 B：项目结构变更

在 product-design.md §4.2 项目结构中新增：

```
Apage/
├── ...
├── Services/
│   ├── ...（原有服务不变）
│   ├── ScriptManager/
│   │   ├── ScriptManagerService.cs      # 脚本生命周期管理
│   │   ├── ScriptBridgeService.cs       # WebMessage 桥接 + 权限校验
│   │   ├── ScriptMatcher.cs             # URL 匹配引擎
│   │   ├── ScriptMetadataParser.cs      # 元数据块解析
│   │   ├── ScriptStorage.cs             # SQLite 脚本存储
│   │   └── ScriptHttpClient.cs          # GM_xmlhttpRequest 代理
│   └── ...
├── ViewModels/
│   ├── ...（原有 ViewModel 不变）
│   ├── ScriptManagerViewModel.cs        # 脚本列表页 VM
│   └── ScriptEditorViewModel.cs         # 脚本编辑器 VM
├── Views/
│   ├── ...（原有视图不变）
│   ├── ScriptManagerWindow.xaml         # 脚本列表页
│   └── ScriptEditorWindow.xaml          # 脚本编辑器
└── ...
```

## 附录 C：决策记录补充

| # | 决策 | 选择 | 理由 |
|---|------|------|------|
| 11 | 脚本格式 | Tampermonkey 兼容 | 社区通用，可直接导入 Greasy Fork 脚本 |
| 12 | 脚本更新 | 仅本地导入 | 与零网络原则一致；远程更新可作 Phase 7+ 可选功能 |
| 13 | 编辑器 | v0.1 自绘 TextBox，v0.2 评估 AvalonEdit | 先保住 <5MB 体积目标 |
| 14 | GM_getValue 同步语义 | 注入时内联存储 KV | TM 兼容要求同步，WebMessage 是异步；注入时把该脚本全部 KV 内联进 IIFE（`__STORE__`），GM_getValue 真同步读，消除 document-start 缓存未就绪窗口 |
| 15 | @require 远程依赖 | 不支持，手动放置 | 零网络原则；提示用户手动下载 |
