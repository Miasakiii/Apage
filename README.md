# Apage

> 一个页面，干净打开。

超轻量、隐私优先、全本地化的 Windows 便携浏览器。

## 特性

- **极致轻量** — 发布目标为 x64 单文件 EXE、签名后 <5MB、窗口可见 <1秒
- **隐私默认全开** — DNT/GPC、WebRTC 防护与 EasyList/EasyPrivacy 兼容快照默认启用；所有隐私声明以可复现验证为准
- **全本地化** — 零遥测、零后台请求、零数据收集
- **无需登录** — 不强制账号，不要求云同步
- **Portable** — 数据跟程序走，U盘即插即用（前提：目标机已装 WebView2 Runtime，Win11 默认预装）
- **无广告** — 浏览器本身无任何广告或推广
- **壁纸新标签页** — 支持自定义静态壁纸，拖拽即设置

## 发布现状（2026-11-02）

- **S0–S3 功能包已完成**（D1/A1/B1/C1/A2/D2/B2/A3/S3），P0/P1 硬化切片已落地：**305 项 Core 单测**、五条红线、Release 构建、五信号 smoke、真实数据往返、查找条与提示条布局探针通过。
- **隐私承诺纠偏**：真机探针证实 `WebResourceRequested` 删除 Cookie 后 WebView2 网络栈仍会重新发送，**“第三方 Cookie 剥离/追踪计数”已从能力、设置、NTP/隐私盾与宣传中移除**；旧字段仅保留兼容且默认 `false`。证据见 [Cookie 探针](docs/spikes/results/cookie-privacy.md) 与 [P0/P1 硬化记录](docs/design/2026-11-02-p0-p1-hardening.md)。
- **生产单文件与发布门禁**：`build\package.ps1` 产出 **1 文件 / 4,068,352 字节** 的 `Apage.Portable.exe`（版本 0.1.0.0，SHA-256 `573E5F52…9E582FEE`），并生成 release manifest 与独立 legal bundle。本轮打包为 `-SkipStartupBench` 调试验收；尚未签名，最终干净工作区打包、启动门禁、签名与签名后复验属 S4。
- **隐私资产已闭环**：PSL `2026-10-28` 版本；内置 EasyList/EasyPrivacy 快照 **94,646 条**通过 SHA-256 校验，MIT/MPL/CC 许可全文随包；运行时零自动更新请求。
- **启动指标（2026-11-02，负载条件）**：首启 ContentRendered **1077.5ms**，热启中位 **939.15ms**，在开机后约 **33%–36% CPU** 占用下以 60.85ms 余量通过本机 1000ms 门禁。该结果**不是安静参考机结论**，首启仍超过 1 秒；正式发布须在稳定参考环境复测。
- **数据、查找与规则更新已闭环**：书签/历史/下载落 SQLite（隐私标签不记历史），支持 Chrome/Edge 书签 HTML 导入；地址栏本地建议与 Ctrl+F 页内查找可用；规则更新仅在设置里手动点击触发，逐跳 HTTPS、全程串行、校验后原子替换，热更新保留 `adblock/*.txt` 用户规则。
- **证据与后续顺序**：见 [P0/P1 硬化记录](docs/design/2026-11-02-p0-p1-hardening.md)、[S4 硬化验证](docs/design/2026-11-02-s4-hardening-validation.md)、[Cookie 探针](docs/spikes/results/cookie-privacy.md)、[S4 启动复验记录](docs/design/2026-11-02-s4-startup-recheck.md)、[B2/A3 实施记录](docs/design/2026-10-29-b2-data-a3-rule-update.md)、[D2 发布工程记录](docs/design/2026-10-29-d2-release-engineering.md)、[签名手册](docs/release/signing.md)、[A2 实施记录](docs/design/2026-10-29-a2-psl-rule-snapshot.md)、[S2 第一批实施记录](docs/design/2026-10-29-s2-first-slice.md)、[S1 验证结果](docs/spikes/results/single-file.md) 与 [v0.1 发布收敛计划](docs/design/2026-10-28-v01-release-convergence.md)。

## 技术栈

单押便携版（Apage.Portable）为唯一发布线；Apage.Core 保留分层以备未来衍生。桌面版（Apage.Desktop）暂缓，PMF 验证后条件启动。

| 层级 | 便携版（Apage.Portable，主力） |
|------|------------------------------|
| 运行时 | .NET Framework 4.8（Win10/11 系统自带） |
| 体积 | 发布目标：签名后 <5MB；当前未签名产物实测 4,068,352 字节单 EXE |
| 定位 | U盘即插即用（前提：已装 WebView2 Runtime） |
| 共享层 | Apage.Core（.NET Standard 2.0，为未来桌面版衍生预留） |
| UI | WPF（当前 code-behind；MVVM 分层为预留，当前未引入 MVVM 包） |
| 引擎 | WebView2（系统自带 Chromium） |
| 通信 | WebMessage（异步，非阻塞） |
| 存储 | 设置/会话/周统计 JSON；SQLite schema v1 与书签/历史/下载 CRUD 已就绪 |

> **实现现状口径**（克制说明，非缺陷清单）：
> - **存储**：设置/会话/周统计继续 JSON（`data\settings.json`、`session.json`、`privacy-stats.json`）；SQLite `apage.db` 已具备 v1 schema、事务迁移、迁移前备份和损坏保护，书签/历史/下载 CRUD 与 HTML 导入已落地（见 [roadmap Phase 3](docs/roadmap.md)）。
> - **UI**：当前为 WPF code-behind（视图与接线在 Apage.Portable 层）；**MVVM 分层为预留**，当前未引入 `CommunityToolkit.Mvvm`（全仓零引用，首发前已移除）。Apage.Core 已按 ViewModels/Models/Services 分层、纯逻辑可单测，便于后续向 MVVM 演进而不必推倒重来。

> 桌面版（暂缓，未来可选）：.NET 8 自包含 ~50MB，完整体验。仅在便携版验证 PMF 后再评估，见 [roadmap Phase 8](docs/roadmap.md)。

## 架构

单押便携版：Apage.Portable 为唯一发布线，Apage.Core 保留分层以备未来衍生。

```
┌──────────────────────┐  ┌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌┐
│  Apage.Portable      │  ┆  Apage.Desktop       ┆
│  .NET FX 4.8 + WPF   │  ┆  .NET 8 + WPF        ┆
│  单文件 <5MB（目标） │  ┆  ~50MB（暂缓/未来）  ┆
│  U盘即插即用         │  ┆  PMF 验证后再评估    ┆
└──────────┬───────────┘  └╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌┘
           │
           ▼
┌──────────────────────────────────────────────────┐
│  Apage.Core (.NET Standard 2.0，为未来衍生预留)   │
│  ┌──────────────────────────────────────────────┐│
│  │  ViewModels + Models                         ││
│  ├──────────────────────────────────────────────┤│
│  │  Services (AppServices 聚合根)               ││
│  │  AdBlock · Privacy · ScriptRuntime           ││
│  │  ScriptMatcher · Storage · Wallpaper         ││
│  ├──────────────────────────────────────────────┤│
│  │  WebMessage 通信层（异步，非阻塞）            ││
│  ├──────────────────────────────────────────────┤│
│  │  Data Layer (SQLite)                         ││
│  └──────────────────────────────────────────────┘│
├──────────────────────────────────────────────────┤
│  Microsoft WebView2 SDK                          │
├──────────────────────────────────────────────────┤
│  系统 Edge WebView2 Runtime (Chromium)            │
└──────────────────────────────────────────────────┘
```

## 运行时结构

**便携版目标发布结构（Apage.Portable，签名后 <5MB）**：

> 普通 `build\build.ps1` 输出仍包含依赖 DLL；正式分发必须运行 `build\package.ps1`，它只复制经 Costura 合并、原生库内嵌的单个 EXE。以下 `data/` 为运行时生成，不属于分发文件。

```
Apage/
├── Apage.Portable.exe     # 目标：唯一必需分发文件（.NET FX 4.8 框架依赖）
├── data/
│   ├── settings.json      # 设置（原子写 JSON）
│   ├── session.json       # 会话保存/恢复
│   ├── privacy-stats.json # 周隐私统计
│   ├── adblock/           # 用户本地规则；内置 PSL/规则快照嵌入 EXE
│   └── webui/             # 新标签页本地资产（运行时抽取）
├── wallpapers/            # 用户壁纸
└── cache/                 # 仅纯便携模式；默认缓存位于宿主机 %TEMP%\Apage\cache
```

**桌面版（Apage.Desktop，~50MB，暂缓，PMF 验证后条件启动）**：

```
Apage/
├── Apage.Desktop.exe      # 单文件可执行（.NET 8 自包含）
├── data/                  # 同便携版
├── wallpapers/            # 同便携版
└── cache/                 # 同便携版
```

## 开发

```bash
# 环境要求
# - Windows 10/11（WebView2 Runtime 已预装）
# - .NET 8 SDK（构建 Apage.Core 共享层，版本经 global.json 固定）
# - Visual Studio 2022（MSBuild + .NET Framework 4.8 目标包）
```

**构建**：`Apage.sln` 是「旧式 v4.8 + SDK 风格」混合解决方案，`dotnet build Apage.sln` **不支持**（会失败），主项目必须用 VS 2022 的 MSBuild。仓库提供的封装脚本 [`build/build.ps1`](build/build.ps1) / [`build/build.sh`](build/build.sh) 会自动经 vswhere 定位 MSBuild、按需设置 `MSBuildSDKsPath` 并关闭 workload 解析器，**这是受支持的构建入口**：

```powershell
# PowerShell（推荐）：Restore + 构建整个解决方案（默认 Release）
build\build.ps1
build\build.ps1 -Configuration Debug
```

```bash
# Git Bash：一条命令验证（内部转调 build/build.ps1）
build/build.sh            # 默认 Release
build/build.sh Debug
```

**冒烟验证**：构建成功后可一条命令跑发布线冒烟检查（[`build/smoke.ps1`](build/smoke.ps1)）：以 `--smoke` 模式拉起 Apage.Portable，验证 **应用可启动 / 主窗口可呈现 / WebView2 内核可初始化 / SQLite 安全初始化 / PSL 与规则快照加载** 五个信号，并在首帧后真实构建设置与数据窗口、执行书签/历史数据往返、Ctrl+F/Toast 布局探针与 NTP getStats 来源门控探针；CI 在 portable 构建步骤之后自动运行：

```powershell
build\smoke.ps1                       # 默认 Release
build\smoke.ps1 -Configuration Debug
```

**正式单文件打包**：`build\package.ps1` 校验许可，构建并复制唯一必需 EXE 到 `artifacts\portable\`，硬性检查 4.25MB 开发目标/5MB 上限、隐私资产哈希、五信号 smoke 和启动基准，并输出 SHA-256、`artifacts\legal\` 与 release manifest：

```powershell
build\package.ps1
build\package.ps1 -SkipBuild        # 已构建时只重新打包/验收
```

**启动基准**：真实 `Apage.Portable` 的窗口可见、WebView2、SQLite 与隐私资产就绪可分离测量；基准在 `artifacts/` 的隔离副本中运行并启用便携缓存，不读取或清理真实 `data/`。对正式单文件运行时只复制 EXE：

```powershell
build\startup-bench.ps1 -ExePath artifacts\portable\Apage.Portable.exe `
  -Iterations 3 -OutputDirectory artifacts\release-startup -WarmMedianLimitMs 1000
```

脚本等价于以下手动步骤：

```powershell
# 方式 A：VS Installer 已勾选「.NET SDK」组件（推荐，一劳永逸）
msbuild Apage.Portable\Apage.Portable.csproj /t:Restore,Build /p:Configuration=Release

# 方式 B：VS 缺少「.NET SDK」组件时，临时指定 SDK 路径（须用 .NET 8 SDK，10.x 会触发 NETSDK1216）
$env:MSBuildSDKsPath = "C:\Program Files\dotnet\sdk\8.0.423\Sdks"
$env:MSBuildEnableWorkloadResolver = "false"
msbuild Apage.Portable\Apage.Portable.csproj /t:Restore,Build /p:Configuration=Release

# 单独构建 Apage.Core（SDK 风格，dotnet CLI 直接可用）
dotnet build Apage.Core/Apage.Core.csproj -c Release

# 发布便携版（D1 已完成；签名与最终复验属 S4）
# .NET FX 4.8 不支持 PublishSingleFile；生产使用 Costura + NativeBootstrap
# build\build.ps1 的 bin 目录仍是多文件调试布局；正式分发以 package.ps1 的单 EXE 为准
# 发布产物：artifacts\portable\Apage.Portable.exe（1 文件，当前 4,068,352 字节，版本 0.1.0.0）
```

> **发布说明**：v0.1~v1.0 单押便携版（Apage.Portable），基于 .NET Framework 4.8（Win10/11 系统自带）实现真即插即用（前提：目标机已装 WebView2 Runtime）。桌面版（Apage.Desktop，.NET 8 自包含）暂缓，仅便携版验证 PMF 后条件启动。Apage.Core（.NET Standard 2.0）保留分层以备未来衍生。详见 [docs/product-design.md](docs/product-design.md)。

## 文档

- [产品设计文档](docs/product-design.md) — 竞品分析、功能设计、架构细节
- [前端设计文档](docs/frontend-design.md) — 设计系统、UI 规范、交互与动效
- [开发路线图](docs/roadmap.md) — 阶段规划与历史状态
- [v0.1 发布收敛计划](docs/design/2026-10-28-v01-release-convergence.md) — 剩余任务、依赖顺序与质量门禁（当前执行依据）
- [S2 第一批实施记录](docs/design/2026-10-29-s2-first-slice.md) — D1/A1/B1/C1 实测证据与已知限制
- [A2 PSL 与离线规则快照](docs/design/2026-10-29-a2-psl-rule-snapshot.md) — 第三方判定、规则筛选、许可与体积证据
- [D2 发布工程记录](docs/design/2026-10-29-d2-release-engineering.md) — 可重复打包、启动门禁、许可包与 CI
- [B2/A3 实施记录](docs/design/2026-10-29-b2-data-a3-rule-update.md) — 数据垂直切片、书签导入、下载跟踪、手动规则更新
- [发布签名手册](docs/release/signing.md) — SignPath 申请、签名工作流与签名后复验
- [S0 基线与共享契约](docs/design/2026-10-28-s0-baseline-contracts.md) — 发布口径和数据/隐私契约
- [S1 单文件与启动验证](docs/spikes/results/single-file.md) — spike 证据、体积预算与复现命令
- [脚本管理器设计](docs/script-manager.md) — 用户脚本运行时与管理器（v0.1 后）

## 致谢

架构设计参考了 [ZZZ 浏览器](https://github.com/zengjiangy/ZZZ) 的轻量方案。

## 许可证

Apage 源代码采用 MIT（见 [LICENSE](LICENSE)）。

随包隐私数据保持各自许可：Public Suffix List 为 **MPL-2.0**；EasyList/EasyPrivacy 过滤快照按 **CC BY-SA 3.0** 分发。完整归属与许可全文见 [THIRD-PARTY-NOTICES.txt](licenses/THIRD-PARTY-NOTICES.txt)、[MIT](licenses/LICENSE-MIT.txt)、[MPL-2.0](licenses/LICENSE-MPL-2.0.txt) 与 [CC BY-SA 3.0](licenses/LICENSE-CC-BY-SA-3.0.txt)。`build\package.ps1` 还会生成独立的 `artifacts\legal\` 发布包。
