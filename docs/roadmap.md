# Apage 开发路线图

> 内部文档 | 更新：2026-11-02
>
> 当前执行状态：**S0–S3 功能包已完成，P0/P1 硬化与自动 S4 验证已落地；正式打包通过，但首启仍超 1s，完整发布未放行**。干净工作区 `137bddb` 产物 **4,068,352 字节 / 1 文件**，Core 单测 **305 绿**，五条红线、五信号 smoke、数据往返与布局/NTP 探针通过。正式单文件 3 轮：首启 1026.2ms、热启中位 959.25ms（门禁 <1000ms，仅热启口径）；仍需参考机、真机点击、exFAT 与签名复验。
>
> 剩余任务以 [v0.1 发布收敛计划](design/2026-10-28-v01-release-convergence.md) 第九节为执行清单；P0/P1 硬化见 [硬化记录](design/2026-11-02-p0-p1-hardening.md)，Cookie 探针见 [结果](spikes/results/cookie-privacy.md)，S4 启动复验见 [记录](design/2026-11-02-s4-startup-recheck.md)。
>
> v1.6 变更（P0/P1 硬化，2026-11-02）：
> - **第三方 Cookie 隔离下架**：真机探针证明 `WebResourceRequested` 删除 Cookie 后网络栈仍重新附加；字段默认 false，设置/追踪统计/NTP/隐私盾与宣传同步移除
> - 请求管线区分顶层主文档与 iframe Document；下载按 id 串行、终态不可回退；单条取消不再全表清理
> - 会话恢复完成前关闭不覆盖 session；设置/会话/统计统一 `AtomicJsonStore`
> - 规则更新保留用户规则、全程串行、逐跳验证 HTTPS；NTP 严格校验 `e.Source`，URL 去除 userinfo，日志原子导出，ABP authority 归一
> - 修复红线脚本根路径误判；Core 单测 305 项
>
> v1.5 变更（S3 收尾与 S4 启动复验，2026-11-02）：
> - S3 余项完成：Ctrl+F 页内查找、Toast 改 Popup 解决 airspace，smoke 增加查找/提示布局探针
> - 单文件 4,060,160 字节（4.25MB 余 189,840 字节 / 4.5%）；287 项单测
> - 启动复验：负载条件下首启 1077.5ms、热启中位 939.15ms，条件通过；首启仍超 1s，正式证据需参考机
> - 仓库整理为 9 个垂直提交；修复 `.gitignore` 误吞 `Apage.Core/Data` 与 `docs/release` 的缺陷
>
> v1.4 变更（B2 数据切片 + A3 规则手动更新，2026-10-29）：
> - B2：书签/历史/下载仓储、Chrome/Edge 书签 HTML 导入、`LibraryWindow` 三页签、Ctrl+D/H/J（应用界面内）；隐私标签不记历史，下载中断区分取消/失败
> - A3：规则快照构建器统一筛选口径，`{数据}\privacy` 原子替换 + 失败回滚；仅设置按钮触发 HTTPS 更新，红线对 `RuleUpdateClient.cs` 精确豁免
> - 单文件 4,035,584 字节（4.25MB 余量 5.0%）；260 项单测；smoke 增加真实数据往返
>
> v1.3 变更（D2：发布工程，2026-10-29）：
> - `package.ps1` 串联许可校验、4.25MB/5MB 硬门禁、五信号 smoke、单文件启动基准、SHA-256、legal bundle 与 release manifest
> - 版本元数据固定为 0.1.0.0 / 0.1.0-dev；当前产物 4,035,584 字节
> - 启动基准支持 `-ExePath` 直接测正式单文件；本机首启 882.6ms、热启中位 802.3ms（<1000ms）
> - SignPath 申请与签名后复验手册已准备；实际签名、签名后 smoke/体积/启动复验仍属 S4
>
> v1.2 变更（A2：PSL + 离线规则快照，2026-10-29）：
> - 随包 PSL（2026-10-28，MPL-2.0）替换末两标签近似；普通/通配/例外、IDN、尾点与安全回退均有测试，`foo.co.uk` vs `bar.co.uk` 现正确判第三方
> - 内置 EasyList/EasyPrivacy 快照 94,646 条可执行规则（CC BY-SA 3.0），跳过 38,432 条并记录原因；加载前校验 SHA-256，用户规则独立加载
> - 单文件增至 3,970,560 字节（4.25MB 余量 6.6%）；smoke 扩展为五信号，新增 PSL/规则加载验证
> - 运行时仍零自动规则更新；A3「立即更新规则」未启动
>
> v1.1 变更（S2 第一批 D1/A1/B1/C1，2026-10-29）：
> - D1：Fody/Costura + `NativeBootstrap` 接入生产；`build\package.ps1` 单文件 3,068,928 字节，SHA-256 `0BE6F45B…C73F99`
> - A1：不支持 `$` 修饰符的规则 fail-closed 拒收；统计 2 秒批量落盘；仅实际移除 Cookie 才计追踪
> - B1：SQLite schema v1、事务迁移、迁移前备份、完整性检查与损坏不删库；初始化在窗口显示后后台执行
> - C1：真实设置窗口（工具栏菜单 + NTP 入口）、滚动本地日志与手动导出；smoke 增加设置窗口构造检查
> - smoke 扩展到四信号：应用 / 窗口 / WebView2 / SQLite；152 项 Core 测试、五条红线、Release 构建全部通过
>
> v1.0 变更（隐私统计 + 新标签页 + 隐私设置消费方接线；设计见 [design/2026-10-23-privacy-stats-ntp.md](design/2026-10-23-privacy-stats-ntp.md)）：
> - 隐私拦截统计落地：Core 新增 PrivacyStatsService / PrivacyStatsState（周一 00:00 本地为周边界、原子写、损坏回落、锁内自增），持久化到 {数据目录}\privacy-stats.json，正常退出时保存；单测累计 131 绿（新增 38）
> - 新标签页接线：Assets/webui 全套（壁纸/搜索/快捷方式/时钟/统计卡）抽取到 {数据目录}\webui 并经 file:// 导航；空白标签即开新标签页；会话保存排除新标签页（与 about: 同等对待）；ntp 通道 WebMessage 桥（navigate / getStats / openSettings / addShortcut）带 Source 门控——任意网页均可 postMessage，不校验 Source 等于把宿主能力暴露给所有站点
> - 隐私设置从「基线」变「事实」（历史）：DNT / Sec-GPC 头注入、WebRTC IP 防护经 AdditionalBrowserArguments 生效；第三方 Cookie 请求侧剥离已于 2026-11-02 经探针证伪并移除
> - 统计口径（历史，2026-11-02 已废止追踪数）：广告数 = 规则拦截次数；原“追踪数 = 第三方 Cookie 剥离次数”经真机证伪后移除；崩溃丢失当次计数（退出才落盘）已明示
> - 体积补测（Spike 4 兑现）：当时便携目录 2.99MB / 30 文件 < 5MB；2026-10-29 S1 完成独立单文件验证，随后 D1 完成生产接入（见顶部 v1.1）
> - Phase 0 补记：WebMessageSpike 历史运行 FAIL 归因为脚手架缺陷（对 async 函数未取回 Promise resolve 值，SDK 只做结果 JSON 编码），非通信链路缺陷，修法见 [spikes/results/webmessage.md](spikes/results/webmessage.md)；ExtensionSpike 无运行日志，MV3 实测全部标注未确证，见 [spikes/results/extension.md](spikes/results/extension.md)——两个 spike 维持未勾选待补跑（**已于 2026-10-26 补跑转绿，见下一条与 Phase 0 勾选**）
> - Phase 0 补跑（2026-10-26）：WebMessage 六断言全绿 PASS（A2 并发 20 乱序 20/20 正确关联，resolutionOrder 严格逆序；100 次往返 p95=0.2ms、max=3.3ms；document-start 注入桥可用）；ExtensionSpike 补 `AreBrowserExtensionsEnabled=true`（默认 false，不开则 0x80070032 一律拒绝）后全矩阵跑通——MV3 可装/可枚举/可启停/可卸载，**最小 MV2 亦被接受**，「仅 MV3」口径按预案修正为「本地侧载扩展，须 Environment 创建时显式开启」（决策 #5、product-design 痛点 5/R3、Phase 8 同步更新）
>
> v0.9 变更（开发者向规划：F12 DevTools + 反调试过滤）：
> - 新增 Phase 7d「开发者工具」：完备 F12 调试控制台（WebView2 原生 Chromium DevTools）+ 反调试过滤（`debugger` 中和 / DevTools 探测反制 / console 过滤）
> - 决策记录新增 #19（DevTools 默认开启，贴合开发者）/ #20（反调试过滤默认关 + 站点级开关 + 注入序前置）/ #21（反调试过滤产品边界：调试辅助而非破解）
> - 风险表新增 R14（反调试过滤误伤/滥用边界）、R15（WebView2 DevTools API 限制：独立窗口无停靠、已开即 no-op、无法编程聚焦）
> - AppSettings 规划新增 `EnableDevTools`（默认 true）/ `FilterAntiDebug`（默认 false）两组开关（细节见 product-design §5.8）
> - 前端：工具栏「开发者」菜单 + 设置面板开发者工具区 + 右键「检查」入口规划（frontend-design §11）
>
> v0.8 变更（UI 夸克式打磨 · 首轮 + 实机验证）：
> - 图标体系：新增 Themes/Icons.xaml（14 个 StreamGeometry 线性图标 + 图标 Path 样式），窗口按钮/工具栏/标签条的 ASCII/Emoji 占位（←→⟳🛡⬇📜⋯ ─▢✕ ✕ +）全部换为矢量线性图标（frontend-design §10.5 落地）
> - 网页图标 favicon：BrowserTabView 订阅 CoreWebView2.FaviconChanged + GetFaviconAsync 解码为 ImageSource → BrowserTab.Favicon → TabStrip 绑定；取网页自身声明的 favicon（非第三方图标服务），零额外联网；无图标回落占位块
> - 最大化边缘裁切修复：拦截 WM_GETMINMAXINFO 把最大化限定到显示器工作区，消除自定义标题栏窗口最大化 ~7px 裁切且不遮挡任务栏（含 try/catch 兜底，钩子异常绝不崩溃）
> - 搜索框聚焦修复：Omnibox 内层 TextBox 改用“裸”模板，消除应用级 TextBox 隐式样式在圆形胶囊内叠出的方形聚焦边框；补 accent 文本选区高亮
> - 推进方式：先出静态走查稿（.uimock/index.html）对齐夸克方向后落地；保留蓝色令牌，只借夸克的圆润/留白/线性图标；已用 ComputerUse 真机点击复验 favicon / 搜索框聚焦 / 最大化三项均正常
> - 后续状态：新建标签追加/滚入视区、拖拽排序、标签休眠已完成；F11 全屏与地址栏聚焦/选区微调移至 v0.1.x / v0.2，不阻塞当前发布闭环
>
> v0.7 变更（会话保存/恢复）：
> - 新增 Core 层 SessionService + SessionState（原子写、损坏/缺失回落空会话、URL 可恢复过滤、索引收敛），配 17 项单测（决策 #18）
> - 退出保存打开的标签、启动恢复上次标签；隐私标签在 CaptureSession 处即排除（roadmap 红线），BrowserTab 加 IsPrivate 结构性保障；恢复时用保存的标题占位（后台隐藏标签加载前也显示正确标题）
> - Phase 2「会话保存」与 Phase 3「会话恢复」提前一并落地（急加载式恢复；懒加载/休眠归 Phase 2「标签休眠」）
>
> v0.6 变更（Phase 1 完成 + Phase 2 多标签会话落地）：
> - App 启动装配：单实例判定（R13）+ 设置加载 + R7 缓存策略灌入 AppPaths + 依赖注入 MainWindow（此前 Core 服务已测但未接入运行时，现已实际生效）
> - Omnibox 接入主窗口：回车导航、URL/搜索智能识别、地址回填；窗口标题随页面 DocumentTitle 同步（Phase 1 收尾两项完成）
> - Phase 2 多标签会话：TabManager 驱动真实标签（每标签一个 BrowserTabView，经 BrowserLifecycleService 共享同一 Environment，仅当前可见），新建/关闭/切换已接入（关最后一个标签即关窗口）；拖拽排序 / 休眠 / 会话保存仍待做
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

- [x] Spike 1：ABP 规则引擎原型（纯控制台）——50 万条规则的匹配耗时与内存，验证子串索引方案（对应 R4）→ [spikes/results/adblock.md](spikes/results/adblock.md)：平均 24µs/URL、较逐条扫描约 7,500 倍提速、100ms 正则超时兜住灾难性回溯
- [x] Spike 2：冷启动实测——真实 `Apage.Portable` 在参考环境下已验证「窗口可见 <1 秒」可达，热启动中位 900.3ms；首启样本 978.4ms / 1,016.1ms 且存在更高波动，**不是全环境稳定达标**，最终签名单文件仍须在 S4 复测（对应 R5，证据见 [spikes/results/single-file.md](spikes/results/single-file.md)）
- [x] Spike 3：扩展能力摸底——实测 `AddBrowserExtension` 可装/不可装的 API 范围，确定对外口径（对应 R3）→ [spikes/results/extension.md](spikes/results/extension.md)：2026-10-26 实测——`AreBrowserExtensionsEnabled` 默认 false（不开则 0x80070032 一律拒绝）；开启后最小 MV3 可装/可枚举/可启停/可卸载，**最小 MV2 亦被接受**，口径修正为「本地侧载扩展（须 Environment 创建时显式开启），API 面小、无商店直连」
- [x] WebMessage 通信链路验证——`WebMessageReceived` ↔ `postMessage` 请求/响应 id 关联，覆盖注入脚本场景 → [spikes/results/webmessage.md](spikes/results/webmessage.md)：2026-10-26 补跑 **PASS（exit 0）**——A1–A6 全绿：A2 并发 20 乱序 20/20 正确关联；100 次往返 p95=0.2ms；document-start 注入桥可用；错误路径后通道可复用；pending 零泄漏
- [x] Spike 4：体积预算表——逐依赖称重（e_sqlite3.dll ~1.5MB、规则快照、PSL、WebView2 SDK 托管部分），验证 <5MB 可达或据实调口径（对应 R12）→ S0 初始便携目录 2.99MB / 30 文件；2026-10-29 S1 独立单文件 spike 实测 **2,950,144 字节 / 1 文件**，含 WPF/Core/WebView2/SQLite，距 5MB 硬门禁余 2,049,856 字节。PSL、规则快照、许可文本和生产 UI 仍需在 S2 持续称重，见 [spikes/results/single-file.md](spikes/results/single-file.md)
- [ ] Spike 5：GM 存储内联注入验证——注入时把脚本存储 KV 序列化内联进 IIFE，验证 GM_getValue 同步语义无「缓存未就绪」窗口（对应决策 #14）

**交付物**：v0.1 前五项发布相关验证结论已齐备；Spike 5 是 v0.1 后用户脚本内核的前置验证，不再阻塞 v0.1 发布闭环。

---

## Phase 1：骨架 + 单标签（便携版优先，1-2 周）

**目标**：能打开网页（便携版 Apage.Portable）

**技术栈**：.NET Framework 4.8 + WPF + WebView2（系统自带运行时，真即插即用）

- [x] 解决方案初始化：Apage.Core（.NET Standard 2.0）+ Apage.Portable（.NET FX 4.8 + WPF）
- [x] Apage.Core 设定 `<LangVersion>latest</LangVersion>`（netstandard2.0 目标仍可用 C# 8+ 语法糖；record/init 补 IsExternalInit polyfill）
- [x] 单标签浏览（前进/后退/刷新/地址栏）——BrowserTabView 导航契约 + 工具栏转发已完成；Omnibox 已接入主窗口（回车导航、URL/搜索智能识别、地址回填）
- [x] 窗口标题同步（页面 DocumentTitleChanged → 窗口标题）
- [x] PerMonitorV2 DPI 清单
- [x] WebView2 运行时检测与缺失引导（R1；注册表探测 + 中文引导面板，手动下载，绝不静默联网下载）
- [x] 缓存位置策略：默认宿主机 %TEMP%，可选纯便携模式（R7）
- [x] 单文件技术验证（独立 spike）——2026-10-29 实测 **2,950,144 字节 / 1 文件**，WPF/Core/WebView2/SQLite 可用，损坏原生缓存可自愈、只读程序目录可运行；该结果不代表生产打包完成
- [x] 生产 `Apage.Portable` 单文件接入与发布实测——D1 完成打包链；当前 4,068,352 字节 / 版本 0.1.0.0。最终签名与签名后复验仍归 S4

**交付物**：能输入 URL 并浏览网页的单文件 EXE（便携版，U盘即插即用）

> **说明**：v0.1~v1.0 单押便携版；桌面版（Apage.Desktop，.NET 8 自包含）暂缓，仅 PMF 验证后条件启动（见 Phase 8）。Apage.Core 保留分层以备未来衍生。

---

## Phase 2：多标签（1 周）

**目标**：多标签可管理

- [x] 标签页创建/关闭/切换——TabManager 驱动真实标签会话：每标签一个 BrowserTabView、经 BrowserLifecycleService 共享同一 Environment、仅当前可见；+/✕/点击切换已接入（关最后一个标签即关窗口）
- [x] 拖拽排序——TabStrip 事件 → TabManager.MoveTab，仅调整集合并保持内核宿主不变
- [x] 标签休眠（非活跃标签释放内存）——非当前标签空闲 10 分钟释放内核，激活时懒物化；会话恢复同样仅物化当前标签
- [x] 单实例管理——SingleInstanceService：Mutex 名 = "Apage/" + 数据目录 SHA256 前 16 位，initiallyOwned:false + WaitOne(0) 零等待检测，Abandoned 自动接管
- [x] 会话保存（退出时记录打开的标签）——SessionService 原子写 session.json；隐私标签在 CaptureSession 处排除

**交付物**：多标签浏览，非活跃标签自动休眠

---

## Phase 3：数据层（1 周）

**目标**：数据可持久化

- [x] SQLite 初始化 + 迁移（journal_mode=DELETE、synchronous=FULL、完整性检查 + 迁移前备份，对应 R6）——B1 完成安全基座：schema v1、`user_version` 事务迁移、迁移失败回滚、损坏不删库；B2 已补齐书签/历史/下载仓储、导入与界面
- [x] 书签 CRUD + 从 Chrome/Edge 导入（HTML）——B2：扁平书签、HTML 导入与列表管理已落地
- [x] 历史记录 + 地址栏本地补全——B2 历史记录；S3 地址栏本地建议（书签优先 + 历史补充）
- [x] 下载管理——B2：进度、完成/失败/取消、打开文件与目录；隐私标签不记历史
- [x] 会话恢复（启动时恢复上次标签；**显式排除隐私标签 URL，绝不写入会话文件**）——随会话保存一并落地：SessionService.Normalize 过滤不可恢复 URL + 收敛索引，BrowserTab.IsPrivate 结构性排除隐私标签
- [x] 单实例语义按数据目录判定（已随 Phase 2 单实例管理一并落地：Mutex 名含数据目录哈希，同 U盘插不同机器互不冲突，对应 R13）

**交付物**：书签、历史、下载、会话恢复可用

---

## Phase 4：新标签页 + 壁纸（1 周）

**目标**：Apage 的视觉差异化

> UI 规范以 [frontend-design.md](frontend-design.md) 为准（设计令牌、新标签页渲染策略、动效与状态规范）；新标签页采用本地 HTML/CSS 实现，经 WebMessage 取隐私统计数据。

- [x] 新标签页 UI（壁纸+搜索+快捷方式+时钟）——v1.0 接线：webui 资产抽取到 {数据目录}\webui，空白标签经 file:// 导航；搜索/快捷方式经 ntp 桥交宿主导航（Source 门控）
- [x] 壁纸设置（选择本地图片 / 拖拽设置）——6 张内置渐变 + 本地图片选择 + 拖入即设；localStorage 持久（file:// 下禁用时自动降级）
- [x] 搜索引擎切换——新标签页内循环切换（百度→必应→Google）；Omnibox 引擎仍取设置项 SearchEngine
- [x] 隐私统计卡片（本周广告拦截计数，纯本地数据）——v1.0 起按周（周一 00:00 本地）统计广告规则拦截；2026-11-02 起移除无有效能力支撑的“追踪”计数，经 getStats 桥实时下发；openSettings/addShortcut 桩待 Phase 6/4 后续

**交付物**：带壁纸与隐私统计的新标签页，拖拽即可设置壁纸

---

## Phase 5：隐私 + 广告拦截（2 周）

**目标**：隐私保护核心能力（v0.1 范围 = 网络层拦截；化妆规则后置 Phase 7+）

- [x] ABP 规则解析引擎（子串索引；规则正则统一 MatchTimeout 防 ReDoS，对应 R8）——v0.6 落地，spike 实证见 [spikes/results/adblock.md](spikes/results/adblock.md)
- [x] WebResourceRequested 拦截（网络规则）——带 RequestSourceKinds 三参过滤器（跨源 iframe 不漏拦）；主文档不拦
- [ ] 第三方 Cookie 控制——**v0.1 不交付**：真机探针证明 `WebResourceRequested` 删除 Cookie 后 WebView2 网络栈仍会重新附加（见 [cookie-privacy](spikes/results/cookie-privacy.md)）；不保留死开关或统计口径，后续仅在 Runtime 提供可验证平台入口时重启
- [x] DNT/Sec-GPC 头注入
- [x] WebRTC IP 泄露防护（disable_non_proxied_udp，按站点开关）——环境级参数已生效；按站点开关仍待
- [x] 内置离线规则快照——A2：EasyList/EasyPrivacy 兼容子集 94,646 条，来源/日期/哈希/许可/跳过报告完整，PSL 与规则加载前校验
- [x] 规则更新机制：手动「立即更新」；v0.1 不做启动/定时/静默自动检查（A3：HTTPS、限时限大小、原子替换、失败保留旧规则；未点击时运行时零联网）
- [x] 规则列表许可合规（R11）——A2：PSL MPL-2.0；EasyList/EasyPrivacy 过滤快照 CC BY-SA 3.0；许可全文与归属随包

**交付物**：网络层广告拦截可用，隐私保护默认全开

---

## Phase 6：打磨 + 发布（1-2 周）

**目标**：v0.1 首次发布

- [ ] 暗黑模式（Smart）——移至 v0.1.x / v0.2，不阻塞 v0.1
- [x] 设置面板——C1 完成真实窗口，工具栏菜单与 NTP `openSettings` 已接通；保存失败回滚，无死开关
- [x] Ctrl+F 页内查找——S3 完成：Popup 规避 WebView2 airspace，输入去抖、Enter/Shift+Enter、Esc 关闭，附布局探针
- [x] 本地滚动日志 + 手动导出（R10）——C1 完成；默认脱敏 URL/Cookie、异常不写 Message、IO 失败不拖垮应用
- [x] 单文件发布（portable EXE）——D1 完成 `build\package.ps1`；当前 4,068,352 字节 / 版本 0.1.0.0
- [ ] 代码签名（SignPath.io，对应 R9）+ 签名后同产物复验——D2 已准备申请/工作流/验证脚本，实际签名归 S4
- [ ] 测试 + 修复
- [ ] 首次发布 v0.1

**交付物**：可分发、已签名的 portable EXE，功能完整的 MVP

---

## Phase 7+：完整版迭代（便携版）

**目标**：逐步补齐高级功能（便携版 Apage.Portable）

- [ ] 完整 ABP 引擎（正则 + 化妆规则 + 右键标记广告）
- [ ] **开发者工具（F12 调试控制台 + 反调试过滤）**——详见 Phase 7d 与 [product-design.md §5.8](product-design.md#58-开发者工具f12-调试控制台--反调试过滤)，贴合开发者日常调试
- [ ] **用户脚本管理器**（详见 [script-manager.md](script-manager.md)）——一级差异化，优先级最高
  - [ ] **最小内核（v0.1 后启动）**：先补 Spike 5，再做脚本导入 + URL 匹配 + 基础 GM_（getValue/setValue/addStyle）；不得反向挤占 v0.1 发布闭环
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

## Phase 7d：开发者工具（F12 调试控制台 + 反调试过滤）（1 周）

**目标**：让 Apage 贴合开发者日常——完备的 F12 调试控制台 + 一键过滤反调试干扰（详见 [product-design.md §5.8](product-design.md#58-开发者工具f12-调试控制台--反调试过滤)）

**背景（诚实口径）**：WebView2 自带完整 Chromium DevTools（与 Edge 同源，Elements/Console/Sources/Network/Performance 全量），「完备」不等于自研——外壳层要做的是把原生能力正确接出（入口/开关/每标签管理）+ 补上 WebView2 壳层没有的「反调试过滤」。F12 / Ctrl+Shift+I / 右键「Inspect」在内核启用 DevTools 时**开箱即用**（`AreDevToolsEnabled` 默认 true），外壳只需同步状态与补菜单。

- [ ] F12 / Ctrl+Shift+I / 右键「检查」开箱即用验证（内核内置，`AreDevToolsEnabled=true` 即可）；工具栏「开发者」菜单补 `OpenDevToolsWindow()` 程序化入口
- [ ] DevTools 生命周期按标签管理：每标签独立 DevTools 窗口（WebView2 无停靠面板 API，独立窗口即最终形态，R15）；标签关闭随内核销毁自动关闭；重复打开为官方 no-op（记录已开状态，避免无谓调用）
- [ ] AppSettings 新增 `EnableDevTools`（默认 true，贴合开发者）；关闭后 `AreDevToolsEnabled=false`，F12/菜单入口同时失效，设置面板可见
- [ ] 反调试过滤（`FilterAntiDebug`，**默认关** + 站点级记忆 + 工具栏一键开关）：
  - [ ] `debugger` 中和（主战场=动态代码）：document-start shim 包裹 `Function` 构造器与 `eval`，剥离代码字符串中的 `debugger` 关键字；保留原生 `toString` 形貌防自检
  - [ ] DevTools 探测反制：debugger 计时差探测随中和自然失效；独立窗口天然免疫窗口尺寸差探测
  - [ ] console 过滤：按级别/正则过滤、重复折叠、防刷屏（DevTools Console 自带级别过滤为基线）
  - [ ] 注入序：反调试 shim 先于用户脚本（script-manager §10 集成）；独立命名空间，不向页面暴露符号
- [ ] 边界红线（决策 #21）：仅调试辅助，**不提供**绕过登录/DRM/付费校验/验证码等破解能力；默认关是结构性保障（R14）
- [ ] 键盘焦点协调：F12 由内核处理，外壳侧快捷键（若补）经 KeyboardService 统一注册，与 F11 全屏、Ctrl+F 查找不冲突
- [ ] 单测目标（Core 纯逻辑，决策 #18）：`AntiDebugShimBuilder`（`debugger` 剥离的源码变换纯函数 + toString 形貌保持）、`ConsoleFilter`（级别/正则规则）、站点级开关记忆的 URL 键归一

**交付物**：F12 随时调出完整 DevTools；对反调试站点一键中和，调试不再被 `debugger;` 打断

---

## Phase 8：桌面版衍生（Apage.Desktop）— 暂缓，条件启动

**状态（v0.4）**：**暂缓，非既定交付**。仅在便携版验证 PMF（v1.0+ 稳定、用户明确需要 MV3 扩展等运行时绑定能力）后再评估启动。桌面版"独有"卖点中仅「部分 MV3 扩展」与运行时/WebView2 版本强绑定，其余（翻译/暗黑增强/AvalonEdit）便携版亦可实现。

**目标**：基于 Apage.Core 共享层，衍生 .NET 8 自包含桌面版

**前提**：便携版已稳定（v1.0+），Apage.Core 接口边界清晰，且 PMF 验证支持第二产品线的维护成本

- [ ] Apage.Desktop 项目初始化（.NET 8 + WPF + 自包含发布）
- [ ] 共享 Apage.Core（验证 .NET Standard 2.0 引用兼容性）
- [ ] 共享 ResourceDictionary（文件链接）
- [ ] 脚本编辑器升级为 AvalonEdit（完整版，~2MB 增量可接受）
- [ ] Chrome 扩展（本地侧载；MV3/MV2 均实测可装，须 `AreBrowserExtensionsEnabled=true`，见 [spikes/results/extension.md](spikes/results/extension.md)）——桌面版独有
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
| 3 | 数据存储 | 新增书签/历史/下载使用 SQLite；设置/会话/周统计继续 JSON | 结构化数据需要事务与查询；小状态原子写已足够，v0.1 不做同期旧数据迁移 |
| 4 | 广告拦截 | ABP 引擎 | 规则兼容，社区生态 |
| 5 | 扩展支持 | 用户脚本优先；本地侧载扩展（v0.1 不含） | WebView2 扩展为本地侧载、须 Environment 创建时显式开启 `AreBrowserExtensionsEnabled`、无商店直连；2026-10-26 实测 MV3/MV2 最小样本均可装，「仅 MV3 子集」旧口径不成立（见 [spikes/results/extension.md](spikes/results/extension.md)）；GM_* 才是真差异化 |
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
| 19 | 开发者工具默认 | DevTools 默认开启（F12 开箱即用），反调试过滤默认关 | 目标用户含开发者，F12 是刚需；反调试过滤会改页面行为且可能被用于不正当用途，默认关 + 站点级显式开启才安全（决策 #20、R14） |
| 20 | 反调试过滤 | document-start 注入序前置的 shim，以动态代码中和为主 | `debugger` 剥离只能在代码执行前介入：`eval`/`Function` 构造器可包装中和；静态脚本内字面 `debugger;` 无法改写（如实标注，不承诺）；默认关 + 站点级记忆 + 一键开关 |
| 21 | 反调试过滤产品边界 | 仅调试辅助，不提供破解能力 | 与「隐私默认全开」同为产品红线：不做绕过登录/DRM/付费校验/验证码；「默认关」是边界的结构性保障 |
| 22 | 第三方域名判定 | 随包 PSL 快照求 eTLD+1；失败回退末两标签 | 精确处理 `co.uk` 等多级后缀；IP/localhost/非法输入仍按漏判方向；v0.1 不联网更新 PSL |
| 23 | 内置规则快照边界 | 仅无 `$` 修饰符的 URL 网络规则；regex/化妆/条件规则跳过并记账 | 引擎不能完整求值的规则不得退化后宽泛拦截；快照按 CC BY-SA 3.0 分发并随包许可 |
| 24 | 第三方 Cookie 防护口径 | v0.1 不提供；旧字段默认关、无 UI/统计/宣传 | 真机探针证明事件内删除 Cookie 后网络栈仍重新附加；宁可诚实缺失，不保留“假防护”。见 [cookie-privacy](spikes/results/cookie-privacy.md) |

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
| F12 DevTools 接入 + 反调试过滤 | WebView2 原生 DevTools（F12/Ctrl+Shift+I/OpenDevToolsWindow）+ Apage 原创注入层（复用脚本注入管线，Phase 7d） |
