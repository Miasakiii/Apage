# Apage 开发路线图

> 内部文档 | 更新：2026-07-20
>
> v0.5 变更（Phase 1 收尾 + 构建修复）：
> - Phase 1 大部分完成：解决方案初始化、单标签浏览、DPI 清单、Runtime 检测引导、缓存策略均已落地；剩余窗口标题同步、Omnibox 接入主窗口、单文件发布实测
> - Phase 2 单实例管理提前完成（R13 按数据目录哈希 Mutex，零等待夺取 + Abandoned 接管）
> - 修复阻塞编译的既有缺陷：Mutex 构造阻塞、WebView2 Environment 类型不匹配、Process.Start 无法打开 URL、XAML 模板/触发器/注释错误、Omnibox 代码后置缺失
> - 构建体系：global.json 固定 .NET 8 SDK；VS MSBuild 需设 MSBuildSDKsPath + MSBuildEnableWorkloadResolver=false（或在 VS Installer 勾选「.NET SDK」组件）
>
> v0.4 变更（回退双线路，单押便携版）：
> - 暂缓双线路：Phase 8（桌面版）降级为「PMF 验证后条件启动」，非既定交付；v0.1~v1.0 单押便携版
> - Phase 0 新增 Spike 4（体积预算表，对应 R12）、Spike 5（GM 存储内联注入验证，对应决策 #14）
> - Phase 4 隐私统计卡片标注 v0.1 首发差异化；Phase 7 脚本管理器抽出「最小内核」标注可提前
> - 决策记录更新 #1/#8/#14/#16/#17，新增 #18 测试策略
>
> v0.3 变更（双线路架构决策）：
> - Phase 1 明确为便携版（.NET Framework 4.8）优先发布
> - 新增 Phase 8：桌面版（.NET 8 自包含）衍生
> - 决策记录新增 #16 双线路架构
> - v2 变更（插入 Phase 0 技术验证；v0.1 范围收敛；决策记录更新）仍有效

---

## Phase 0：技术验证（3-5 天）

**目标**：在写 UI 之前，先验证三个最大架构风险（详见设计文档「七、风险与应对」）

- [ ] Spike 1：ABP 规则引擎原型（纯控制台）——50 万条规则的匹配耗时与内存，验证子串索引方案（对应 R4）
- [ ] Spike 2：冷启动实测——WPF UI 先行显示 + WebView2 后台预热，验证「窗口可见 <1 秒」可达（对应 R5）
- [ ] Spike 3：扩展能力摸底——实测 `AddBrowserExtension` 可装/不可装的 API 范围，确定对外口径（对应 R3）
- [ ] WebMessage 通信链路验证——`WebMessageReceived` ↔ `postMessage` 请求/响应 id 关联，覆盖注入脚本场景
- [ ] Spike 4：体积预算表——逐依赖称重（e_sqlite3.dll ~1.5MB、规则快照、PSL、WebView2 SDK 托管部分），验证 <5MB 可达或据实调口径（对应 R12）
- [ ] Spike 5：GM 存储内联注入验证——注入时把脚本存储 KV 序列化内联进 IIFE，验证 GM_getValue 同步语义无「缓存未就绪」窗口（对应决策 #14）

**交付物**：六份验证结论；任一不通过，回到设计文档调整架构后再进 Phase 1

---

## Phase 1：骨架 + 单标签（便携版优先，1-2 周）

**目标**：能打开网页（便携版 Apage.Portable）

**技术栈**：.NET Framework 4.8 + WPF + WebView2（系统自带运行时，真即插即用）

- [x] 解决方案初始化：Apage.Core（.NET Standard 2.0）+ Apage.Portable（.NET FX 4.8 + WPF）
- [x] Apage.Core 设定 `<LangVersion>latest</LangVersion>`（netstandard2.0 目标仍可用 C# 8+ 语法糖；record/init 补 IsExternalInit polyfill）
- [x] 单标签浏览（前进/后退/刷新/地址栏）——BrowserTabView 导航契约 + 工具栏转发已完成；Omnibox 控件就绪但尚未接入主窗口（工具栏仍为占位）
- [ ] 窗口标题同步
- [x] PerMonitorV2 DPI 清单
- [x] WebView2 运行时检测与缺失引导（R1；注册表探测 + 中文引导面板，手动下载，绝不静默联网下载）
- [x] 缓存位置策略：默认宿主机 %TEMP%，可选纯便携模式（R7）
- [ ] 单文件发布测试（框架依赖，实测体积 <5MB）

**交付物**：能输入 URL 并浏览网页的单文件 EXE（便携版，U盘即插即用）

> **说明**：v0.1~v1.0 单押便携版；桌面版（Apage.Desktop，.NET 8 自包含）暂缓，仅 PMF 验证后条件启动（见 Phase 8）。Apage.Core 保留分层以备未来衍生。

---

## Phase 2：多标签（1 周）

**目标**：多标签可管理

- [ ] 标签页创建/关闭/切换——TabStrip UI 已完成（桩数据版），待接入真实标签会话
- [ ] 拖拽排序
- [ ] 标签休眠（非活跃标签释放内存）
- [x] 单实例管理——SingleInstanceService：Mutex 名 = "Apage/" + 数据目录 SHA256 前 16 位，initiallyOwned:false + WaitOne(0) 零等待检测，Abandoned 自动接管
- [ ] 会话保存（退出时记录打开的标签）

**交付物**：多标签浏览，非活跃标签自动休眠

---

## Phase 3：数据层（1 周）

**目标**：数据可持久化

- [ ] SQLite 初始化 + 迁移（journal_mode=DELETE、synchronous=FULL、启动完整性检查 + 自动备份，对应 R6）
- [ ] 书签 CRUD + 从 Chrome/Edge 导入（HTML）
- [ ] 历史记录 + 地址栏本地补全
- [ ] 下载管理
- [ ] 会话恢复（启动时恢复上次标签；**显式排除隐私标签 URL，绝不写入会话文件**）
- [x] 单实例语义按数据目录判定（已随 Phase 2 单实例管理一并落地：Mutex 名含数据目录哈希，同 U盘插不同机器互不冲突，对应 R13）

**交付物**：书签、历史、下载、会话恢复可用

---

## Phase 4：新标签页 + 壁纸（1 周）

**目标**：Apage 的视觉差异化

> UI 规范以 [frontend-design.md](frontend-design.md) 为准（设计令牌、新标签页渲染策略、动效与状态规范）；新标签页采用本地 HTML/CSS 实现，经 WebMessage 取隐私统计数据。

- [ ] 新标签页 UI（壁纸+搜索+快捷方式+时钟）
- [ ] 壁纸设置（选择本地图片 / 拖拽设置）
- [ ] 搜索引擎切换
- [ ] 隐私统计卡片（本周拦截追踪/广告计数，纯本地数据）——**v0.1 首发差异化，最便宜、感知最强，确保随首发交付**

**交付物**：带壁纸与隐私统计的新标签页，拖拽即可设置壁纸

---

## Phase 5：隐私 + 广告拦截（2 周）

**目标**：隐私保护核心能力（v0.1 范围 = 网络层拦截；化妆规则后置 Phase 7+）

- [ ] ABP 规则解析引擎（子串索引；规则正则统一 MatchTimeout 防 ReDoS，对应 R8）
- [ ] WebResourceRequested 拦截（网络规则）
- [ ] 第三方 Cookie 控制（请求剥 Cookie + 响应剥 Set-Cookie 可选）
- [ ] DNT/Sec-GPC 头注入
- [ ] WebRTC IP 泄露防护（disable_non_proxied_udp，按站点开关）
- [ ] 规则更新机制：内置快照 + 手动「立即更新」；自动更新可选且默认关
- [ ] 规则列表许可合规（R11）

**交付物**：网络层广告拦截可用，隐私保护默认全开

---

## Phase 6：打磨 + 发布（1-2 周）

**目标**：v0.1 首次发布

- [ ] 暗黑模式（Smart）
- [ ] 设置面板
- [ ] Ctrl+F 页内查找
- [ ] 本地崩溃日志 + 手动导出（R10）
- [ ] 单文件发布（portable EXE）+ 代码签名（SignPath.io，对应 R9）
- [ ] 测试 + 修复
- [ ] 首次发布 v0.1

**交付物**：可分发、已签名的 portable EXE，功能完整的 MVP

---

## Phase 7+：完整版迭代（便携版）

**目标**：逐步补齐高级功能（便携版 Apage.Portable）

- [ ] 完整 ABP 引擎（正则 + 化妆规则 + 右键标记广告）
- [ ] **用户脚本管理器**（详见 [script-manager.md](script-manager.md)）——一级差异化，优先级最高
  - [ ] **最小内核（可提前至 v0.1）**：脚本导入 + URL 匹配 + 基础 GM_（getValue/setValue/addStyle）——条件成熟时前移，让首发即带可见的脚本能力
  - [ ] Phase 7a：运行时核心——ScriptBridgeService、URL 匹配引擎、注入管线、第一优先级 GM_* API
  - [ ] Phase 7b：管理器 UI + 存储——脚本导入、列表页、编辑器（基础版 TextBox）、存储管理
  - [ ] Phase 7c：高级 API + 打磨——GM_xmlhttpRequest、@require、安全校验
- [ ] 隐私标签（共享隔离 Environment）+ 外部 watchdog 崩溃守护
- [ ] 地理位置模拟
- [ ] 暗黑模式（Smart，轻量 `color-scheme: dark`）
- [ ] 阅读模式
- [ ] 搜索建议（默认本地；联网建议显式开启）
- [ ] 快捷键自定义
- [ ] 设为默认浏览器 / 协议关联
- [ ] 多窗口
- [ ] 媒体嗅探
- [ ] 多壁纸轮播

**交付物**：功能完整的便携版浏览器（v1.0）

---

## Phase 8：桌面版衍生（Apage.Desktop）— 暂缓，条件启动

**状态（v0.4）**：**暂缓，非既定交付**。仅在便携版验证 PMF（v1.0+ 稳定、用户明确需要 MV3 扩展等运行时绑定能力）后再评估启动。桌面版"独有"卖点中仅「部分 MV3 扩展」与运行时/WebView2 版本强绑定，其余（翻译/暗黑增强/AvalonEdit）便携版亦可实现。

**目标**：基于 Apage.Core 共享层，衍生 .NET 8 自包含桌面版

**前提**：便携版已稳定（v1.0+），Apage.Core 接口边界清晰，且 PMF 验证支持第二产品线的维护成本

- [ ] Apage.Desktop 项目初始化（.NET 8 + WPF + 自包含发布）
- [ ] 共享 Apage.Core（验证 .NET Standard 2.0 引用兼容性）
- [ ] 共享 ResourceDictionary（文件链接）
- [ ] 脚本编辑器升级为 AvalonEdit（完整版，~2MB 增量可接受）
- [ ] Chrome 扩展（部分 MV3，侧载）——桌面版独有
- [ ] 网页翻译（Edge 翻译 API）——桌面版独有
- [ ] 暗黑模式（Force + Native）——桌面版独有
- [ ] ReadyToRun 编译优化（冷启动 <0.8s）
- [ ] 桌面版发布测试（自包含单文件，~50MB）

**交付物**：Apage.Desktop v0.1，完整体验的桌面版浏览器

> **维护策略**：桌面版功能优先在 Apage.Core 实现，UI 层差异化单独处理。避免便携版/桌面版逻辑分叉，保持 ~90% 代码共享。

---

## 关键设计决策记录

| # | 决策 | 选择 | 理由 |
|---|------|------|------|
| 1 | 运行时 | 便携版 .NET FX 4.8（唯一发布线） | 系统自带实现真即插即用；ZZZ 已验证，踩坑少。桌面版 .NET 8 暂缓（见 #16） |
| 2 | 通信方式 | WebMessage（chrome.webview.postMessage） | WebView2 原生异步通道，无端口、无鉴权问题；WebSocket 受 HTTPS 混合内容阻断且暴露本地端口（设计文档 4.4） |
| 3 | 数据存储 | SQLite | 单文件，查询高效，portable 友好 |
| 4 | 广告拦截 | ABP 引擎 | 规则兼容，社区生态 |
| 5 | 扩展支持 | 用户脚本优先；部分 MV3 扩展（侧载） | WebView2 扩展 API 仅覆盖 MV3 子集，GM_* 才是真差异化 |
| 6 | 壁纸系统 | 简单 + 拖拽 + 隐私统计卡片 | 差异化，不复杂；统计卡片强化隐私心智 |
| 7 | 隐私默认 | 全开 | 产品定位决定 |
| 8 | 运行时分发 | 框架依赖（.NET FX 4.8 系统自带） | 保住 <5MB 单文件；真正依赖是 WebView2 Runtime（R1，前提诚实化，不静默联网下载），非 .NET |
| 9 | 隐私标签隔离 | 共享隔离 Environment | 每标签独立 Environment = 每标签一套进程，内存目标无法达成 |
| 10 | WebRTC | 防 IP 泄露，不断连 | 一刀切阻断 RTCPeerConnection 会打死会议类网站 |
| 11 | 脚本格式 | Tampermonkey 兼容 | 社区通用，可直接导入 Greasy Fork 脚本 |
| 12 | 脚本更新 | 仅本地导入 | 与零网络原则一致；远程更新可作后续可选功能 |
| 13 | 脚本编辑器 | v0.1 自绘 TextBox，v0.2 评估 AvalonEdit | 先保住 <5MB 体积目标 |
| 14 | GM_getValue 同步语义 | 注入时内联存储 KV | TM 兼容要求同步，WebMessage 是异步；注入时把该脚本全部 KV 内联进 IIFE 头部，彻底消除 document-start「缓存未就绪」窗口，比运行时异步预加载可靠 |
| 15 | @require 远程依赖 | 不支持，手动放置 | 零网络原则；提示用户手动下载 |
| 16 | 运行时分发架构 | **v0.4 修正：暂缓双线路，单押便携版** | v0.3 曾定双线路；评审发现桌面版"独有"能力中仅 MV3 扩展与运行时强绑定，双 CI/CD 对个人项目过重。改为单押便携版；Apage.Core 保留分层（解除 C# 7.3 死锁，LangVersion=latest）以备未来衍生；桌面版降级为 PMF 验证后可选项（R2、Phase 8） |
| 17 | 主力发布版本 | 便携版唯一发布线（v0.1~v1.0） | 即插即用是 Apage 最硬卖点；.NET FX 4.8 路径 ZZZ 已验证，踩坑少；桌面版暂缓，非既定交付 |
| 18 | 测试策略 | Core 层纯逻辑优先单测 | ScriptMatcher、AdBlockRuleEngine、ScriptMetadataParser 等纯逻辑最该也最好写单测，是共享层最划算的收益点 |

---

## 参考实现

| 模块 | 参考来源 |
|------|---------|
| 服务容器 | ZZZ 的 AppServices |
| 懒加载策略 | ZZZ 的 InitializeAsync |
| ABP 引擎 + 子串索引 | ZZZ 的 AdBlockRuleEngine |
| 隐私标签隔离 | ZZZ 的 GetEnvironmentAsync（改为共享隔离环境） |
| 崩溃清理守护 | ZZZ 的 PrivateDataGuard（外部 watchdog 进程） |
| 标签休眠 | ZZZ 的 Sleep/SetActive |
| WebMessage 异步通信 | Apage（替代 ZZZ 的同步 HostObject；WebSocket 方案已否决） |
| 壁纸系统 + 隐私统计卡片 | Apage 原创 |
| 脚本管理器（GM_* 运行时 + 管理 UI） | Apage 原创设计（详见 script-manager.md） |
