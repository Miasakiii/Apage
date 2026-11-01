# Spike 3：WebView2 扩展能力摸底（风险 R3）

- 证据日期：**2026-10-26（补跑两次：默认选项 FAIL → 开启 `AreBrowserExtensionsEnabled` 后全矩阵跑通）**；源码完成：2026-07-19；本文整理：2026-10-23，补跑后更新：2026-10-26
- 环境：.NET 运行时 8.0.32（`net8.0-windows`，`OutputType=Exe` 的 WPF 混合项目）；`Microsoft.Web.WebView2` SDK **1.0.4078.44**；本机 WebView2 Runtime **155.0.4283.62**；OS Windows NT 10.0.26200
- 代码：`spikes/ExtensionSpike/Program.cs`（独立项目，未加入 `Apage.sln`）
- 复现：`dotnet run --project spikes/ExtensionSpike -- <日志文件路径>`（看门狗 150s 未完成即强退 exit 2）
- 实测状态：**已确证（两次运行，日志原文在列）**；`*.log` 不入库，本文嵌入原文
- 退出码语义：exit 0 = 流程跑完（单步 FAIL 属摸底数据，不上抛），**不代表全绿**——判定须读日志

## 摸底什么（R3 / product-design.md 痛点 5）

> 实测 `AddBrowserExtension` 可装/不可装的 API 范围，确定对外口径（对应 R3）。

即：WebView2 到底能装哪些扩展、哪些装不上，据此收敛对外宣传口径，不做全量 Chrome 扩展承诺。

## 源码设计的测试矩阵（RunStepsAsync）

| 步骤 | 动作 | 目的 |
|---|---|---|
| 1 | `CoreWebView2Environment.GetAvailableBrowserVersionString(null)` 取本机 Runtime 版本 | 记录运行环境（无 Runtime 则抛错中止） |
| 2 | 反射确认 SDK 扩展 API 存在性（见下表） | 确认编译期引用 1.0.4078.44 的 API 面 |
| 3 | 程序内生成「最小 MV3」与「最小 MV2」扩展到临时目录 | 备好正向/负面样本 |
| 4 | `CreateAsync` + `EnsureCoreWebView2Async` 初始化 WebView2 | 就绪被测对象 |
| 5 | `profile.AddBrowserExtensionAsync(mv3Dir)` 加载最小 MV3 | **正向：MV3 可否安装** |
| 6 | `profile.GetBrowserExtensionsAsync()` 枚举已加载扩展 | 确认安装生效 |
| 7 | `ext.EnableAsync(false)` / `EnableAsync(true)` 切换启用 | 启用/禁用是否生效 |
| 8 | `AddBrowserExtensionAsync(mv2Dir)` 加载最小 MV2（负面测试） | **负面：MV2 是否被拒** |
| 9 | `ext.RemoveAsync()` 卸载并复查列表 | 卸载是否生效 |

生成的样本极简：MV3 为 `manifest_version:3` + `background.service_worker`；MV2 为 `manifest_version:2` + `background.scripts`（Program.cs 尾部两个 Write 方法）。

## API 面（SDK 1.0.4078.44，编译期反射 + 运行日志双重确认）

| 类型 | 成员 | 实测 |
|---|---|---|
| `CoreWebView2Environment` | `GetAvailableBrowserVersionString(string)`（静态） | 存在 |
| `CoreWebView2EnvironmentOptions` | `AreBrowserExtensionsEnabled` | 存在，**默认 `false`**（SDK XML 文档明示：关闭时不能安装新扩展、已装扩展不可用；与已运行环境值不一致报 `ERROR_INVALID_STATE`） |
| `CoreWebView2Profile` | `AddBrowserExtensionAsync(string)` / `GetBrowserExtensionsAsync()` | 存在 |
| `CoreWebView2Profile` | `BrowserExtensions`（同步属性） | **不存在**（运行日志「缺失」；2026-10-23 版本文曾列为确认存在，更正） |
| `CoreWebView2BrowserExtension` | `Id` / `Name` / `IsEnabled` / `EnableAsync(bool)` / `RemoveAsync()` | 存在 |

另有一条命名实证（2026-07-19 反射确认）：版本查询 API 名为 `GetAvailableBrowserVersionString`，**不存在** `GetAvailableCoreWebView2BrowserVersionString` 变体。

## 关键发现一：扩展支持默认关闭（第一次运行，2026-10-26 02:00:58）

`CreateAsync(null, udfDir)` 未传 options（即默认 `AreBrowserExtensionsEnabled=false`）时，`AddBrowserExtensionAsync` 对 **MV3 与 MV2 一律**直接抛：

```
[02:00:58.630] FAIL: System.Runtime.InteropServices.COMException HResult=0x80070032: 不支持该请求。 (0x80070032)
```

（0x80070032 = `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`。）注意：此时步骤 8 的 MV2「如预期失败」**不是**「MV2 被拒」的证据——是扩展通道整体关闭，与 manifest 版本无关。

**修法**：创建 Environment 时一次性指定（事后不可开）：

```csharp
var env = await CoreWebView2Environment.CreateAsync(null, udfDir,
    new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = true });
```

## 运行记录二（2026-10-26 02:03:15，开启选项后全矩阵跑通，据日志原文）

```
[02:03:15.531] === Apage Spike 3: WebView2 扩展能力摸底（R3） ===
[02:03:15.538] 时间(本地): 2026-10-26 02:03:15
[02:03:15.538] OS: Microsoft Windows NT 10.0.26200.0
[02:03:15.538] .NET 运行时: 8.0.32
[02:03:15.538] WebView2 SDK 程序集: Microsoft.Web.WebView2.Core 1.0.4078.44
[02:03:15.538] --- [1] CoreWebView2Environment.GetAvailableBrowserVersionString ---
[02:03:15.542] OK: Runtime 版本 = 155.0.4283.62
[02:03:15.542] --- [2] SDK 扩展 API 可用性（反射，编译期引用 1.0.4078.44） ---
[02:03:15.542] 存在: CoreWebView2Profile.AddBrowserExtensionAsync(string) -> ...
[02:03:15.542] 存在: CoreWebView2Profile.GetBrowserExtensionsAsync() -> ...
[02:03:15.542] 缺失: CoreWebView2Profile.BrowserExtensions (同步属性)
[02:03:15.542] 存在: CoreWebView2BrowserExtension.Id / .Name / .IsEnabled / .EnableAsync(bool) / .RemoveAsync()
[02:03:15.546] --- [3] 生成最小扩展到临时目录 ---
[02:03:15.546] --- [4] 初始化 WebView2（AreBrowserExtensionsEnabled=true） ---
[02:03:15.990] OK: CoreWebView2 初始化完成，CookieManager 可用=True
[02:03:15.990] 默认 Profile: Name=, IsInPrivateModeEnabled=False
[02:03:15.990] --- [5] AddBrowserExtensionAsync 加载最小 MV3 扩展 ---
[02:03:16.002] OK: 加载成功 -> Id=killliajfeijdpbjpkdnfnjimcbppkif, Name=Apage Spike MV3 Probe, IsEnabled=True
[02:03:16.002] --- [6] GetBrowserExtensionsAsync 枚举 ---
[02:03:16.005] 枚举到 3 个扩展:
[02:03:16.005]   - Id=dgiklkfkllikcanfonkcabmbdfmgleag, Name=Microsoft Clipboard Extension, IsEnabled=True
[02:03:16.005]   - Id=killliajfeijdpbjpkdnfnjimcbppkif, Name=Apage Spike MV3 Probe, IsEnabled=True
[02:03:16.006]   - Id=mhjfbmdgcfjbbpaeojofohoefgiehjai, Name=Microsoft Edge PDF Viewer, IsEnabled=True
[02:03:16.013] --- [7] EnableAsync 禁用/启用 ---
[02:03:16.013] EnableAsync(false) 后: IsEnabled=False
[02:03:16.014] EnableAsync(true) 后: IsEnabled=True
[02:03:16.014] --- [8] MV2 扩展负面测试（预期被拒绝） ---
[02:03:16.037] 意外成功: Id=aieehgghomgidamoiaanldennldeogjc, Name=Apage Spike MV2 Probe（MV2 也被接受，口径需修正）
[02:03:16.041] --- [9] RemoveAsync 卸载并确认 ---
[02:03:16.047] RemoveAsync 后: 仍在列表=False（当前共 2 个扩展）
[02:03:16.101] === Spike 3 执行完毕 ===
```

（日志中两条反射「存在」行与 API 面表格有行合并省略；原始日志留存于 `spikes/ExtensionSpike/spike-run.log`。临时目录清理有一处文件占用失败，不影响结论。）

**逐项结论**：

- **MV3 可装**：`AddBrowserExtensionAsync` 返回有效 `CoreWebView2BrowserExtension`（Id/Name/IsEnabled=True）；
- **可枚举**：`GetBrowserExtensionsAsync` 列出 3 个（探针 + 两个出厂内置：Microsoft Edge PDF Viewer、Microsoft Clipboard Extension）；
- **可禁用/启用**：`EnableAsync(false/true)` 后 `IsEnabled` 如实切换；
- **MV2 也被接受**：最小 MV2 侧载成功（步骤 8「意外成功」）——**「仅 MV3」口径不成立**，触发 Program.cs 预案注释「MV2 也被接受，口径需修正」；
- **可卸载**：`RemoveAsync` 后探针从列表消失（剩 2 个内置）。

## 对外口径（按实测修正）

2026-10-23 版口径「部分 MV3 扩展（侧载）」中「仅 MV3」的依据（WebView2 会拒 MV2）被实测推翻。修正后的口径：

- **能装**：本地目录侧载（API 入参即本地路径），MV3 / MV2 最小样本均实测可装；
- **前置条件**：必须在 `CoreWebView2Environment` **创建时**显式 `AreBrowserExtensionsEnabled=true`（默认关闭，表现为 0x80070032）；
- **仍然受限**：API 面小（装/枚举/启停/卸载，无每扩展权限细粒度管理）、**无商店直连**（无 Edge/Chrome 商店安装与自动更新通道）、能力随目标机 WebView2 Runtime 版本浮动；
- **不变的产品结论**：用户脚本（GM_*）仍是一级差异化；扩展仍非便携版 v0.1 范围，归桌面版条件启动评估（roadmap Phase 8）。

## 已确证 / 未确证

**已确证（2026-10-26 两次运行日志可指认）**：

- `AreBrowserExtensionsEnabled` 默认 false，不开则 `AddBrowserExtensionAsync` 报 0x80070032（运行一）；
- 最小 MV3 实际加载成功，枚举/启停/卸载全链路生效（运行二）；
- 最小 MV2 亦被接受（运行二）；
- Runtime 155.0.4283.62 + SDK 1.0.4078.44 组合的扩展 API 面与命名（含 `BrowserExtensions` 同步属性缺失、`GetAvailableBrowserVersionString` 命名）；
- 出厂内置两个扩展（Edge PDF Viewer、Clipboard Extension）默认在列。

**仍未确证（本 spike 未覆盖，口径引用时须克制）**：

- 真实世界 MV3 扩展（有 content scripts / host permissions / 多权限组合）的兼容性与崩溃面；
- MV2 完整特性面（仅验证了最小样本可装）；
- 不同 WebView2 Runtime 版本下的行为差异（仅测了 155.0.4283.62 一台机器）；
- 扩展与 Apage 隐私管道（WebResourceRequested 拦截等）的相互作用。

## 落地建议（2026-10-26 更新）

1. ~~在装有 WebView2 Runtime 的机器上补跑一次~~ **已完成**：两次运行日志在列，Phase 0 Spike 3 已具备勾选依据；复跑命令不变（注意 exit 0 只代表流程跑完，判定看日志）。
2. 口径核对结果：**MV3 加载 OK 成立，但「MV2 被拒」不成立**——已按 Program.cs 步骤 8 的预案修正对外口径（见上节；roadmap 决策 #5、product-design 痛点 5 / R3 同步更新）。
3. 在便携版/桌面版落地扩展时的工程要点：`AreBrowserExtensionsEnabled` 只能随 Environment 创建指定（Apage 的 `BrowserLifecycleService` 建环境处须同步开关）；`AddBrowserExtensionAsync` 属 `CoreWebView2Profile` 能力，可用性随目标机 Runtime 版本浮动（R3）；便携版 v0.1 范围不含扩展，本 spike 结论主要服务于桌面版条件启动的评估。
