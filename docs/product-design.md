# Apage 产品设计文档

> 内部文档 | 版本：v0.4 | 更新：2026-07-19
>
> v0.4 变更（回退双线路，单押便携版）：
> - 双线路架构暂缓：Apage.Desktop 降级为「PMF 验证后可选项」，不再是 Phase 8 既定交付；v0.1~v1.0 单押便携版（Apage.Portable）
> - 解除 Apage.Core 的 C# 7.3 死锁：目标框架保持 .NET Standard 2.0，但 LangVersion 放开为 latest（消除 §4.4 / script-manager §3.2 示例用 C# 8 switch 表达式的自相矛盾）
> - WebView2 Runtime 前提诚实化：R1 升为最高优先级，即插即用明确前提为「目标机已装 WebView2 Runtime」，不做静默联网下载
> - 体积口径建立在实测上：Phase 0 新增体积预算表 spike（新增风险 R12）
> - GM_getValue 同步语义改为「注入时内联存储 KV」，消除 document-start 缓存未就绪窗口
> - 差异化温和前置：隐私统计卡片随 v0.1 首发；脚本管理器抽出最小内核标注可提前
>
> v0.3 变更（双线路架构决策）：
> - 运行时分发由「单一框架依赖 + 缺失检测引导」改为「双线路」：便携版 .NET Framework 4.8 + 桌面版 .NET 8 自包含
> - 新增共享代码层 Apage.Core（.NET Standard 2.0），~90% 代码共享
> - 风险 R2 重写：从「缺失检测引导下载」改为「双线路规避 + 便携版优先发布」
> - 性能目标分便携版/桌面版两列（见 4.5）
> - 发布策略：便携版优先（v0.1 MVP），桌面版作为 Phase 7+ 衍生产品
>
> v0.2 变更（架构评审修订）：
> - 通信方案 WebSocket → WebMessage（原因见 4.4）
> - Chrome 扩展口径修正为「部分 MV3 扩展（侧载）」，用户脚本升级为一级差异化
> - WebRTC 一刀切阻断 → IP 泄露防护（不断连会议类网站）
> - 崩溃守护伪代码修正为外部 watchdog 进程方案
> - 新增「5.6 基础体验清单」「七、风险与应对」

---

## 一、产品定位

### 一句话定义

**Apage 是一款超轻量、隐私优先、全本地化的 Windows 便携浏览器。**

### 目标用户

- 对隐私敏感的技术用户
- 需要便携浏览器的移动办公人群（U盘随身带）
- 受不了 Chrome/Edge 臃肿的极简主义者
- 需要一台电脑多个独立浏览器环境的开发者

### 核心特征

| 特征 | 描述 |
|------|------|
| 极致轻量 | 单文件 EXE，<5MB，冷启动 <1秒（窗口可见） |
| 隐私默认全开 | 广告拦截、追踪阻断、Cookie 控制，开箱即用 |
| 全本地化 | 零遥测、零后台请求、零数据收集（仅用户触发的网络请求，清单透明） |
| 无需登录 | 不强制账号，不要求云同步 |
| Portable | 数据跟程序走，U盘即插即用（前提：目标机已装 WebView2 Runtime，Win11 默认预装，见风险 R1） |
| 功能完整 | 用户脚本（GM_* 兼容）、部分 MV3 扩展（侧载）、多标签、下载管理 |
| 无广告 | 浏览器本身无任何广告、推广、"可接受广告"白名单 |
| 壁纸美学 | 新标签页自定义静态壁纸 + 隐私拦截统计卡片 |

---

## 二、竞品分析

### 2.1 全景图

| 浏览器 | 引擎 | 体积 | 平台 | 隐私 | 广告拦截 | 扩展 | 壁纸 |
|--------|------|------|------|------|---------|------|------|
| **ZZZ** | WebView2 | ~5MB | Win | ★★★★★ | ABP全量 | 用户脚本 | ❌ |
| **Helium** | Chromium去谷歌 | ~109MB | 全平台 | ★★★★★ | 内置(无偏见) | Chrome扩展 | ❌ |
| **Min** | Electron | ~60MB | 全平台 | ★★★★ | 内置基础 | ❌ | ❌ |
| **Brave** | Chromium | ~200MB | 全平台 | ★★★★ | Shields | Chrome扩展 | ❌ |
| **Vivaldi** | Chromium | ~150MB | 全平台 | ★★★ | 需手动配 | Chrome扩展 | ❌ |
| **Falkon** | QtWebEngine | ~30MB | Linux/Win | ★★★ | 内置基础 | ❌ | ❌ |
| **星愿** | Chromium | ~80MB | Win | ★★ | 内置 | 部分Chrome | ✅ |
| **百分** | Chromium | ~50MB | Win | ★★★ | 需扩展 | Chrome扩展 | ❌ |

### 2.2 核心竞品拆解

#### ZZZ（最核心参考）

- 仓库：https://github.com/zengjiangy/ZZZ
- 技术栈：C# + .NET Framework 4.8 + WPF + WebView2
- 哲学：不造轮子，只造壳
- 体积：~5MB 单 EXE
- **关键观察**：ZZZ 的 5MB 零依赖即插即用，建立在 .NET Framework 4.8 是 Win10/11 系统自带组件这一事实上。Apage 选择 .NET 8 则必须直面运行时分发问题（见风险 R2）。

**值得借鉴**：
1. 中心化服务容器 `AppServices`（19 个服务的聚合根）
2. 懒加载策略（冷启动只加载设置+书签+壁纸，历史/脚本/广告规则后台加载）
3. ABP 规则引擎 + 子串索引（50 万条规则无压力）
4. 隐私标签独立 Profile + 禁用缓存
5. PrivateDataGuard 崩溃清理（marker token + **独立 watchdog 进程**）
6. 标签休眠机制（非活跃标签释放内存）
7. WebRTC 阻断、地理位置模拟
8. Edge 翻译 API（免费、无 Key、DOM 就地翻译）
9. DPAPI 数据加密

**局限**：
- 仅 Windows（.NET Framework 4.8 绑定）
- 无壁纸/主题自定义
- 无 Chrome 扩展支持
- HostObject 同步通信（潜在卡顿）

#### Helium（隐私标杆）

- 官网：https://helium.computer
- 技术栈：基于 ungoogled Chromium，去除所有 Google 服务

**核心卖点**：
1. 零后台请求 — 首次启动零网络请求，所有服务请求必须用户确认
2. 无偏见广告拦截 — 不设"可接受广告"白名单
3. !Bangs 快捷跳转 — 内置 13000+ 指令，本地处理
4. 匿名化扩展商店 — Chrome 扩展请求通过 Helium 服务匿名代理
5. 指纹噪声 — 对 Web API 添加噪声干扰指纹追踪
6. 完全开源 — 浏览器 + 在线服务全部开源

**局限**：体积 ~109MB，无 DRM（不能看 Netflix），无壁纸自定义

#### Min（极简典范）

- 仓库：https://github.com/minbrowser/min
- 技术栈：Electron + JavaScript

**性能数据**：

| 指标 | Min | Chrome | Firefox |
|------|-----|--------|---------|
| 冷启动 | 0.8s | 1.5s | 1.2s |
| 空白标签 | 45MB | 78MB | 65MB |
| 百度首页 | 128MB | 195MB | 172MB |
| YouTube | 210MB | 342MB | 298MB |

**局限**：Electron 偏重（~60MB），不支持扩展，JS 性能弱

### 2.3 竞品缺失（Apage 机会）

| 功能 | ZZZ | Helium | Min | Brave | Apage |
|------|-----|--------|-----|-------|-------|
| <5MB 体积 | ✅ | ❌ | ❌ | ❌ | ✅ |
| 壁纸新标签页 | ❌ | ❌ | ❌ | ❌ | ✅ |
| Portable 即插即用 | ✅ | ❌ | ❌ | ❌ | ✅（前提：已装 WebView2 Runtime） |
| Chrome 扩展 | ❌ | ✅ | ❌ | ✅ | 部分（MV3 侧载） |
| 用户脚本（GM_*） | ✅ | ❌ | ❌ | ❌ | ✅ |
| 无偏见拦截 | ✅ | ✅ | ✅ | ❌ | ✅ |
| 崩溃隐私守护 | ✅ | ❌ | ❌ | ❌ | ✅ |
| 网页翻译 | ✅ | ❌ | ❌ | ❌ | 后期 |
| 异步无阻塞通信 | ❌ | N/A | N/A | N/A | ✅（WebMessage） |

---

## 三、用户痛点

### 痛点 1：体积和启动速度

> "Chrome 2GB，我就想看个网页，为什么要装一个操作系统？"

- 用户对冷启动 >2 秒已零容忍
- Min 0.8 秒冷启动被反复称赞，ZZZ 5MB 单文件是极致标杆

**目标**：冷启动 <1 秒（窗口可见，WebView2 后台预热），体积 <5MB

### 痛点 2：隐私"假保护"

> "Brave 说保护隐私，结果自己塞了加密货币钱包和 BAT 广告"

- 商业化污染了隐私产品的信任
- Helium 的"零后台请求、零数据收集"才是用户真正想要的

**原则**：不遥测、不检查更新（仅手动触发）；所有网络请求仅限用户触发，并在文档中列出完整清单

### 痛点 3：广告拦截的"偏见"

> "Brave Shields 会放过自己的广告合作伙伴"

- 用户对"可接受广告"白名单极度反感
- Helium 的"无偏见拦截"获得好评

**原则**：不设白名单，所有广告一视同仁，用户规则优先级最高

### 痛点 4：WebView2 的坑

> "WebView2 的 HostObject 是同步阻塞的，一调就卡 UI"

| 痛点 | 描述 |
|------|------|
| 同步阻塞 | HostObject：JS 调 C# 阻塞渲染线程 |
| 异常连锁 | 子进程崩溃拖垮主进程 |
| 内核绑定 | 换引擎成本极高 |

**方案**：JS↔C# 通信走 **WebMessage**（`chrome.webview.postMessage`）——WebView2 原生异步通道，而非同步 HostObject。不采用 WebSocket，原因见 4.4。

### 痛点 5：小众浏览器"功能残废"

> "Min 浏览器连扩展都没有，装个密码管理器都不行"

- 过度精简导致用户无法安装必要扩展
- Helium 做法最好：支持 Chrome 扩展 + 匿名化商店请求

**要求**：
- 必须支持**用户脚本（Tampermonkey 兼容 GM_\* API）**——这是 Apage 的一级差异化能力
- Chrome 扩展量力而行：WebView2 的扩展支持目前仅限 **MV3 子集 + 本地侧载**，API 覆盖不全，无商店直连。宣传口径为「支持部分 MV3 扩展」，不做全量承诺（实测验证见路线图 Phase 0）

### 痛点 6：没有差异化记忆点

> "百分浏览器和 Chrome 有什么区别？体积小了点？"

- 星愿的差异化太窄（主页+漫画），Vivaldi 的太复杂（什么都能定制）

**Apage 的标签**：壁纸美学 + 隐私统计 + Portable 即插即用

> 注：单纯的壁纸容易被复制。更强的记忆点是新标签页上的**隐私拦截统计**（"本周已拦截 N 条追踪 / M 个广告"）——Brave 靠这个小卡片建立了极强的用户感知，且数据全本地，与零联网原则不冲突。壁纸负责美，拦截统计负责价值感。

---

## 四、技术架构

### 4.1 技术栈

单押便携版：Apage.Portable 为唯一发布线；Apage.Core 保留分层以备未来衍生。桌面版（Apage.Desktop）暂缓，作为 PMF 验证后的可选项（见风险 R2、Phase 8）。

| 层级 | 便携版（Apage.Portable，主力） | 选择理由 |
|------|------------------------------|---------|
| 运行时 | .NET Framework 4.8 | 利用 Win10/11 系统自带运行时实现真即插即用；ZZZ 已验证，踩坑少 |
| 运行时分发 | 框架依赖（系统自带） | 保 <5MB 单文件；真正的运行时依赖是 WebView2 Runtime（见 R1），非 .NET |
| 共享层 | Apage.Core（.NET Standard 2.0） | Services/ViewModels/Models 分层，纯逻辑可单测；为未来桌面版衍生预留 |
| UI | WPF（.NET Framework 4.8） | 比 WinUI 3 更成熟，XAML 灵活 |
| MVVM | CommunityToolkit.Mvvm | 轻量、源生成器、ZZZ 验证 |
| 引擎 | WebView2 | 系统自带 Chromium（常青版），零体积 |
| 通信 | WebMessage（chrome.webview.postMessage） | WebView2 原生异步通道，无端口、无鉴权问题 |
| 存储 | SQLite（Microsoft.Data.Sqlite） | 单文件、查询高效、portable 友好 |
| 打包 | PublishSingleFile（框架依赖） | 原生单 EXE |
| JSON | System.Text.Json | 高性能 |

> **共享层语言版本**：Apage.Core 目标框架为 .NET Standard 2.0（保证未来桌面版 .NET 8 与便携版 .NET FX 4.8 都能引用）。**LangVersion 放开为 `latest`**——.NET Standard 2.0 目标框架仍可使用 C# 8+ 的多数语法糖（`switch` 表达式、`using` 声明、模式匹配等，编译器负责降级）；`record`/`init` 仅需补一个 `IsExternalInit` polyfill；只需避免依赖运行时的新 API。此前 v0.3 锁定 `7.3` 导致 §4.4、script-manager §3.2 示例的 `switch` 表达式自相矛盾，v0.4 已修正。

### 4.2 项目结构

解决方案：一个共享类库 + 便携版 UI + 一个守护进程；桌面版 UI 目录预留但暂缓（见 Phase 8）。

```
Apage/
├── Apage.sln
│
├── Apage.Core/                       # 共享层（.NET Standard 2.0，~90% 代码）
│   ├── Apage.Core.csproj
│   ├── Configuration/
│   │   ├── AppPaths.cs
│   │   └── Settings.cs
│   ├── Models/
│   │   ├── Bookmark.cs
│   │   ├── HistoryEntry.cs
│   │   ├── AdBlockModels.cs
│   │   └── TabInfo.cs
│   ├── ViewModels/
│   │   ├── MainViewModel.cs
│   │   ├── BrowserTabViewModel.cs
│   │   ├── NewTabViewModel.cs
│   │   ├── SettingsViewModel.cs
│   │   ├── ScriptManagerViewModel.cs
│   │   └── ScriptEditorViewModel.cs
│   ├── Services/
│   │   ├── AppServices.cs
│   │   ├── BrowserLifecycleService.cs
│   │   ├── TabService.cs
│   │   ├── DataServices.cs
│   │   ├── AdBlockManager.cs
│   │   ├── AdBlockRuleEngine.cs
│   │   ├── PrivacyService.cs
│   │   ├── FaviconCacheService.cs
│   │   ├── SearchSuggestionService.cs
│   │   ├── WallpaperService.cs
│   │   ├── SessionService.cs
│   │   ├── RuleUpdateService.cs
│   │   ├── SingleInstanceService.cs
│   │   ├── NativeDependencyService.cs
│   │   └── ScriptManager/
│   │       ├── ScriptManagerService.cs
│   │       ├── ScriptBridgeService.cs
│   │       ├── ScriptMatcher.cs
│   │       ├── ScriptMetadataParser.cs
│   │       ├── ScriptStorage.cs
│   │       └── ScriptHttpClient.cs
│   └── Resources/
│       └── public_suffix_list.dat
│
├── Apage.Portable/                   # 便携版 UI（.NET Framework 4.8 + WPF）
│   ├── Apage.Portable.csproj
│   ├── App.xaml(.cs)
│   ├── MainWindow.xaml(.cs)
│   ├── Views/
│   │   ├── MainWindow.xaml
│   │   ├── BrowserTab.xaml
│   │   ├── NewTabPage.xaml
│   │   ├── SettingsWindow.xaml
│   │   ├── BookmarkManager.xaml
│   │   ├── DownloadManager.xaml
│   │   ├── ScriptManagerWindow.xaml   # 基础版（TextBox 编辑器）
│   │   └── ScriptEditorWindow.xaml
│   └── Assets/
│       └── icons/
│
├── Apage.Desktop/                    # 桌面版 UI（.NET 8 + WPF）— 未来可选，非既定交付（见 Phase 8）
│   ├── Apage.Desktop.csproj
│   ├── App.xaml(.cs)
│   ├── MainWindow.xaml(.cs)
│   ├── Views/
│   │   └── （同便携版，可共享 ResourceDictionary；编辑器升级为 AvalonEdit）
│   └── Assets/
│       └── icons/
│
├── Apage.Watchdog/                   # 独立守护进程（隐私数据崩溃清理，<100KB 无依赖）
│   └── Program.cs
│
└── docs/
```

> **共享策略**：Apage.Core 包含所有 Services / ViewModels / Models / 业务逻辑，UI 项目只负责视图与平台相关代码（当前仅便携版；未来桌面版衍生时同此策略，ResourceDictionary 可通过文件链接在两个 UI 项目间共享）。ViewModel 绑定逻辑完全在 Core 层，UI 项目只做 XAML 绑定。

### 4.3 服务聚合根

```csharp
public sealed class AppServices : IDisposable
{
    // 核心
    public SettingsService Settings { get; }
    public BookmarkService Bookmarks { get; }
    public HistoryService History { get; }
    public BrowserLifecycleService Browser { get; }
    public TabService Tabs { get; }

    // 功能
    public AdBlockManager AdBlock { get; }
    public PrivacyService Privacy { get; }
    public FaviconCacheService Favicons { get; }
    public DownloadService Downloads { get; }
    public WallpaperService Wallpaper { get; }
    public SessionService Sessions { get; }

    // 懒加载
    public async Task InitializeAsync()
    {
        await Settings.LoadAsync();
        await Task.WhenAll(Bookmarks.LoadAsync(), Wallpaper.LoadAsync());
    }

    public Task EnsureBackgroundAsync() => Task.Run(async () =>
    {
        await Task.WhenAll(History.LoadAsync(), AdBlock.LoadAsync());
    });
}
```

### 4.4 WebMessage 通信（替代 HostObject，不采用 WebSocket）

```
┌──────────────────────┐      WebMessage（进程内通道）     ┌──────────────────┐
│  WebView2 (JS)       │ ◄───────────────────────────────► │  C# Backend      │
│                      │   chrome.webview.postMessage      │                  │
│  用户脚本 / 注入脚本  │   JSON messages                   │  AdBlock Engine  │
│  GM_* API            │   异步、非阻塞                     │  Privacy Guard   │
└──────────────────────┘                                    └──────────────────┘
```

```csharp
// C# 端
coreWebView2.WebMessageReceived += async (s, e) =>
{
    var msg = JsonSerializer.Deserialize<BridgeMessage>(e.WebMessageAsJson);
    var result = await DispatchAsync(msg);          // GM_getValue / GM_setValue / ...
    coreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(
        new { id = msg.Id, ok = true, data = result }));
};
```

```javascript
// JS 端（随 AddScriptToExecuteOnDocumentCreated 注入，天然可用）
let seq = 0; const pending = new Map();
function callBackend(type, payload) {
    return new Promise(resolve => {
        const id = ++seq;
        pending.set(id, resolve);
        window.chrome.webview.postMessage({ id, type, ...payload });
    });
}
window.chrome.webview.addEventListener('message', e => {
    pending.get(e.data.id)?.(e.data.data);
    pending.delete(e.data.id);
});
```

**为什么不采用 WebSocket**（v0.1 草案曾选 WS，评审后否决）：

| 问题 | 说明 |
|------|------|
| HTTPS 混合内容阻断 | HTTPS 页面禁止向 `ws://`（明文）发起连接，用户脚本在绝大多数网站上直接失效；`wss://` 又要求受信任的本地证书，等于让用户装根证书，与隐私定位冲突 |
| 本地端口攻击面 | 任意网页（含恶意网站）都能连接 `ws://127.0.0.1:{port}`，端口可被脚本扫描探测，`GM_*` 等特权 API 必须额外做 token 鉴权 + Origin 校验 |
| HttpListener 权限坑 | .NET 下基于 http.sys 的 WS 服务需要 URLACL 预留或管理员权限，违背便携原则 |

WebMessage 通道只存在于我们自己的 WebView2 进程内：无 TCP 端口、其他进程和网站物理上无法触达，天然免疫上述问题。代价是需要自行维护请求/响应 id 关联（上方代码已涵盖）。

### 4.5 性能目标

| 指标 | 便携版目标（主力） | 参考值 | 备注 |
|------|------------------|--------|------|
| 安装包体积 | <5MB（Phase 0 实测预算，见 R12） | ZZZ ~5MB | 框架依赖单文件；e_sqlite3.dll + 规则快照 + PSL 叠加需实测称重，超支则调口径 |
| 冷启动时间 | <1秒（窗口可见） | Min 0.8秒 | UI 先行 + WebView2 后台预热 |
| 空白标签内存 | <50MB | Min 45MB | 共享 Environment |
| 10 标签内存 | <300MB | Min ~250MB | **隐私标签共享一个隔离 Environment**，否则每标签一套浏览器进程（3+ 进程），目标无法达成 |
| 广告规则容量 | 50万条 | ZZZ 已验证 | Phase 0 benchmark 复核 |
| 运行时依赖 | .NET FX 4.8（系统自带）+ WebView2 Runtime（见 R1） | — | 真即插即用前提是 WebView2 Runtime 已装 |

> 桌面版（暂缓）性能目标：体积 ~50MB 自包含、冷启动 <0.8s（ReadyToRun）。仅在 PMF 验证后启动时适用，见 Phase 8。

---

## 五、功能设计

### 5.1 新标签页

```
┌─────────────────────────────────────────────────┐
│            [高质量壁纸背景]                       │
│            [毛玻璃覆盖层]                         │
│                                                 │
│         ┌───────────────────────┐               │
│         │  🔍  搜索或输入网址     │               │
│         └───────────────────────┘               │
│                                                 │
│    ┌──────┐ ┌──────┐ ┌──────┐ ┌──────┐ ┌────┐ │
│    │Google│ │GitHub│ │ B站  │ │ 知乎 │ │ +  │ │
│    └──────┘ └──────┘ └──────┘ └──────┘ └────┘ │
│                                                 │
│         🛡 本周已拦截 1,283 条追踪 · 342 个广告    │
│                                                 │
│                    14:32                        │
│                  星期六                          │
│               2026年7月19日                      │
│                                 [设置壁纸 🖼️]    │
└─────────────────────────────────────────────────┘
```

- 壁纸：本地图片 / 拖拽设置 / 内置精选
- 毛玻璃搜索框（backdrop-filter: blur）
- 快捷方式可编辑/排序/删除
- **隐私统计卡片（v0.1 首发差异化）**：本周拦截追踪/广告计数（纯本地数据，强化隐私心智；最便宜、感知最强的差异化，确保随 v0.1 首发）
- 时钟 + 日期

### 5.2 隐私保护

#### 默认配置

```csharp
public class PrivacyDefaults
{
    public bool BlockThirdPartyCookies = true;       // 默认开
    public bool SendDoNotTrack = true;               // 默认开（声明性，多数站点忽略）
    public bool SendGlobalPrivacyControl = true;     // 默认开（部分法域有法律效力）
    public bool WebRTCIPProtection = true;           // 默认开：防 IP 泄露，不断连（见下）
    public bool ClearOnExit = false;                 // 可选
    public bool AutoDeleteCookies = false;           // 可选
    public bool OnlineSearchSuggestions = false;     // 默认关：联网建议会把击键发给引擎
}
```

#### 广告拦截规则来源与更新机制

- EasyList
- EasyList China
- CJX's Annoyance List
- EasyPrivacy
- Adblock Warning Removal List

原则：不设"可接受广告"白名单。用户自定义规则优先级最高。

**更新机制**（与"零后台请求"原则对齐）：

1. 安装包内置一份规则快照，保证开箱即用；
2. 设置页提供「立即更新规则」按钮（用户触发的网络请求）；
3. 「启动时自动更新」为可选开关，**默认关闭**，开启时明确提示会产生后台请求；
4. 关于页列出程序可能发起的全部网络请求清单，透明可审计，包括：规则更新（用户触发）、用户主动联网搜索建议（默认关）、**favicon 抓取**（浏览时按需请求站点图标，属浏览衍生请求而非后台静默——需在清单中诚实列出并提供关闭开关）。

#### 三方 Cookie 控制（诚实版）

- 请求时按 Public Suffix List 判断域名归属，剥离第三方请求的 `Cookie` 头；
- 响应侧同步剥离 `Set-Cookie`（需重写响应，有成本，按开关启用）；
- **已知残余泄露面**（文档公开，不粉饰）：第三方脚本的 `document.cookie`、IndexedDB/localStorage 等存储类追踪不在头部手术覆盖范围内，依赖 Chromium 存储分区与 Profile 首选项缓解。优先使用 Chromium 内建能力，头部手术作为补充。

#### 追踪保护头

```csharp
e.Request.Headers.SetHeader("DNT", "1");
e.Request.Headers.SetHeader("Sec-GPC", "1");
```

#### WebRTC IP 泄露防护（不是一刀切阻断）

一刀切禁用 `RTCPeerConnection` 会打死 Google Meet / 网页版微信通话 / Discord 等会议类网站。改为：

```csharp
var options = new CoreWebView2EnvironmentOptions {
    AdditionalBrowserArguments = "--force-webrtc-ip-handling-policy=disable_non_proxied_udp"
};
```

阻止本地/公网 IP 经 WebRTC 泄露，同时保留音视频通话能力；设置中提供按站点开关。

#### 地理位置模拟

劫持 navigator.geolocation，永远拒绝真实位置，可选返回模拟坐标。

#### 隐私标签崩溃守护（外部 watchdog 进程）

进程内 `ProcessExit` + 后台线程的方案在真实崩溃时**不生效**（崩溃不触发 ProcessExit，线程随进程一起死）。采用 ZZZ 同思路的外部守护：

```
主进程                                          Apage.Watchdog.exe（独立进程，<100KB）
  │ 开启隐私标签                                   │
  ├─ 创建隐私目录 + 写入 marker token              │
  ├─ 启动 watchdog（参数：目录、token、主进程 PID） ──►│ 轮询主进程 PID
  │                                                │ 主进程消失（正常退出或崩溃）
  │ 正常退出：自行清理隐私目录，                     │ 校验 marker token
  │ 通知 watchdog 退出                              │ 删除隐私目录后自退
  │ 崩溃：清理未执行 ─────────────────────────────►│ （兜底保障）
```

双保险：主进程下次启动时扫描残留隐私目录 + stale token，直接清理。

### 5.3 广告拦截引擎

#### 规则类型

| 类型 | 示例 |
|------|------|
| 网络拦截 | `\|\|ads.example.com^` |
| 正则拦截 | `/ad[0-9]+\./` |
| 资源过滤 | `$script,domain=example.com` |
| 化妆隐藏 | `##.ad-banner` |
| 例外规则 | `@@\|\|ok.com` |
| badfilter | `\|\|ads.com$badfilter` |

#### 子串索引

规则中的 3-8 字符关键子串作为 key，匹配时扫描 URL 子串而非逐条正则。50 万条规则无压力（Phase 0 benchmark 复核）。

#### 供应链安全（防 ReDoS / 恶意规则）

规则来自第三方列表，视为不可信输入：

- 所有规则编译的正则**统一设置 `MatchTimeout`（≤100ms）**，杜绝灾难性回溯卡死浏览器；
- 单条规则长度上限、单列表条数/体积上限；
- 列表更新后校验格式，损坏则回滚上一版本。

#### 化妆规则安全过滤

只允许纯 CSS 选择器注入 `display:none!important`，拒绝 scriptlet/CSS 注入。

### 5.4 标签页管理

#### 环境隔离

- 普通标签共享一个 WebView2 环境
- **隐私标签共享一个独立隔离 Environment**（独立 Profile + 禁用缓存）——而非每标签一个环境。每个 Environment 对应一套浏览器进程（3+ 进程、近百 MB），每标签独立环境会直接击穿内存目标。

#### 休眠机制

- 非活跃标签降低内存优先级
- 完全释放 WebView2 实例（标签休眠）

#### 事件管道

每个 WebView2 实例注册完整事件链：广告拦截、DNT/GPC 头注入、Cookie 剥离、历史记录、暗黑模式、下载管理等。

#### 关闭流程

先停导航 → 保存会话 → 保存设置 → 清理隐私 → 释放 WebView2

### 5.5 暗黑模式

| 模式 | 实现 | 效果 |
|------|------|------|
| Smart（默认） | `color-scheme: dark` | 轻量，依赖浏览器原生 |
| Force | 亮度重映射 + MutationObserver | 强制所有页面暗色 |
| Native | Edge DevTools Protocol | 最完整，但依赖 Edge |

### 5.6 基础体验清单（易被遗忘但用户会立刻要）

| 功能 | 说明 | 计划 |
|------|------|------|
| 会话恢复 | 启动时恢复上次打开的标签 | Phase 3 |
| 书签导入 | Chrome/Edge HTML 导入（换浏览器的第一道门槛） | Phase 3 |
| 页内查找 | Ctrl+F（WebView2 不自带 UI，需自绘） | Phase 6 |
| 地址栏补全 | 本地历史 + 书签匹配 | Phase 3 |
| 崩溃日志 | 本地记录 + 用户手动导出（零遥测的代价是不能当瞎子） | Phase 6 |
| 设为默认浏览器 | http/https 协议关联 | Phase 7+ |
| 多窗口策略 | v0.1 单窗口多标签，多窗口后置 | Phase 7+ |
| DPI 适配 | PerMonitorV2 清单，4K 屏文字清晰 | Phase 1 |
| 代码签名 | 未签名 EXE 必触发 SmartScreen；开源可申请 SignPath.io 免费签名 | Phase 6 |

### 5.7 网页翻译（后期）

- Edge 翻译 API（免费、无 Key）
- DOM 就地翻译（TreeWalker 收集文本节点）
- 批量翻译（每批 40 条，最多 12000 字符）
- 完美恢复原文

---

## 六、差异化策略

### 竞争定位

```
         轻量 ◄──────────────────────────► 重量
         │
    隐   │  Apage ★        Helium
    私   │  (5MB)          (109MB)
    优   │
    先   │  ZZZ            Brave
         │  (5MB)          (200MB)
         │
    功   │  Min            Vivaldi
    能   │  (60MB)         (150MB)
    优   │
    先   │  百分            Chrome
         │  (50MB)         (200MB)
```

### 卖点总结

| 卖点 | 便携版（主力） | 桌面版（暂缓，未来可选） | 竞品对比 |
|------|--------------|----------------------|---------|
| 单文件体积 | <5MB | ~50MB | 便携版与 ZZZ 同级，Helium 的 1/20 |
| 壁纸 + 隐私统计新标签页 | ✓（v0.1 首发） | ✓ | 无竞品做好 |
| Portable 即插即用 | ✓（.NET FX 4.8 系统自带，前提装 WebView2 Runtime） | — | 便携版与 ZZZ 同级 |
| 隐私默认全开 | ✓ | ✓ | Helium 同级 |
| 无偏见广告拦截 | ✓ | ✓ | Brave 有白名单 |
| WebMessage 异步通信 | ✓ | ✓ | ZZZ 用同步 HostObject |
| 用户脚本（GM_* 兼容） | ✓ | ✓ | Min/Helium 没有 |
| 脚本编辑器 | 基础版（TextBox） | 完整版（AvalonEdit） | 桌面版体验更优 |
| 部分 MV3 扩展（侧载） | ✗ | ✓（未来可选） | 与运行时/WebView2 版本绑定，桌面版唯一实质理由 |
| 网页翻译 | 后期（Edge API 与运行时无关，便携版亦可做） | ✓ | — |

> **定位差异**：v0.1~v1.0 单押便携版，主打"U盘即插即用 + 核心能力 + 隐私统计"。桌面版暂缓——其"独有"卖点中仅「部分 MV3 扩展」与运行时/WebView2 版本强绑定，网页翻译/暗黑增强/AvalonEdit 便携版亦可实现，不足以支撑第二条产品线 + 双 CI/CD 的成本。桌面版待便携版验证 PMF 后再评估（见 Phase 8）。

---

## 七、风险与应对

| # | 风险 | 影响 | 应对 | 验证节点 |
|---|------|------|------|---------|
| R1 | **WebView2 Runtime 缺失**（LTSC/企业精简版/老 Win10）——头号现实风险 | 无法启动 | **诚实标注前提**：README/关于页明确"即插即用依赖目标机已装 WebView2 Runtime（Win11 默认预装，Win10 需 Edge 保持更新）"；启动时检测缺失并给出手动下载指引，**不做静默联网下载**（违背零网络）；不打包 Fixed Version（~180MB 摧毁 <5MB） | Phase 1 |
| R2 | 双线路维护成本 vs 个人项目产能 | 双 CI/CD、双测试拖垮迭代 | **v0.4 决策：暂缓双线路，单押便携版**。便携版借 .NET FX 4.8 系统自带实现真即插即用（.NET 运行时非瓶颈，见 R1）。Apage.Core 保留分层但解除 C# 7.3 死锁（LangVersion=latest）。桌面版降级为 PMF 验证后可选项（Phase 8），非既定交付 | v0.1 发布后复盘 |
| R3 | WebView2 扩展 API 覆盖不足 | "支持 Chrome 扩展"承诺落空 | 宣传口径降为「部分 MV3 扩展（侧载）」；用户脚本作为一级能力 | Phase 0 Spike 3 |
| R4 | 50 万条规则下拦截性能不达标 | 核心卖点受损 | 子串索引 + MatchTimeout；benchmark 不达标则裁剪默认规则集 | Phase 0 Spike 1 |
| R5 | 冷启动 <1s 达不到 | 核心卖点受损 | UI 先行 + WebView2 后台预热 + ReadyToRun；验收口径为「窗口可见 <1s」 | Phase 0 Spike 2 |
| R6 | U盘热拔出 / exFAT 上 SQLite 损坏 | 用户数据丢失 | `journal_mode=DELETE` + `synchronous=FULL` + 启动 `PRAGMA integrity_check` + 自动备份上一版本 | Phase 3 |
| R7 | WebView2 缓存海量小文件写入伤 U盘、拖慢速度 | 便携体验差 | 缓存位置可配置：默认宿主机 `%TEMP%`（数据仍便携），纯便携模式可选；只读 U盘 场景降级运行 | Phase 1 |
| R8 | 规则列表供应链攻击（ReDoS/恶意规则） | 浏览器卡死 | 见 5.3 供应链安全 | Phase 5 |
| R9 | 未签名 EXE 触发 SmartScreen 拦截 | 新用户被劝退 | 开源项目申请 SignPath.io 免费签名 | Phase 6 |
| R10 | 零遥测 = 对线上问题失明 | 无法定位崩溃 | 本地崩溃日志 + 用户手动导出 | Phase 6 |
| R11 | 捆绑第三方资源的许可（EasyList 系、public_suffix_list.dat 为 MPL） | 合规风险 | 规则以运行时下载为主、内置快照附许可文本；附录注明来源与许可 | Phase 5 |
| R12 | 体积预算超支（e_sqlite3.dll ~1.5MB + 规则快照 + public_suffix_list.dat 叠加逼近/突破 <5MB） | 核心卖点/营销数字兑现不了 | Phase 0 新增「体积预算表」spike，逐依赖称重；超支则裁剪默认规则集、或把口径基于实测调整（宁可发布前调口径，不被用户拿体积打脸） | Phase 0 |
| R13 | 便携版单实例语义误判（全局 Mutex 导致同 U盘插不同机器/一机多副本冲突） | 便携体验受损 | SingleInstanceService 按**数据目录**（而非按机器）判定实例，Mutex 名含数据目录哈希 | Phase 3 |

---

## 附录：参考资源

| 资源 | 链接 |
|------|------|
| ZZZ 浏览器 | https://github.com/zengjiangy/ZZZ |
| Helium 浏览器 | https://helium.computer |
| Min 浏览器 | https://github.com/minbrowser/min |
| WebView2 文档 | https://learn.microsoft.com/en-us/microsoft-edge/webview2/ |
| WebView2 扩展支持 | https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/extensions |
| ABP 规则语法 | https://adblockplus.org/filter-cheatsheet |
| Public Suffix List | https://publicsuffix.org/ （MPL 许可） |
| SignPath.io（开源签名） | https://signpath.io/ |
