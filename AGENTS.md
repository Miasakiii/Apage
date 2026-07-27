# AGENTS.md — Apage 智能体作业指南

> 面向编码智能体的入口。先读本文件，再动手。人类贡献者也适用。
> 一句话：超轻量、隐私优先、全本地化的 Windows 便携浏览器（.NET FX 4.8 + WPF + WebView2）。

## 快速事实

- **主交付物**：`Apage.Portable`（.NET Framework **4.8**，旧式 csproj，`WinExe`，单文件 <5MB）。唯一发布线。
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

- 只测 `Apage.Core` 的纯逻辑（决策 #18：Core 层纯逻辑优先单测）。
- 现有可测目标：`AppPaths`（缓存目录 R7 策略）、`AppSettings`（隐私默认全开基线）、`SettingsService`（原子写 / 损坏 JSON 回落默认）、`SessionService`（会话原子写 / 损坏回落空 / URL 可恢复过滤 + 索引收敛）。
- 计划中的 `ScriptMatcher` / `AdBlockRuleEngine` / `ScriptMetadataParser` **尚未实现**；实现后补对应单测。

## 环境要求

- Windows 10/11（WebView2 Runtime 系统自带；Win11 默认预装）。
- .NET 8 SDK（构建 Core / 跑测试，版本经 `global.json` 固定）。
- Visual Studio 2022（含 MSBuild + .NET Framework 4.8 目标包）——构建主项目必需。

## 高风险区域 / 约定（改动前务必知晓）

1. **不要把 `Apage.Portable.csproj` 的通配符 `Compile`/`Page` 展开成逐文件条目**。通配符是故意的，让并行智能体加 `.cs`/`.xaml` 无需改 csproj（见该文件注释）。新增源码/资源直接放目录即可被拾取；`App.xaml` 是 `ApplicationDefinition`，已从 `Page` glob 排除。
2. **隐私默认全开是产品红线**（`AppSettings` 默认值）：`BlockThirdPartyCookies` / `SendDoNotTrack` / `SendGPC` / `WebRTCIPProtection` 默认 `true`，`OnlineSearchSuggestions` / `ClearOnExit` 默认 `false`。改默认值等于改产品定位，需先对齐 `docs/product-design.md`。
3. **便携运行时目录**（`data/` `cache/` `wallpapers/`）永不提交（已在 `.gitignore`）。缓存位置遵循风险 R7：默认写宿主机 `%TEMP%\Apage\cache`，纯便携模式才写 exe 同级 `cache/`。
4. **零遥测 / 零后台请求**：不要引入任何遥测、崩溃上报、云同步或“检查更新”联网逻辑。
5. **`Apage.Core` 保持 netstandard2.0**，不要引入依赖 Windows 或 .NET 8 专有 API 的包（否则破坏未来衍生分层）。

## 文档地图（权威来源）

| 主题 | 文档 |
|------|------|
| 产品设计（竞品、功能、架构细节、隐私基线 §5.2） | [docs/product-design.md](docs/product-design.md) |
| 前端设计（设计系统、UI 规范、交互动效） | [docs/frontend-design.md](docs/frontend-design.md) |
| 路线图（阶段规划、任务清单、风险 R1–R13、决策 #1–#18） | [docs/roadmap.md](docs/roadmap.md) |
| 脚本管理器（用户脚本运行时与管理器） | [docs/script-manager.md](docs/script-manager.md) |

## 交付前自检

- [ ] `dotnet test Apage.Core.Tests` 通过。
- [ ] `build\build.ps1` 主项目构建成功（需 VS 2022 环境）。
- [ ] 未改动通配符 csproj、未引入遥测、未改隐私默认值（除非明确要求）。
