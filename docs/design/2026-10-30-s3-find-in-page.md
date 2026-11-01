# S3 余项：页内查找（Ctrl+F）与门禁复测

- 日期：2026-10-30
- 范围：v0.1 发布收敛 S3 余项。新增 Ctrl+F 页内查找（纯逻辑 + Portable UI 接线），并完整复跑五项门禁。
- 前置：仓库存在大量既有未提交修改（本次开工时 80 项），全部保留；未执行 commit / reset / clean。
- 关联：[2026-10-28-v01-release-convergence.md](2026-10-28-v01-release-convergence.md)、[2026-10-29-b2-data-a3-rule-update.md](2026-10-29-b2-data-a3-rule-update.md)

## 一、门禁结果（本次实测）

宿主机：Windows，构建/打包期间 `Win32_Processor.LoadPercentage` 读数 29% → 47% → 41%（三次采样，均非独占环境）。

| 门禁 | 命令 | 结果 | 关键数字 |
|------|------|------|----------|
| 单元测试 | `dotnet test Apage.Core.Tests -c Release` | 通过 | 287 通过 / 0 失败（基线 260，S3 首切片 +7，本轮 +20） |
| 红线 | `build\check-redlines.ps1` | 通过 | 5 条约定全部成立（通配符 / 零遥测 / Core netstandard2.0 + 包边界 / 运行时目录不入库 / 隐私默认断言） |
| 许可 | `build\check-licenses.ps1` | 通过 | MIT + MPL-2.0 PSL + CC BY-SA 3.0（2 源 94,646 条） |
| 构建 | `build\build.ps1 -Configuration Release` | 通过 | Core / Tests / Portable 三目标成功 |
| 打包 | `build\package.ps1 -SkipBuild -Configuration Release` | 通过 | 1 文件 4,058,112 字节；SHA-256 `6286E1BACC2E4EDC73BAC9C41BD61794A4780475885547C9D84459AE2484E87A`；签名 NotSigned |

启动门禁读数（单文件产物，同一产物三次运行）：

| 轮次 | 类型 | ContentRendered | 门禁 |
|------|------|-----------------|------|
| 本次终态 run1 | first | 925.3 ms | — |
| 本次终态 run2–3 | warm | 842.1 / 864.5 ms | 中位 853.3 ms，通过（限 1000 ms） |
| 同日早间（改动前） | warm | 935.8 ms（first 984.9） | 通过 |
| 同日门禁前的另一次复测 | warm | 792.3 ms（first 839.5） | 通过 |

口径说明：本轮未出现「1.4–1.9s 失败」的历史现象；三次采样负载（29% / 47% / 41%）与中位读数无单调关系（47% 负载反而最快），说明该指标目前由噪声主导，单次读数不可作结论。**未改动 `build/package.ps1` 的任何阈值，也未添加跳过门禁的开关。**

体积账：4,058,112 字节；相对 S2 收尾（4,035,584）增 22,528 字节（+0.56%）。4.25MB 开发门槛余 398,336 字节（8.9%），5MB 发布门槛余 1,184,768 字节（22.6%）。本轮无新增资源文件，增量全部是代码。

## 二、S3 余项：Ctrl+F 页内查找

### 2.1 先核对 SDK，再写代码

WebView2 包为 `Microsoft.Web.WebView2 1.0.4078.44`。App 内置 WebView2 Runtime 版本 `156.0.4314.50`。

为避免凭记忆写 API，先用 `ildasm` 反编译 `Microsoft.Web.WebView2.Core.dll` 与 `.Wpf.dll` 确认签名，结论：

| 成员 | 实际签名 |
|------|----------|
| `CoreWebView2.Find` | 只读属性，返回 `CoreWebView2Find` 会话对象 |
| `CoreWebView2Find.StartAsync` | `Task StartAsync(CoreWebView2FindOptions)`（异步，`AsyncTaskMethodBuilder`） |
| `CoreWebView2Find.FindNext` / `FindPrevious` / `Stop` | `void`（**非** Task，也没有 `*Async` 变体） |
| 结果事件 | `MatchCountChanged` / `ActiveMatchIndexChanged`，类型为 `EventHandler<object>`（无专用 EventArgs） |
| 选项对象 | 必须由 `CoreWebView2Environment.CreateFindOptions()` 创建，无公开构造函数 |
| 计数属性 | `int MatchCount`、`int ActiveMatchIndex`（1 起，无命中为 -1） |

### 2.2 分层

- **`Apage.Core/Services/FindBarState.cs`（新增，netstandard2.0）**：查找条状态机——开关、查询、命中计数收敛、状态文案、按键语义（`FindKey` 最小键集，不引用 WPF）。
  - 文案规则：无查询 → 空；查询已变但命中未回报 → 空（避免刚敲字就闪「无结果」）；0 命中 → `无结果`；有命中 → `当前 / 总数`，内核未给活动索引时只显示 `N 项`。
  - `ApplyResult` 把序号夹取到 `[1, 总数]`，并在查找条已关闭时丢弃迟到的异步回报。
- **`Apage.Portable/Views/BrowserTabView.xaml.cs`**：查找能力门面 `CanFind` / `StartFindAsync` / `FindNextMatch` / `FindPreviousMatch` / `StopFind` / `FindMatchCount`，事件 `FindRequested` / `FindCloseRequested` / `FindResultsChanged`。
  - `HookFind` 整体 try/catch：Runtime 过旧导致 `core.Find` 不可用时 `CanFind=false`，UI 提示「当前页面暂不支持页内查找」而不是静默失败。
  - 会话选项固定 `ShouldHighlightAllMatches=true`、`SuppressDefaultFindDialog=true`（自绘 UI，禁止内核再弹自带查找框）。
  - 键盘上浮：WPF 封装把 `CoreWebView2Controller` 作为 **internal** 属性持有（ildasm 确认 `assembly hidebysig`），未公开 `AcceleratorKeyPressed`；沿用本文件既有反射做法（`DefaultBackgroundColor` 同样走反射）取控件后挂加速键，取不到则静默降级，Ctrl+F 仍可由窗口级 `PreviewKeyDown` 兜底。
  - Esc 仅在**本标签查找会话处于激活态**时被吞掉并上浮关闭请求，避免抢走网页自身对 Esc 的处理。
- **`Apage.Portable/MainWindow.xaml(.cs)`**：查找条 UI + 接线（输入即查找去抖 120ms、Enter 下一个、Shift+Enter 上一个、Esc 关闭、显示「无结果」）。事件只挂在**当前标签**的视图上，切标签时重挂并收起查找条，防止旧标签计数写到新标签。

### 2.3 关键设计决策：查找条用 Popup，不用 Grid 覆盖层

`BrowserTabView.xaml` 用的是 `wv2:WebView2`（HwndHost 子窗口）。WPF 覆盖元素会被该子窗口盖住（airspace），如果按常规在 `Grid.Row=2` 里放一个 `Border`，编译能过、冒烟能过，但真机上**根本看不见**。因此查找条做成 `Popup`（独立窗口、`StaysOpen=True`、相对 `TabHost` 定位），并随内容区尺寸变化重算偏移。

> 附带发现（未修，交由后续排期）：现有 `Toast` 提示条同样是 `Grid.Row=2` 内的 `Border`，在网页内容之上时应被 WebView2 遮盖，即「已添加到书签」这类反馈可能看不见。属既有缺陷，与本轮查找功能无关，本轮不扩大改动面。

### 2.4 冒烟新增两条探针

`--smoke` 路径新增（不改变既有五信号契约与退出码语义）：

1. **查找探针（非门禁）**：真实内核上跑一次 `CreateFindOptions → StartAsync("本周已拦截") → 等 MatchCountChanged → Stop`，把 `available / matches` 写入本地日志。Runtime 过旧时功能本就降级，故不判失败。
2. **查找条布局探针（门禁）**：`MainWindow.ValidateFindBarLayout` 真实打开 Popup，断言尺寸非零、不溢出内容区、关闭后状态干净；失败即冒烟失败（布局退化属代码回归，不是环境差异）。

实测日志：`find-probe available=True; matches=1`、`find-layout size=356x40; offsetX=650; hostWidth=1024`。

## 三、改动文件清单

新增：

| 文件 | 说明 |
|------|------|
| `Apage.Core/Services/FindBarState.cs` | 查找条状态机（纯逻辑，可单测） |
| `Apage.Core.Tests/FindBarStateTests.cs` | 20 个单测（生命周期 / 查询 / 计数收敛 / 文案 / 按键语义） |
| `docs/design/2026-10-30-s3-find-in-page.md` | 本文 |

修改：

| 文件 | 变更 |
|------|------|
| `Apage.Portable/Views/BrowserTabView.xaml.cs` | 查找门面 + 会话挂接 + 加速键反射 + `FindResultsChangedEventArgs`；`using System.Reflection` / `System.Windows.Input` |
| `Apage.Portable/MainWindow.xaml` | 新增查找条 `Popup`（图标 / 输入框 / 状态 / 上一个 / 下一个 / 关闭） |
| `Apage.Portable/MainWindow.xaml.cs` | Ctrl+F 接线、去抖查找、键位分发、切标签重挂、`ValidateFindBarLayout`、`LogSmokeNote` |
| `Apage.Portable/SmokeCheck.cs` | 查找探针 + 布局探针；冒烟收尾条件加 `findProbeDone` |
| `AGENTS.md` | 文档地图补本文件条目 |

未触碰：`build/package.ps1` 阈值、`Apage.Portable.csproj` 通配符、`AppSettings` 隐私默认值、网络豁免名单。

## 四、已知限制 / 未验证项

1. **未做真机点击走查**：本环境禁用运行时编译（`Add-Type` 被安全策略拦截），无法注入按键或截图，因此 Ctrl+F 的实际按键路径、查找条在网页上方的可见性、Esc 关闭后的焦点回归，均只有代码与布局探针证据，尚未真机点击复验。
2. **加速键反射的版本脆弱性**：`CoreWebView2Controller` 是 internal 成员，升级 WebView2 包后需重新确认（失效时 Ctrl+F 仅在应用界面焦点内生效）。
3. **命中计数语义未逐条断言**：探针只断言「有会话且计数回报」，未覆盖「上一个/下一个循环」「跨页面导航后计数重置」等边界。
4. **查找条遮挡**：Popup 浮在内容区右上角，页面右上有内容时会被遮住；未做「点击页面自动收起」。
5. **既有 Toast airspace 问题**（见 §2.3）未修。
6. 未验证 exFAT / 只读目录矩阵、未复现 bit-for-bit 构建、产物仍未签名。

## 五、下一步

1. 真机走查：Ctrl+F（工具栏焦点 / 网页焦点各一次）、输入即查找、Enter/Shift+Enter 循环、Esc 关闭与焦点回归、隐私标签下是否也正常（不涉及落库，应正常）。
2. 修 `Toast` 的 airspace 问题（同 Popup 方案），或统一改成内容区顶部浮层。
3. 若接受，给窗口级 Ctrl+F 加「查找条已打开时再次按下 → 全选重打」的更明确视觉反馈。
4. 进入 S4（RC 收口、故障注入、网络审计、签名与签名后复验）。
