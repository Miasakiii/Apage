# Apage

> 一个页面，干净打开。

超轻量、隐私优先、全本地化的 Windows 便携浏览器。

## 特性

- **极致轻量** — 单文件 EXE，<5MB，冷启动 <1秒
- **隐私默认全开** — 广告拦截、追踪阻断、Cookie 控制，开箱即用
- **全本地化** — 零遥测、零后台请求、零数据收集
- **无需登录** — 不强制账号，不要求云同步
- **Portable** — 数据跟程序走，U盘即插即用（前提：目标机已装 WebView2 Runtime，Win11 默认预装）
- **无广告** — 浏览器本身无任何广告或推广
- **壁纸新标签页** — 支持自定义静态壁纸，拖拽即设置

## 技术栈

单押便携版（Apage.Portable）为唯一发布线；Apage.Core 保留分层以备未来衍生。桌面版（Apage.Desktop）暂缓，PMF 验证后条件启动。

| 层级 | 便携版（Apage.Portable，主力） |
|------|------------------------------|
| 运行时 | .NET Framework 4.8（Win10/11 系统自带） |
| 体积 | <5MB 单文件（Phase 0 实测预算） |
| 定位 | U盘即插即用（前提：已装 WebView2 Runtime） |
| 共享层 | Apage.Core（.NET Standard 2.0，为未来桌面版衍生预留） |
| UI | WPF + CommunityToolkit.Mvvm |
| 引擎 | WebView2（系统自带 Chromium） |
| 通信 | WebMessage（异步，非阻塞） |
| 存储 | SQLite |

> 桌面版（暂缓，未来可选）：.NET 8 自包含 ~50MB，完整体验。仅在便携版验证 PMF 后再评估，见 [roadmap Phase 8](docs/roadmap.md)。

## 架构

单押便携版：Apage.Portable 为唯一发布线，Apage.Core 保留分层以备未来衍生。

```
┌──────────────────────┐  ┌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌┐
│  Apage.Portable      │  ┆  Apage.Desktop       ┆
│  .NET FX 4.8 + WPF   │  ┆  .NET 8 + WPF        ┆
│  单文件 <5MB（主力） │  ┆  ~50MB（暂缓/未来）  ┆
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

**便携版（Apage.Portable，<5MB）**：

```
Apage/
├── Apage.Portable.exe     # 单文件可执行（.NET FX 4.8 框架依赖）
├── data/
│   ├── apage.db           # 书签 + 历史 + 设置
│   ├── ad-rules/          # 广告规则缓存
│   ├── favicons/          # 图标缓存
│   └── scripts/           # 用户脚本（.user.js + 存储）
├── wallpapers/            # 用户壁纸
└── cache/                 # WebView2 缓存（可清理）
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

# 发布便携版（Phase 6）
# .NET FX 4.8 不支持 PublishSingleFile，单文件合并方案（Costura.Fody 等）待 Phase 6 评估
# 当前构建产物：bin\Release\Apage.Portable.exe + WebView2 加载器（框架依赖，目标机无需安装 .NET）
```

> **发布说明**：v0.1~v1.0 单押便携版（Apage.Portable），基于 .NET Framework 4.8（Win10/11 系统自带）实现真即插即用（前提：目标机已装 WebView2 Runtime）。桌面版（Apage.Desktop，.NET 8 自包含）暂缓，仅便携版验证 PMF 后条件启动。Apage.Core（.NET Standard 2.0）保留分层以备未来衍生。详见 [docs/product-design.md](docs/product-design.md)。

## 文档

- [产品设计文档](docs/product-design.md) — 竞品分析、功能设计、架构细节
- [前端设计文档](docs/frontend-design.md) — 设计系统、UI 规范、交互与动效
- [开发路线图](docs/roadmap.md) — 阶段规划与任务清单
- [脚本管理器设计](docs/script-manager.md) — 用户脚本运行时与管理器

## 致谢

架构设计参考了 [ZZZ 浏览器](https://github.com/zengjiangy/ZZZ) 的轻量方案。

## 许可证

MIT
