# AGENTS.md — Apage 智能体作业指南

> 面向编码智能体的入口。先读本文件，再动手。人类贡献者也适用。
> 一句话：超轻量、隐私优先、全本地化的 Windows 便携浏览器（.NET FX 4.8 + WPF + WebView2）。

## 快速事实

- **主交付物**：`Apage.Portable`（.NET Framework **4.8**，旧式 csproj，`WinExe`，单文件 <5MB，版本 0.1.0.0）。唯一发布线；正式分发由 `build\package.ps1` 产出单 EXE + legal bundle + manifest，`bin\` 仍是多文件调试布局。
- **共享层**：`Apage.Core`（**netstandard2.0**，SDK 风格）。纯逻辑放这里，最该写单测。
- **测试**：`Apage.Core.Tests`（**net8.0** + xUnit，SDK 风格）。
- SDK 版本由 [`global.json`](global.json) 固定为 `8.0.423`。
- `Apage.Desktop`（.NET 8 自包含）**暂缓**，PMF 验证后再评估，勿主动创建。

## 构建（关键：`dotnet build` 不支持整个解决方案）

`Apage.sln` 是「旧式 v4.8 + SDK 风格」混合解决方案，`dotnet build Apage.sln` **会失败**。主项目必须用 **VS 2022 的 MSBuild**。仓库已提供封装脚本（自动经 vswhere 定位 MSBuild、按需设置 `MSBuildSDKsPath` 并关闭 workload 解析器、逐步汇总结果），**这是受支持的构建入口**：

```powershell
# PowerShell（推荐）：Restore + 构建整个解决方案（Release）
build\build.ps1
build\build.ps1 -Configuration Debug
```

```bash
# Git Bash：一条命令验证（内部转调 build/build.ps1）
build/build.sh            # 默认 Release
build/build.sh Debug
```

脚本等价于以下手动步骤（见 [README 开发节](README.md#开发)）：

```powershell
# 方式 A：VS Installer 勾选了「.NET SDK」组件时，直接：
msbuild Apage.Portable\Apage.Portable.csproj /t:Restore,Build /p:Configuration=Release

# 方式 B：VS 缺「.NET SDK」组件时，临时指定 SDK 路径（须用 .NET 8 SDK，10.x 会触发 NETSDK1216）：
$env:MSBuildSDKsPath = "C:\Program Files\dotnet\sdk\8.0.423\Sdks"
$env:MSBuildEnableWorkloadResolver = "false"
msbuild Apage.Portable\Apage.Portable.csproj /t:Restore,Build /p:Configuration=Release
```

`Apage.Core` 与 `Apage.Core.Tests` 是 SDK 风格，`dotnet` CLI 直接可用：

```powershell
dotnet build Apage.Core/Apage.Core.csproj -c Release
```

## 测试

```powershell
dotnet test Apage.Core.Tests/Apage.Core.Tests.csproj
```

```powershell
# 发布线冒烟验证（需先 build\build.ps1 构建成功）：以 --smoke 模式拉起 Apage.Portable，
# 验证 应用可启动 / 主窗口可呈现 / WebView2 内核可初始化 / SQLite 安全初始化 /
# PSL 与内置规则快照加载 五信号，并真实构建设置窗口、执行数据往返、
# Ctrl+F/Popup 布局探针与 NTP getConfig 来源门控探针与首启零非白名单出站探针；CI 在 portable 构建步骤之后自动运行。
build\smoke.ps1
```

```powershell
# 正式单文件打包：许可校验 + 单文件断言 + 4.25MB/5MB 体积门禁 + SHA-256
# + 隔离五信号 smoke + 正式单文件启动基准 + legal bundle + release manifest
build\package.ps1 -RequireCleanWorktree
build\package.ps1 -SkipBuild -RequireCleanWorktree   # 已构建时只重新验收
build\package.ps1 -SkipBuild -SkipStartupBench       # 调试用，不得用于发布
```

- 只测 `Apage.Core` 的纯逻辑（决策 #18：Core 层纯逻辑优先单测）。
- 现有可测目标：`AppPaths`（缓存目录 R7 策略）、`AppSettings`（隐私默认全开基线）、`SettingsService`（原子写 / 损坏 JSON 回落默认）、`SessionService`（会话原子写 / 损坏回落空 / URL 可恢复过滤 + 索引收敛）、`ScriptMetadataParser`（TM 兼容元数据块解析 / 导入校验顺序 / 零网络字段忽略 / @run-at 回落默认）、`ScriptMatcher`（@match/@include URL 匹配 / 排除优先级 / 非法模式丢弃）、`AdBlockRuleEngine`（ABP 规则关键子串索引匹配 / @@ 例外规则 / ## 化妆规则分桶 / 不可信规则防护：超长拒收 + 100ms 正则超时 / 不支持修饰符 fail-closed）、`PrivacyStatsState`（周一边界 / 跨周滚存）、`PrivacyStatsService`（损坏回落 / 原子写 / 跨周归零 / 并发自增 / 短周期批量落盘）、`ThirdPartyDetector`（末两标签近似 / 漏判不误判方向）、`SqliteDatabase`（PRAGMA 基线 / 事务迁移 / 迁移前备份 / 损坏不删库 / 并发初始化）、`LocalLogService`（滚动 / 导出 / URL 与 Cookie 脱敏 / IO 故障不抛）、`PublicSuffixList`（普通 / 通配 / 例外 / IDN / 尾点 / IP 回退）、`PrivacyAssets`（清单哈希 / PSL 版本 / 规则快照可执行性 / 许可资源）、`AdBlockRuleEngine.Classify`（快照与运行时共用规则边界）、`UrlKey`（http/https 判定 / 大小写与默认端口 / IDN 与尾点归一）、`BookmarkRepository` / `HistoryRepository` / `DownloadRepository`（CRUD / 去重 / 搜索转义 / 终态 / 异常退出残留 / 并发写）、`BookmarkHtmlImporter`（Netscape 格式容错 / 实体 / 截断 / 非法链接）、`RuleSnapshotBuilder` / `RuleSnapshotStore` / `RuleUpdateService`（共用筛选 / 原子替换与回滚 / 安全下限 / 失败不改动旧快照 / 用户规则保留 / 并发串行）、`AtomicJsonStore`（Replace/Copy 故障下保留唯一副本 / .bak/.tmp 恢复）、`UserRuleStore`（用户规则统一读取）、`FaviconStore`（主机名归一化 / 文件名不含路径分隔符 / 缺失即回落 / 上线裁剪 / data URL 包装）、`SearchEngineHosts`（引擎主机白名单 / 子域与仿冒拒绝 / 引擎 id → 首选主机）、`FindBarState`（开关 / 计数收敛 / 文案 / 按键语义）、`OmniboxSuggestionService`（书签优先 / 历史补充 / 去重 / 上限）。
- 测试文件与目标一一对应：`Apage.Core.Tests/<目标名>Tests.cs`（如 `ScriptMatcherTests.cs`）。新增 Core 纯逻辑时按此约定补单测。

## 环境要求

- Windows 10/11（WebView2 Runtime 系统自带；Win11 默认预装）。
- .NET 8 SDK（构建 Core / 跑测试，版本经 `global.json` 固定）。
- Visual Studio 2022（含 MSBuild + .NET Framework 4.8 目标包）——构建主项目必需。

动手前先跑只读环境预检，确认当前机器支持哪些构建子集（不改动任何东西）：

```powershell
# 检测 .NET SDK / VS 2022 MSBuild / .NET FX 4.8 目标包，输出可用构建路线：
#   Core+Tests（dotnet CLI）与 Portable 整解（build\build.ps1 + smoke）。
# 退出码：0 全量可用；2 仅部分路线可用；1 无可用构建路线。
build\preflight.ps1
```

## 高风险区域 / 约定（改动前务必知晓）

1. **不要把 `Apage.Portable.csproj` 的通配符 `Compile`/`Page` 展开成逐文件条目**。通配符是故意的，让并行智能体加 `.cs`/`.xaml` 无需改 csproj（见该文件注释）。新增源码/资源直接放目录即可被拾取；`App.xaml` 是 `ApplicationDefinition`，已从 `Page` glob 排除。
2. **隐私默认全开是产品红线**（`AppSettings` 默认值）：`SendDoNotTrack` / `SendGPC` / `WebRTCIPProtection` 默认 `true`，`OnlineSearchSuggestions` / `ClearOnExit` / `BlockThirdPartyCookies` 默认 `false`。`FetchEngineIcons` 默认 `true`：这是**唯一默认开启的出站开关**，范围被 `SearchEngineHosts` 白名单锁死为四个引擎主机，改动其默认值需同步 `docs/product-design.md` §5.2 网络请求清单。`BlockThirdPartyCookies` 是旧字段：真机探针已证伪事件级 Cookie 删除（见 `docs/spikes/results/cookie-privacy.md`），不得重新接线或宣传；改其他默认值等于改产品定位，需先对齐 `docs/product-design.md`。
3. **便携运行时目录**（`data/` `cache/` `wallpapers/`）永不提交（已在 `.gitignore`）。缓存位置遵循风险 R7：默认写宿主机 `%TEMP%\Apage\cache`，纯便携模式才写 exe 同级 `cache/`。
4. **零遥测 / 零后台请求**：不要引入任何遥测、崩溃上报、云同步或“检查更新”联网逻辑。全仓只允许两条出站路径，且都由 `build\check-redlines.ps1` 以**文件级白名单**锁死：`Apage.Portable/Services/RuleUpdateClient.cs`（手动规则更新，按钮触发）与 `Apage.Portable/Services/FaviconFetcher.cs`（搜索引擎图标一次性抓取，仅 `SearchEngineHosts` 白名单主机、本地无缓存时触发、设置可关闭）。新增出站必须同时改产品文档、红线白名单与冒烟门禁。新增任何隐私相关能力必须同步 `docs/product-design.md` §5.2 能力边界声明；WebView2 Runtime 自身的诊断数据不受 Apage 控制（微软文档明示），属已披露边界，不得以「零遥测」笼统宣传。
5. **`Apage.Core` 保持 netstandard2.0**，不要引入依赖 Windows 或 .NET 8 专有 API 的包（否则破坏未来衍生分层）。
6. **`Apage.Core/Resources/Privacy/*` 是生成物**（PSL、`builtin-rules.txt`、`privacy-assets.json`），不要手工编辑；用 `tools\SnapshotBuilder\update-snapshot.ps1` 显式更新并同步 `licenses/` 与体积账本。清单中的 SHA-256 不匹配会导致运行时拒绝加载。

## 文档地图（权威来源）

| 主题 | 文档 |
|------|------|
| 产品设计（竞品、功能、架构细节、隐私基线 §5.2） | [docs/product-design.md](docs/product-design.md) |
| 前端设计（设计系统、UI 规范、交互动效） | [docs/frontend-design.md](docs/frontend-design.md) |
| 路线图（阶段规划、任务清单、风险 R1–R13、决策 #1–#18） | [docs/roadmap.md](docs/roadmap.md) |
| 脚本管理器（用户脚本运行时与管理器） | [docs/script-manager.md](docs/script-manager.md) |
| v0.1 发布收敛计划（后续任务顺序、质量门禁、并行写域；当前优先执行依据） | [docs/design/2026-10-28-v01-release-convergence.md](docs/design/2026-10-28-v01-release-convergence.md) |
| P0/P1 硬化记录（Cookie 能力纠偏、下载/会话/原子写/规则更新修复） | [docs/design/2026-11-02-p0-p1-hardening.md](docs/design/2026-11-02-p0-p1-hardening.md) |
| Cookie 真机探针结论（事件级删除无效证据与复现） | [docs/spikes/results/cookie-privacy.md](docs/spikes/results/cookie-privacy.md) |
| 站点图标本地缓存口径与真机证据（不新增网络请求） | [docs/spikes/results/favicon-cache.md](docs/spikes/results/favicon-cache.md) |
| S4 硬化验证（真实更新、网络采样、SQLite/缓存/只读故障） | [docs/design/2026-11-02-s4-hardening-validation.md](docs/design/2026-11-02-s4-hardening-validation.md) |
| S2 第一批实施记录（D1/A1/B1/C1 证据、限制与下一波） | [docs/design/2026-10-29-s2-first-slice.md](docs/design/2026-10-29-s2-first-slice.md) |
| A2 PSL 与离线规则快照（来源、筛选、许可、体积） | [docs/design/2026-10-29-a2-psl-rule-snapshot.md](docs/design/2026-10-29-a2-psl-rule-snapshot.md) |
| D2 发布工程（打包、启动门禁、许可包、CI） | [docs/design/2026-10-29-d2-release-engineering.md](docs/design/2026-10-29-d2-release-engineering.md) |
| S3 余项：页内查找 Ctrl+F（SDK 核实、Popup 与 airspace、门禁复测） | [docs/design/2026-10-30-s3-find-in-page.md](docs/design/2026-10-30-s3-find-in-page.md) |
| S4 启动复验（2026-11-02 负载条件、单文件证据与剩余风险） | [docs/design/2026-11-02-s4-startup-recheck.md](docs/design/2026-11-02-s4-startup-recheck.md) |
| 发布签名手册（SignPath 申请/工作流/签名后复验） | [docs/release/signing.md](docs/release/signing.md) |
| 单文件/原生依赖/冷启动实测（S1 spike + D1 生产接入证据） | [docs/spikes/results/single-file.md](docs/spikes/results/single-file.md) |
| 设计决策记录（design/ 目录，按日期记录设计与接线决策；首篇 privacy-stats-ntp） | [docs/design/2026-10-23-privacy-stats-ntp.md](docs/design/2026-10-23-privacy-stats-ntp.md) |

## 交付前自检

- [ ] `dotnet test Apage.Core.Tests` 通过。
- [ ] `build\build.ps1` 主项目构建成功（需 VS 2022 环境）。
- [ ] `build\smoke.ps1` 冒烟通过（应用 / 窗口 / WebView2 / SQLite / PSL 与规则快照 + 窗口构造 + 书签历史数据往返 + 页内查找会话与查找条布局探针）。
- [ ] `build\check-licenses.ps1` 通过（MIT/MPL/CC 文本与隐私资产哈希）。
- [ ] `build\package.ps1 -SkipBuild` 单文件打包通过（1 文件、4.25MB/5MB 门禁、五信号 smoke、启动基准、SHA-256）。
- [ ] `build\verify-signature.ps1` 已记录签名状态；发布前必须以 `-RequireValid` 通过。
- [ ] 未改动通配符 csproj、未引入遥测、未改隐私默认值（除非明确要求）。
