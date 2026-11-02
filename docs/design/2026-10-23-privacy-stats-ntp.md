# 设计：隐私拦截统计 + 新标签页接线 + 隐私设置消费方

> 日期：2026-10-23 | 状态：已批准实施 | 关联：roadmap Phase 4 / Phase 5
>
> **2026-11-02 修订**：本文关于“第三方 Cookie 剥离 / 追踪计数”的设计已被真机探针证伪并废止。WebView2 会在 `WebResourceRequested` 后重新附加 Cookie；当前产品已移除该能力、统计与宣传。以后续 [P0/P1 硬化记录](2026-11-02-p0-p1-hardening.md) 与 [Cookie 探针](../spikes/results/cookie-privacy.md) 为准；本文其余历史设计保留。
>
> **2026-11-02 再修订**：新标签页统计卡与 `getStats` 桥已下线（决策 #25），`addShortcut` 桥未实现；周拦截计数保留在隐私盾面板「本周拦截（本机统计）」。统计状态机、周边界、原子写与批量落盘设计仍然有效。
>
> **后续状态（2026-10-29）**：A2 已将末两标签近似替换为随包 PSL；A1 已把统计改为 2 秒批量落盘并仅在实际移除 Cookie 时计数；C1 已交付真实设置窗口；B1 已落地 SQLite 基础。本文保留原始设计，差异以新记录为准：[A2](2026-10-29-a2-psl-rule-snapshot.md)、[S2 第一批](2026-10-29-s2-first-slice.md)。
>
> 背景：2026-10-19 项目盘点发现三个 P0 级「资产已就绪、最后接线未做」的断点——
> 隐私统计卡片吃桩数据、隐私设置无消费方、新标签页零接线。本设计补齐这三段接线，
> 让 v0.1 首发三大卖点（壁纸新标签页 / 隐私统计 / 隐私默认全开）成为事实。

## 一、问题定义

| # | 问题 | 现状 | 后果 |
|---|---|---|---|
| P0-1 | 隐私统计卡片无真实数据 | [app.js](../../Apage.Portable/Assets/webui/app.js) 中 `stats = {trackers: 1283, ads: 342}` 为硬编码桩；`HookAdBlock` 命中即 403，无计数管道 | 「本周已拦截 N 条追踪」——v0.1 最便宜、感知最强的差异化不可用 |
| P0-2 | 隐私设置无消费方 | `BlockThirdPartyCookies / SendDoNotTrack / SendGPC / WebRTCIPProtection` 四个默认全开项在 Portable 层零引用（仅 `SearchEngine`/`PortableCache` 被读取） | 「隐私默认全开」产品红线只是 JSON 基线，非事实 |
| P0-3 | 新标签页未接线 | `Assets/webui/` 全套（壁纸/搜索/快捷方式/时钟/统计卡 + WebMessage 桥）已写好，全仓无任何引用 | Phase 4 断点，视觉差异化不存在 |

## 二、目标 / 非目标

**目标**
1. 拦截计数落库（按周），经 WebMessage 推给新标签页，统计卡显示真实数字；
2. DNT / Sec-GPC 头注入、WebRTC IP 防护参数、第三方 Cookie 剥离全部接通设置开关；
3. 空白标签导航到本地新标签页，`ntp` 通道桥（navigate / getStats / openSettings / addShortcut）可用。

**非目标（明确不做，记录在案）**
- 设置面板 UI（Phase 6）；`openSettings` 先以事件上浮 + 信息提示桩承接。
- Public Suffix List：本设计当时用「末两标签」近似法，PSL 列入后续；**A2 已完成 PSL 替换**。
- SQLite 化：持久化继续 JSON 文件（与 SettingsService/SessionService 一致），roadmap 口径同步修正。
- 按规则来源分流统计（EasyPrivacy → 追踪计数）：本期追踪数 = 第三方 Cookie 剥离次数，广告数 = 规则拦截次数。
- MVVM 化改造：维持 code-behind 现实，README 口径修正。

## 三、Core 层设计（Apage.Core，netstandard2.0）

### 3.1 PrivacyStatsState（Models，纯数据 + 纯函数）

```csharp
public sealed record PrivacyStatsState
{
    public DateTime WeekStart { get; init; }      // 本周一 00:00（本地时间）
    public long AdsBlocked { get; init; }         // 广告规则拦截次数
    public long TrackersBlocked { get; init; }    // 第三方 Cookie 剥离次数
    public static DateTime ComputeWeekStart(DateTime now);   // 周一 00:00 本地
    public PrivacyStatsState RolledOver(DateTime now);       // 跨周则归零重启（纯函数）
}
```

周口径：**周一 00:00 本地时间**为一周边界（与「本周」中文语义一致；不用 UTC，避免用户感知错位）。

### 3.2 PrivacyStatsService（Services）

- 构造注入文件路径（`{数据目录}\privacy-stats.json`），与 SettingsService/SessionService 同构；
- `LoadAsync()`：读盘 → JSON 损坏/缺失回落全新状态 → `RolledOver(now)`；
- `SaveAsync()`：原子写（.tmp → Replace，exFAT 降级路径同 SettingsService）；
- `RecordAdBlocked(int count = 1)` / `RecordTrackerBlocked(int count = 1)`：锁内自增（UI 线程调用，锁成本可忽略）；
- `Snapshot`：`(long ads, long trackers)` 供 WebMessage 回复；
- **保存时机（A1 更新）**：计数变更后 2 秒批量原子落盘，正常退出（MainWindow.OnClosing）再显式 Flush；崩溃最多损失一个批次。

### 3.3 ThirdPartyDetector（Services，A2 后为实例 + PSL）

```csharp
public sealed class ThirdPartyDetector
{
    public ThirdPartyDetector(IPublicSuffixResolver resolver);
    public bool IsThirdParty(string? pageUrl, string? requestUrl);
    public static ThirdPartyDetector Fallback { get; } // PSL 不可用时末两标签
}
```

- 两边 host 经随包 PSL 求 registrable domain 比较；
- 任一侧解析失败 / 为空 / 为 IP / 仅公共后缀 → 返回 false（宁可漏判不可误剥）；
- PSL 加载失败时宿主显式注入 `Fallback`，安全方向为漏判；PSL 细节见 [A2 记录](2026-10-29-a2-psl-rule-snapshot.md)。

### 3.4 BrowserTabContext（Models，Portable→Core 的注入包）

```csharp
public sealed class BrowserTabContext
{
    public AppSettings Settings { get; set; } = new();
    public PrivacyStatsService? Stats { get; set; }
    public AdBlockRuleEngine? AdBlock { get; set; }
    public BrowserLifecycleService? Lifecycle { get; set; }
}
```

取代 BrowserTabView 现有的 `Lifecycle`/`AdBlock` 双属性（魔法赋值顺序收敛为一个对象，须在 Loaded 前赋值的约束不变）。

### 3.5 AppServices 接线

- 新增 `PrivacyStatsService PrivacyStats { get; }`（`{数据目录}\privacy-stats.json`）；
- `InitializeAsync()` 中 `await PrivacyStats.LoadAsync()`（小文件，冷启动成本可忽略）；
- `WebView2EnvironmentFactory` 委改签名：`Task<object> (string userDataFolder, string? additionalBrowserArguments)`；
- `BrowserLifecycleService` 构造增补 `string? additionalBrowserArguments = null`，创建 Environment 时透传。

## 四、Portable 层设计（Apage.Portable）

### 4.1 请求管道重构（BrowserTabView.OnCoreReady → HookWebPipeline）

过滤器注册条件从「仅 adBlock 非空」改为「adBlock 非空 **或** 任一隐私开关开」；单管道内顺序：

1. `SendDoNotTrack` → `Headers.SetHeader("DNT", "1")`；
2. `SendGPC` → `Headers.SetHeader("Sec-GPC", "Sec-GPC", "1")`；
3. 广告拦截（非 Document 上下文）→ 命中则 403 并 `Stats.RecordAdBlocked()`，随即 return；
4. `BlockThirdPartyCookies` 且非 Document → 经注入的 `ThirdPartyDetector` 判为第三方后，仅当 `Cookie` 头实际存在并成功移除才 `Stats.RecordTrackerBlocked()`（A1/A2）。

> 主文档（Document）不剥 Cookie：顶层导航对目标站即第一方，剥离会直接破坏登录态。

### 4.2 新标签页桥（WebMessageReceived）

- 仅当 `core.Source` 为新标签页 URI 时处理（**安全门控**：任意网页都能 `postMessage`，不校验 Source 等于把导航/设置入口暴露给所有站点，官方文档亦要求校验 Source 后使用宿主能力）；
- `channel == "ntp"` 分派：
  - `navigate` → `Navigate(url)`；
  - `getStats` → `PostWebMessageAsJson({channel:"ntp", type:"stats", trackers, ads})`；
  - `openSettings` / `addShortcut` → 上浮 `NewTabAction` 事件给 MainWindow（openSettings 先信息提示「设置面板开发中」，addShortcut 记 Trace 待 Phase 6）。

### 4.3 webui 落盘与导航

- `WebUiAssets.EnsureExtracted()`：把内嵌 Resource（`Assets/webui/{index.html,app.js,style.css,tokens.css,error.html}`）抽取到 `{数据目录}\webui\`，以 `.version` 标记文件判重（缺失/不匹配才重写，避免每次启动 IO）；
- 调用点：`App.OnStartup`（窗口创建前）；
- `TabManager`：空白标签（Url 为空）物化时导航到 NTP URI；`CaptureSession` 排除 NTP（与 `about:` 同等对待，恢复时不开NTP）；NTP 的 `DocumentTitle` 即「新标签页」，标题链自然正确。

### 4.4 启动装配（App.xaml.cs）

- `WebRTCIPProtection` 开 → `AdditionalBrowserArguments = "--force-webrtc-ip-handling-policy=disable_non_proxied_udp"`（Environment 创建时一次性生效，普通/隐私环境同参数）；
- 装配 `BrowserTabContext`（Settings + Stats + AdBlock + Lifecycle）经 MainWindow → TabManager → 各 BrowserTabView；
- 调用 `WebUiAssets.EnsureExtracted()`。

## 五、测试计划（决策 #18：Core 纯逻辑优先）

| 目标 | 文件 | 覆盖 |
|---|---|---|
| PrivacyStatsState | PrivacyStatsStateTests | 周一边界（周日/周一/周中/跨周）、RolledOver 归零与保留 |
| PrivacyStatsService | PrivacyStatsServiceTests | 损坏回落、原子写往返、跨周加载自动归零、并发自增 |
| ThirdPartyDetector | ThirdPartyDetectorTests | 同域/子域/跨域、多级后缀误判方向（漏剥不误剥）、空值与非法 URL |

## 六、风险与对策

| 风险 | 影响 | 对策 |
|---|---|---|
| 三方 Cookie 剥离破坏站点（默认开） | 登录态丢失 | 文档公开「已知残余泄露面」既有口径；设置开关可关（设置面板 Phase 6 前改 settings.json） |
| file:// 下 localStorage 不持久 | 壁纸选择不记忆 | app.js 已 try/catch 降级；不阻断 |
| 统计崩丢失 | 数字回退 | 退出保存；文档明示 |
| NTP 桥被恶意页面利用 | 诱导导航 | Source 门控（§4.2） |

## 七、实施分工与写域

| 任务 | 负责 | 写域 |
|---|---|---|
| T1 Core 层 + 单测 | core-dev | `Apage.Core/**`、`Apage.Core.Tests/**` |
| T2 文档同步（独立部分） | docs-dev | `docs/**`、`README.md`、`AGENTS.md`、`.workbuddy/**` |
| T3 Portable 接线 | lead | `Apage.Portable/**` |
| T4 集成验证 + 路线图/README 收尾 | lead | `docs/roadmap.md`、`README.md`、`docs/product-design.md` |
