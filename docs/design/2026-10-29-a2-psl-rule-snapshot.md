# A2：PSL 与离线规则快照

> 日期：2026-10-29 ｜ 状态：**已完成** ｜ 上游：[v0.1 发布收敛计划](2026-10-28-v01-release-convergence.md) §9.2
>
> 后续：D2 增加版本元数据并重新打包，当前产物为 3,972,608 字节 / SHA-256 `5DDE1010…B6FA30D`；本文的 3,970,560 / `08E6717A…` 记录的是 A2 完成时点，保留为证据。

## 一、结论

A2 已完成，第三方判定从“末两标签近似”切换为随包 Public Suffix List，内置广告/追踪规则从“仅用户本地文件”升级为经过筛选、哈希校验和许可归档的离线快照。

| 项目 | 结果 |
|---|---|
| PSL 版本 | `2026-10-28_13-15-55_UTC`，commit `9dfbe81931367b3f0af21035c36ae7eeb830dd88` |
| PSL 文件 | 338,012 字节，MPL-2.0 |
| 规则来源 | EasyList + EasyPrivacy（CC BY-SA 3.0，上游另可选 GPL-3.0+） |
| 接受规则 | **94,646** |
| 跳过规则 | 38,432 |
| 规则文件 | 2,246,626 字节 |
| 单文件产物 | **3,970,560 字节 / 1 文件** |
| 相对 D1 | +901,632 字节 |
| 4.25MB 目标余量 | 279,440 字节（6.6%） |
| 5MB 硬门禁余量 | 1,029,440 字节（20.6%） |
| 最终 SHA-256 | `08E6717A20CEBD368623028B08103D5E214C0DEEE52E6DC3FDD00FC6967F57E8` |
| 测试 | **198 通过 / 0 失败** |

## 二、PSL 与第三方判定

新增 `Apage.Core/Services/PublicSuffixList.cs`：

- 支持普通、`*.` 通配、`!` 例外规则。
- 支持 IDN（Unicode/punycode 等价）、大小写和尾点归一化。
- IP、localhost、端口、非法 host、仅公共后缀均返回 `null`，调用方按“宁可漏判不可误判”返回非第三方。
- `ThirdPartyDetector` 改为消费 `IPublicSuffixResolver`；PSL 不可用时显式回退 `ThirdPartyDetector.Fallback`（旧末两标签算法），不会误剥。
- `BrowserTabContext.ThirdParty` 注入到请求管线；PSL 在窗口首帧后、首个标签创建前后台加载。
- 已知修正：`foo.co.uk` 与 `bar.co.uk` 现在正确判为第三方，不再因 `co.uk` 后缀漏判。

证据：`PublicSuffixListTests`（普通/通配/例外、IDN、尾点、IP、未知 TLD）与 `ThirdPartyDetectorTests`（同域/子域/多级后缀/回退）。

## 三、离线规则快照

### 3.1 生成与筛选

新增开发者工具 `tools/SnapshotBuilder/`：

```powershell
# 开发者显式联网更新；Apage 运行时不会调用
tools\SnapshotBuilder\update-snapshot.ps1

# 已有源文件时只重建
dotnet run --project tools\SnapshotBuilder\SnapshotBuilder.csproj -c Release -- `
  artifacts\snapshot-source Apage.Core\Resources\Privacy
```

分类由 Core 的 `AdBlockRuleEngine.Classify` 单一事实来源决定，快照生成与运行时入库共用，避免“打包器接受、运行器误解”的漂移。

| 跳过原因 | 条数 |
|---|---:|
| 注释/列表头 | 1,049 |
| 化妆规则 `##` | 24,196 |
| 不支持的化妆操作符 | 564 |
| 不支持的 `$` 修饰符 | 12,590 |
| 不支持的 regex 规则 | 9 |
| 超长 | 2 |
| 重复 | 22 |
| 空行/非法 | 0 |

只有 `Block` / `Exception` 网络规则进入快照；化妆规则、`$` 条件规则、`/regex/` 一律跳过，绝不退化后扩大拦截。

### 3.2 运行时校验

`PrivacyAssets.LoadEmbedded()`：

1. 读取 `privacy-assets.json`。
2. 校验 PSL 与规则文件的 SHA-256。
3. 校验 PSL 版本与清单一致。
4. 从嵌入资源加载 94,646 条规则并核对 accepted/block/exception 计数。
5. 任何不一致抛出，由宿主记录日志并回退到用户本地规则/末两标签，不带病运行。

`PrivacyAssetsReady` 已加入单文件 smoke 第五信号；真实产物实测加载成功。

### 3.3 用户规则

`{数据目录}\adblock\*.txt` 仍独立加载，不与内置快照混写；用户规则加载失败只记录日志，不覆盖内置规则。

## 四、许可与来源

- PSL：MPL-2.0，随包文件保留原始头与完整许可文本。
- EasyList/EasyPrivacy：选择 CC BY-SA 3.0；快照为过滤、去重后的衍生数据，按 CC BY-SA 3.0 分发；Apage 代码仍为 MIT。
- 许可全文与归属写入 `licenses/`，并嵌入 Core 资源：
  - `LICENSE-MPL-2.0.txt`
  - `LICENSE-CC-BY-SA-3.0.txt`
  - `THIRD-PARTY-NOTICES.txt`
- `privacy-assets.json` 记录每个来源的 URL、抓取时间、上游 SHA-256、接受/跳过数和许可。
- `build/package.ps1` 在打包前对 PSL/规则文件与清单哈希做二次校验，并把隐私资产摘要写入 `artifacts/portable.metrics.json`。

## 五、验证

- `dotnet test`：**198 通过 / 0 失败**（新增 PSL、第三方判定、规则分类、隐私资产清单测试）。
- `build/build.ps1 -Configuration Release`：通过。
- `build/check-redlines.ps1`：五条通过。
- `build/package.ps1 -SkipBuild`：1 文件 / 3,970,560 字节；五信号 smoke 通过（应用、窗口、WebView2、SQLite、PSL/规则快照）。
- 抽样拦截：`doubleclick.com` 与 `googlesyndication.com/pagead/` 命中内置规则。

## 六、限制与下一步

1. A3 的「立即更新规则」尚未实现；v0.1 运行时无规则/PSL 联网更新。
2. 当前快照只含无 `$` 修饰符的 URL 规则；资源类型、domain、third-party 条件规则全部安全跳过，后续完整 ABP 引擎再放开。
3. 化妆规则未注入；引擎仅保留基础分类能力。
4. 4.25MB 开发目标余量已降至 6.6%；A3/B2/设置扩展必须继续称重，D2 需把体积门禁固定为 CI 硬门禁。
5. 启动性能未在 A2 产物上重测；新增的 2.25MB 规则加载已移到首帧后，但仍需 D2 基准复测。
