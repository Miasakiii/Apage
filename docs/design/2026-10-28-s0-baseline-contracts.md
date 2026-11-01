# S0：v0.1 基线、发布口径与共享契约

> 日期：2026-10-28 ｜ 状态：已冻结 ｜ 上游：[v0.1 发布收敛计划](2026-10-28-v01-release-convergence.md)

## 一、基线结论

当前基线为 commit `88e08a9` 加工作区既有未提交修改；本轮不清理、不回退、不提交这些修改。后续发布候选必须落在明确提交上。

| 检查 | 命令 | 实测结果 |
|---|---|---|
| Core 单测 | `dotnet test Apage.Core.Tests/Apage.Core.Tests.csproj -c Release` | 131 通过 / 0 失败 / 0 跳过 |
| 红线 | `build\check-redlines.ps1` | 五条全部通过 |
| 构建 | `build\build.ps1 -Configuration Release` | 全解决方案成功 |
| 冒烟 | `build\smoke.ps1 -Configuration Release` | S0 时三信号通过（当前已扩展为五信号，见 [A2 记录](2026-10-29-a2-psl-rule-snapshot.md)） |

## 二、参考环境

- Windows 11 家庭中文版，build 26200。
- Intel Core i9-14900HX，63.71 GiB RAM。
- WebView2 Runtime 156.0.4314.50。
- .NET SDK 8.0.427；VS 2022 MSBuild。

性能数字只对此参考环境有效；其他硬件需单独测量，不做普遍承诺。

## 三、发布口径

1. 首发只提供 **x64 单文件 EXE**；x86/ARM64 后续独立出包。
2. 分发目录只能有一个必需 EXE；运行时 `data/cache/wallpapers` 不属于分发文件。
3. 最终签名后 EXE `< 5,000,000` 字节；开发期目标 `≤ 4,250,000` 字节。
4. 冷启动指进程启动至主窗口 `ContentRendered`，目标 `<1s`；首启与重复启动分别记录。
5. WebView2 就绪、规则就绪、SQLite 就绪分别测量，不能混入“窗口可见”口径。
6. 签名后必须对同一产物重跑 smoke、体积和启动测试。

## 四、共享契约

### 4.1 PSL 与第三方判断

- 输入为页面 URL 与请求 URL；先归一化 host，再按 PSL 求 eTLD+1。
- 支持普通、通配和例外规则，以及 IDN、大小写、尾点。
- IP、localhost、空值、非法 URL、无法可靠判断时返回“非第三方”，即**宁可漏剥，不可误剥**。
- PSL 只使用随包本地快照；v0.1 不在线更新。
- ThirdPartyDetector 通过抽象查询接口消费 PSL，UI 不直接读取 PSL 文件。

### 4.2 规则快照与更新

- 快照元数据至少包含：来源、上游版本/日期、SHA-256、接受条数、跳过条数及原因、许可。
- 带未支持 `$` 修饰符的规则必须跳过或实现语义，禁止去掉修饰符后扩大匹配。
- 规则引擎在后台构建，完成后原子替换；所有现有/新建/休眠恢复标签共享同一规则提供方。
- 用户规则独立保存，更新不得覆盖。
- 更新只由按钮触发；无启动检查、定时检查、静默重试。
- 更新必须 HTTPS、限时、限大小/条数、暂存校验、失败回滚；联网红线仅对专用传输文件精确豁免。

### 4.3 SQLite 与数据模型

- SQLite 只承载新增的书签、历史、下载；设置、会话、周统计继续 JSON。
- 使用 `journal_mode=DELETE`、`synchronous=FULL`、`foreign_keys=ON`、`user_version` 事务迁移。
- 初始化与完整性检查不得阻塞主窗口；迁移前备份，损坏不得静默删库。
- 书签以 URL 规范化结果去重；导入异常条目不得中断整批。
- 历史只记录成功的顶层 HTTP/HTTPS 导航，排除 NTP、内部页和失败导航。
- 下载状态限定为进行中/完成/失败/取消；重启后遗留“进行中”必须转为失败或中断，不得伪显示仍可继续。

### 4.4 设置生效语义

| 设置 | 生效方式 |
|---|---|
| 搜索引擎 | 即时；新导航使用新值 |
| DNT / GPC / 第三方 Cookie | 即时；请求管线每次读取当前设置 |
| 规则手动更新 | 成功后对全部标签原子生效 |
| WebRTC IP 防护 | 重启后生效；Environment 创建参数不可原地变更 |
| PortableCache | 重启后生效；Environment 用户目录不可原地变更 |
| Theme / ClearOnExit / OnlineSearchSuggestions | 无消费方前不在设置 UI 暴露 |

### 4.5 启动与隐私顺序

- 最小设置读取后尽快显示窗口；周统计、会话、规则、PSL、SQLite 完整性检查在首帧后初始化。
- 首个标签创建在 Show 之后进行；NTP 呈现前周统计必须已加载。
- 本地 NTP 可先呈现；首个外部网页导航必须等待隐私管线就绪。
- 不得以冷启动优化为由让网页绕过广告拦截、Cookie 控制或 DNT/GPC。

## 五、体积基线

S0 时 Release 便携目录：**30 文件，3,133,416 字节（约 2.99 MiB）**。其中根目录 19 文件、2,656,816 字节，另含三架构 WebView2Loader。该表保留为历史基线，S1 后数值见第七节。

| 项目 | 字节 | 说明 |
|---|---:|---|
| System.Text.Json.dll | 778,024 | 当前最大托管依赖 |
| Microsoft.Web.WebView2.Core.dll | 698,208 | 必需 |
| CommunityToolkit.Mvvm.dll | 147,784 | S0 时存在；S1 已确认零引用并移除 |
| Apage.Portable.exe | 121,856 | 当前主程序 |
| Apage.Core.dll | 48,128 | 共享层 |
| WebView2Loader x64 | 164,192 | 首发保留 |
| WebView2Loader x86 + ARM64 | 276,672 | x64 首发可移除 |
| 两个 PDB | 167,456 | 发布包排除、符号另存 |

x64 首发且去除 x86/ARM64、PDB 后，S0 估算未合并基线约 **2,689,128** 字节。S1 独立单文件 spike 实测 **2,950,144 字节**，距 5MB 硬门禁余 2,049,856 字节；该值不是生产 `Apage.Portable` 成品体积，PSL、规则快照和许可文本仍须持续记账。

## 六、S0 完成判定

- [x] 全绿基线复验。
- [x] 发布指标与参考环境固定。
- [x] PSL、规则、SQLite、设置和启动契约固定。
- [x] 当前体积与预算基线记录。
- [x] 文档现状同步。

## 七、S1 后基线变更（2026-10-29）

S1 已修正两处影响发布口径的事实：

1. `Apage.Portable` 实际以 x86 运行；现已显式 `PlatformTarget=x64` / `Prefer32Bit=false`。
2. `CommunityToolkit.Mvvm` 全仓零引用；已移除，Core 包边界只保留 `System.Text.Json`。

当前 x64 Release 输出（不含 PDB、x86/ARM64 原生目录）为 **17 文件 / 2,628,360 字节**，不能与 S0 初始 30 文件 / 3,133,416 字节直接相减。单文件 spike 现为 **2,950,144 字节**；详见 [S1 结果](../spikes/results/single-file.md)。
