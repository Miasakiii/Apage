# Apage 产品设计文档

> 内部文档 | 版本：v0.10 | 更新：2026-11-02
>
> v0.10 变更（P0/P1 硬化，2026-11-02）：
> - Cookie 真机探针证伪事件级删除：WebView2 网络栈会重新附加 Cookie；第三方 Cookie 隔离、追踪计数与相关宣传从 v0.1 移除，旧字段默认 false
> - 广告拦截区分顶层主文档与 iframe Document，第三方 iframe 不再绕过规则
> - 下载按 id 串行、终态不可回退；单条取消不再全表标孤儿；会话恢复完成前关闭不覆盖 session
> - 设置/会话/统计统一 `AtomicJsonStore`；规则更新保留用户规则、串行并逐跳校验 HTTPS
>
> v0.8 变更（D2：发布工程）：
> - 正式产物版本元数据固定为 0.1.0.0 / 0.1.0-dev，不再输出 0.0.0.0
> - `build\package.ps1` 串联许可校验、4.25MB/5MB 硬门禁、五信号 smoke、单文件启动基准、SHA-256、legal bundle 与 release manifest
> - 当前产物 3,972,608 字节；首启 ContentRendered 882.6ms，热启中位 802.3ms（本机 1000ms 门槛通过）
> - SignPath 申请与签名后复验手册已准备；产物仍未签名，签名执行属 S4
>
> v0.7 变更（A2：PSL + 离线规则快照）：
> - 第三方 Cookie 判定从末两标签近似切换为随包 Public Suffix List（2026-10-28 版本，MPL-2.0）；通配/例外/IDN/尾点均有测试，失败回退末两标签
> - 内置 EasyList/EasyPrivacy 兼容快照 94,646 条（CC BY-SA 3.0），筛选后跳过 38,432 条不可执行规则并记录原因；PSL/规则均校验 SHA-256 后加载
> - 单文件产物增至 3,970,560 字节，仍低于 4.25MB 开发目标；smoke 扩展为应用/窗口/WebView2/SQLite/隐私资产五信号
> - 运行时仍零规则/PSL 自动更新；A3 的「立即更新规则」尚未实现
>
> v0.6 变更（S2 第一批 D1/A1/B1/C1）：
> - D1 正式单文件打包落地：1 文件 / 3,068,928 字节，真实 smoke 覆盖窗口、WebView2、SQLite 与设置窗口构造；尚未签名
> - A1 收紧规则语义：无法完整求值的 `$` 修饰符规则整条拒收，不再宽泛拦截；追踪统计仅在 Cookie 实际存在并移除时增加，且改为短周期批量落盘
> - B1 SQLite 安全基座落地：schema v1、事务迁移、迁移前备份、完整性检查、损坏不删库；设置/会话/统计继续 JSON
> - C1 真实设置窗口与本地滚动诊断落地；日志默认脱敏 URL/Cookie，只手动导出
>
> v0.5 变更（v0.1 发布收敛基线）：
> - S0 发布口径与共享契约已冻结；S1 独立单文件 spike 条件通过，2,950,144 字节 EXE 已验证 WPF/Core/WebView2/SQLite
> - 生产 `Apage.Portable` 当前仍为多文件构建；Costura、原生依赖引导和正式体积门禁转入 S2-D，不能把 spike 结果写成生产已发布能力
> - 真实应用热启动中位 900.3ms；首启存在阈值附近及更高波动，`<1s` 仅为参考环境下的窗口可见目标，最终签名单文件仍须复测
> - v0.1 数据边界固定为新增书签/历史/下载使用 SQLite，设置/会话/周统计继续 JSON；用户脚本最小内核移至 v0.1 后
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
| 极致轻量 | 发布目标：x64 单文件 EXE、签名后 <5MB、窗口可见 <1秒；B2/A3 产物 4,035,584 字节（未签名） |
| 隐私默认全开 | DNT/GPC、WebRTC 防护与广告规则拦截默认启用；第三方 Cookie 隔离因 WebView2 无可验证入口，v0.1 不提供 |
| 全本地化 | 零遥测、零后台请求、零数据收集（仅用户触发的网络请求，清单透明） |
| 无需登录 | 不强制账号，不要求云同步 |
| Portable | 数据跟程序走，U盘即插即用（前提：目标机已装 WebView2 Runtime，Win11 默认预装，见风险 R1） |
| 功能完整 | 用户脚本（GM_* 兼容）、部分 MV3 扩展（侧载）、多标签、下载管理 |
| 无广告 | 浏览器本身无任何广告、推广、"可接受广告"白名单 |
| 壁纸美学 | 新标签页自定义静态壁纸 + 隐私拦截统计卡片 |
| 开发者友好 | 完整 F12 调试控制台（WebView2 原生 DevTools）+ 反调试过滤一键开关 |

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
- Chrome 扩展量力而行：WebView2 的扩展支持为 **本地侧载**（须在 Environment 创建时显式开启 `AreBrowserExtensionsEnabled`，默认关闭；2026-10-26 实测最小 MV3/MV2 均可装），API 覆盖不全，无商店直连。宣传口径为「支持本地侧载扩展」，不做全量承诺（实测见 [spikes/results/extension.md](spikes/results/extension.md) 与路线图 Phase 0）

### 痛点 6：没有差异化记忆点

> "百分浏览器和 Chrome 有什么区别？体积小了点？"

- 星愿的差异化太窄（主页+漫画），Vivaldi 的太复杂（什么都能定制）

**Apage 的标签**：壁纸美学 + 隐私统计 + Portable 即插即用

> 注：单纯的壁纸容易被复制。更强的记忆点是新标签页上的**隐私拦截统计**（"本周已拦截 M 个广告"）——Brave 靠这个小卡片建立了极强的用户感知，且数据全本地，与零联网原则不冲突。壁纸负责美，可验证的拦截统计负责价值感；无法验证的“追踪数”不纳入。

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
| UI 组织 | 当前 WPF code-behind；MVVM 分层为预留（当前未引入 MVVM 包） | 避免首发前大范围重写，纯逻辑继续沉淀 Core |
| 引擎 | WebView2 | 系统自带 Chromium（常青版），零体积 |
| 通信 | WebMessage（chrome.webview.postMessage） | WebView2 原生异步通道，无端口、无鉴权问题 |
| 存储 | 设置/会话/周统计 JSON；SQLite schema v1 与安全基础已就绪，书签/历史/下载待 B2 | 小状态原子写足够；结构化数据走事务与查询 |
| 打包 | 生产单文件：Costura + NativeBootstrap，`build\package.ps1` 当前产出 3,972,608 字节 / 版本 0.1.0.0 | .NET FX 4.8 不支持 PublishSingleFile；已准备 SignPath 签名后复验，实际签名属 S4 |
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
| 安装包体积 | 签名后 <5MB（见 R12） | ZZZ ~5MB | D2 未签名产物 3,972,608 字节；4.25MB 目标余量 277,392（6.5%） |
| 冷启动时间 | <1秒（窗口可见） | Min 0.8秒 | D2 本机首启 882.6ms、热启中位 802.3ms；CI 用 1500ms 非参考门禁，发布须复测 |
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
│         🛡 本周已拦截 342 个广告                    │
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
- **隐私统计卡片（v0.1 首发差异化）**：本周广告拦截计数（纯本地数据，强化隐私心智；不再展示无法验证的追踪数）
- 时钟 + 日期

### 5.2 隐私保护

#### 默认配置

```csharp
public class PrivacyDefaults
{
    public bool BlockThirdPartyCookies = false;      // 旧字段：事件级删除已被真机证伪，默认关且无 UI
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

> **实现现状（A2，2026-10-29）**：v0.1 随包快照当前只含 **EasyList + EasyPrivacy** 中引擎可正确执行的网络规则（94,646 条），EasyList China 等列表可在后续版本追加。快照记录来源 URL、抓取时间、上游 SHA-256、接受/跳过原因与许可；过滤快照按 CC BY-SA 3.0 分发，PSL 按 MPL-2.0。运行时加载前校验 SHA-256，不自动联网更新；「立即更新规则」属 A3。

**更新机制**（与"零后台请求"原则对齐）：

1. 安装包内置一份规则快照，保证开箱即用；
2. 设置页提供「立即更新规则」按钮（用户触发的网络请求）；
3. 「启动时自动更新」为可选开关，**默认关闭**，开启时明确提示会产生后台请求；
4. 关于页列出程序可能发起的全部网络请求清单，透明可审计，共三条：规则更新（用户点击触发）、联网搜索建议（默认关）、**搜索引擎图标一次性抓取**（默认开，可在设置关闭；仅对白名单内的四个引擎各抓一次 favicon.ico）。

> **站点图标口径（2026-11-02 实现）**：图标有两条来源，都不经过任何第三方图标服务。
>
> **来源一（零请求）**：用户访问站点时，内核本来就会取回 favicon，宿主用 `GetFaviconAsync` 把同一份字节落盘到 `{数据目录}\favicons`，新标签页从本地读。
>
> **来源二（一次性、受控）**：为了让首屏四个引擎图标开箱可用，新标签页会请求 `https://<引擎主机>/favicon.ico` 各一次。边界：仅限百度 / 必应 / Google / DuckDuckGo 的主机别名（`SearchEngineHosts` 白名单，页面无法指定任意主机）；仅当本地无缓存时触发；仅 HTTPS、不跟随重定向、6 秒超时、单次 ≤64KB、只接受图片魔数；每个主机每会话最多尝试一次；命中或失败后落缓存，不再请求。设置里「获取搜索引擎图标」可关闭，关闭后完全不发请求。
>
> 因此：
> - **不新增任何请求**：不请求 `favicon.ico`、不调用第三方图标服务、不做远程代理；
> - **用户快捷方式仍不抓取**：只有引擎主机走来源二，用户自己添加的站点一律靠访问获取，没有图标时回落为品牌色字标；
> - 抓取结果统一重编码为 32px PNG 再入库，避免超大原图与格式不一致；缓存上限 256 条，按最后写入时间裁剪；目录损坏或不可写只影响图标显示，不影响浏览。

#### 第三方 Cookie 隔离（v0.1 不提供，已纠偏）

原设计计划在 `WebResourceRequested` 中按 PSL 判断第三方并删除 `Cookie` 请求头。**2026-11-02 真机探针证伪**：事件内能读到并删除 Cookie，但 WebView2 网络栈随后会重新附加，服务器仍收到同一 Cookie。

- 证据：[cookie-privacy.md](spikes/results/cookie-privacy.md)（基线/删除两轮，Runtime 156.0.4314.58）；官方时序亦说明事件后可继续附加 Cookie/Authorization。
- 产品处理：`BlockThirdPartyCookies` 保留为旧 JSON 兼容字段且默认 `false`；设置、请求管线、隐私统计、NTP 与隐私盾全部移除该能力，不作超额宣传。
- WebView2 Tracking Prevention 仍为默认 Balanced（按已知追踪器名单工作），不等价于“第三方 Cookie 全阻断”，因此不以此替代承诺。
- PSL 仍随包保留，供未来受支持平台能力或规则来源分析使用；当前不产生“已剥离 Cookie”的运行时声明。
- **隐私统计卡片口径**（新标签页「本周已拦截 N 个广告」）：广告数 = 规则拦截次数，按周统计（周一 00:00 本地时间边界），正常退出时原子写 `{数据目录}\privacy-stats.json`；追踪数已随无效能力移除。
- 决策与实现细节见 [P0/P1 硬化记录](design/2026-11-02-p0-p1-hardening.md)。

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

每个 WebView2 实例注册完整事件链：广告拦截（含第三方 iframe 文档）、DNT/GPC 头注入、历史记录、暗黑模式、下载管理等。

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
| F12 开发者工具 | 完整 DevTools + 反调试过滤开关（详见 §5.8） | Phase 7d |

### 5.7 网页翻译（后期）

- Edge 翻译 API（免费、无 Key）
- DOM 就地翻译（TreeWalker 收集文本节点）
- 批量翻译（每批 40 条，最多 12000 字符）
- 完美恢复原文

### 5.8 开发者工具（F12 调试控制台 + 反调试过滤）

> 规划随 [roadmap.md](roadmap.md) Phase 7d 落地（v0.9）。目标用户含开发者（见「一、产品定位」），F12 是刚需；反调试过滤是 WebView2 壳层能提供的差异化开发体验。

#### F12 调试控制台（DevTools）

- **形态**：WebView2 原生完整 Chromium DevTools（与 Edge 同源：Elements / Console / Sources / Network / Performance / Application …），**独立窗口**承载——WebView2 无「停靠面板」API，独立窗口即最终形态，不承诺停靠（R15）。
- **入口**：内核内置 F12 / Ctrl+Shift+I / 右键「Inspect」开箱即用（`AreDevToolsEnabled` 默认 true）；外壳补工具栏「开发者」菜单（程序化 `OpenDevToolsWindow()`）与设置面板开关。
- **生命周期**：每个标签一个 DevTools 窗口，标签关闭随内核销毁自动关闭；重复打开是官方 no-op（记录已开状态，避免无谓调用）；聚焦已有窗口需 Win32 窗口探测（脆弱，列为探索项，R15）。
- **开关**：`AppSettings.EnableDevTools`（默认 true，贴合开发者）；关闭后 `AreDevToolsEnabled=false`，F12/菜单入口一并失效。
- **版本兼容**：程序化打开统一用官方文档确认的无参 `OpenDevToolsWindow()`，不依赖带参重载，避免旧 SDK/Runtime 编译或运行失败。

#### 反调试过滤（Anti-Anti-Debug）

页面常用反调试手段干扰调试，常见三类与应对：

| 手段 | 原理 | Apage 应对 |
|------|------|-----------|
| `debugger;` 刷屏 | 循环/定时器反复执行 `debugger;`，DevTools 打开即不断暂停 | **动态代码中和**：document-start shim 包裹 `Function` 构造器与 `eval`，剥离代码字符串中的 `debugger` 关键字；定时器回调 `toString()` 重建剥离列为探索项（有闭包破坏风险）；**静态脚本内的字面 `debugger;` 无法改写**（已解析字节），如实标注，依赖 DevTools「Deactivate breakpoints」习惯 |
| DevTools 探测 | debugger 计时差、窗口尺寸差、`toString` 自检 | 计时探测随 `debugger` 中和自然失效；独立窗口形态天然免疫窗口尺寸差；shim 保留原生 `toString` 形貌防自检 |
| console 刷屏 | 海量日志淹没真实输出 | 控制台过滤：按级别/正则过滤、重复折叠、防刷屏（DevTools Console 自带级别过滤为基线） |

- **开关**：`AppSettings.FilterAntiDebug`（**默认关**）+ 站点级记忆 + 工具栏一键开关。默认关：注入会改页面行为、可能误伤正常站点，也避免滑向「破解工具」。
- **注入方式**：与用户脚本共用 `AddScriptToExecuteOnDocumentCreated` 管线，注入顺序**前置**（先于用户脚本与页面脚本），独立命名空间不向页面暴露（script-manager §10 集成）。
- **产品红线（决策 #21）**：仅做「让开发者能调试自己页面/正当页面」的辅助，**不提供**绕过登录、DRM、内容付费校验、验证码等破解能力；反调试过滤不进「默认全开」集合，与隐私统计/广告拦截定位不同。

#### 与既有系统的关系

- 复用 WebMessage 通道：宿主侧能力（拦截统计、网络管道数据）如需推给调试面板，走 `chrome.webview` 通道，零额外端口。
- 快捷键：F12/Ctrl+Shift+I 由内核内置处理；外壳侧快捷键（若补）经 KeyboardService 统一注册，与 F11 全屏、Ctrl+F 查找协调，页内输入焦点不冲突。

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
| R3 | WebView2 扩展 API 覆盖不足 | "支持 Chrome 扩展"承诺落空 | 宣传口径为「本地侧载扩展」（须显式开启 `AreBrowserExtensionsEnabled`；MV3/MV2 均实测可装，2026-10-26 Spike 3，见 [spikes/results/extension.md](spikes/results/extension.md)）；用户脚本作为一级能力 | Phase 0 Spike 3（已实测） |
| R4 | 50 万条规则下拦截性能不达标 | 核心卖点受损 | 子串索引 + MatchTimeout；benchmark 不达标则裁剪默认规则集 | Phase 0 Spike 1 |
| R5 | 冷启动 <1s 达不到 | 核心卖点受损 | UI 先行 + WebView2 后台预热；验收口径为「窗口可见 <1s」。真实应用热启动中位 900.3ms，首启存在波动，不作全环境承诺 | Phase 0 Spike 2（已实测；S4 复测） |
| R6 | U盘热拔出 / exFAT 上 SQLite 损坏 | 用户数据丢失 | `journal_mode=DELETE` + `synchronous=FULL` + 启动 `PRAGMA integrity_check` + 自动备份上一版本 | Phase 3 |
| R7 | WebView2 缓存海量小文件写入伤 U盘、拖慢速度 | 便携体验差 | 缓存位置可配置：默认宿主机 `%TEMP%`（数据仍便携），纯便携模式可选；只读 U盘 场景降级运行 | Phase 1 |
| R8 | 规则列表供应链攻击（ReDoS/恶意规则） | 浏览器卡死 | 见 5.3 供应链安全 | Phase 5 |
| R9 | 未签名 EXE 触发 SmartScreen 拦截 | 新用户被劝退 | 开源项目申请 SignPath.io 免费签名 | Phase 6 |
| R10 | 零遥测 = 对线上问题失明 | 无法定位崩溃 | 本地崩溃日志 + 用户手动导出 | Phase 6 |
| R11 | 捆绑第三方资源的许可（EasyList 系、public_suffix_list.dat 为 MPL） | 合规风险 | **A2 已闭环**：PSL MPL-2.0；EasyList/EasyPrivacy 过滤快照选择 CC BY-SA 3.0；许可全文、归属和来源哈希随包，`licenses/THIRD-PARTY-NOTICES.txt` 可审计 | A2（完成） |
| R12 | 体积预算超支（e_sqlite3.dll ~1.5MB + 规则快照 + public_suffix_list.dat 叠加逼近/突破 <5MB） | 核心卖点/营销数字兑现不了 | **B2/A3 实测 4,035,584 字节**：4.25MB 目标余 214,416（5.0%）、5MB 硬门禁余 964,416（19.3%）；`package.ps1` 已硬性拦截超限 | B2/A3（完成）；S4 复测 |
| R13 | 便携版单实例语义误判（全局 Mutex 导致同 U盘插不同机器/一机多副本冲突） | 便携体验受损 | SingleInstanceService 按**数据目录**（而非按机器）判定实例，Mutex 名含数据目录哈希 | Phase 3 |
| R14 | 反调试过滤误伤正常站点 / 被当成「破解工具」 | 站点行为异常、产品口碑受损 | **默认关** + 站点级记忆 + 一键开关；仅动态代码中和，不做 JS 响应改写等重武器；明确产品红线（决策 #21）：仅调试辅助，不提供绕过登录/DRM/付费校验 | Phase 7d |
| R15 | WebView2 DevTools API 限制（独立窗口无停靠、`OpenDevToolsWindow` 已开即 no-op、无法编程聚焦/枚举、.NET API 仅无参重载） | F12 体验不如 Chrome 停靠面板 | 诚实口径：独立窗口即最终形态，不承诺停靠；每标签记录已开状态；聚焦已有窗口列为探索项；程序化打开统一无参调用 | Phase 7d |

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
| Public Suffix List | https://publicsuffix.org/ （MPL-2.0） |
| EasyList / EasyPrivacy | https://easylist.to/ （过滤快照按 CC BY-SA 3.0 分发，上游另可选 GPL-3.0+） |
| SignPath.io（开源签名） | https://signpath.io/ |
| WebView2 DevTools 调试 | https://learn.microsoft.com/en-us/microsoft-edge/webview2/how-to/debug-devtools |
