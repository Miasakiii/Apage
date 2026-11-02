# 单文件打包、原生依赖与冷启动验证（S1 / G1）

- 证据日期：2026-10-29 ｜ 状态：**条件通过**（功能、体积通过；常规启动通过，首启存在环境波动）
- 代码：`spikes/SingleFileSpike/`（独立 SDK 风格 `net48` WPF 项目，未加入 `Apage.sln`）
- 正本：`docs/design/2026-10-28-v01-release-convergence.md`、`docs/design/2026-10-28-s0-baseline-contracts.md`

## 复现命令

```powershell
# 单文件与未合并对照（产物写入 artifacts/，不入库）
spikes\SingleFileSpike\run.ps1 -Mode SingleFile -Iterations 3
spikes\SingleFileSpike\run.ps1 -Mode Baseline  -Iterations 3

# 真实 Apage.Portable：隔离数据目录，启用便携缓存，测首启/重复启动
build\build.ps1
build\startup-bench.ps1 -Iterations 5

# 真实应用冒烟（可选输出本地阶段标记）
build\smoke.ps1 -MetricsPath artifacts\SmokeTest\startup.json
```

## 一、G1 结论

| 问题 | 结论 | 证据 |
|---|---|---|
| ① 单 EXE | **通过**。分发目录 1 文件；运行 3 轮后仍只有 EXE | `DistributionFiles=1` |
| ② 合并产物可用 | **通过**。WPF/XAML、`Apage.Core`、隐私默认值、WebView2 初始化及本地 HTML 渲染均成功，标题断言为 `ApageSingleFileSpike` | `navigationTitle`、单文件 3/3 exit 0 |
| ③ SQLite | **通过**。`DELETE` journal + `synchronous=FULL` 下建库、写入、读回 42 | `sqliteValue=42`，库 8,192 字节 |
| ④ 体积 | **通过**。单文件 2,950,144 字节；距 5MB 硬门禁余 2,049,856（41.0%） | SHA-256 `0A63ECD0…B93D8C` |
| ⑤ 启动 | **条件通过**。热启动中位 900.3ms，稳定 `<1s`；首启两批为 978.4ms / 1,016.1ms，处于阈值边缘，尚未稳定达标 | `StartupBench/20261029-181341/metrics.json` |
| ⑥ 故障边界 | **通过**。损坏原生缓存自愈、只读程序目录可运行 | 见「故障注入」 |

**判断**：架构上不存在阻塞 v0.1 的单文件、原生依赖或 SQLite 问题；S1 足以关闭。常规启动已达标，但“首次启动稳定 `<1s`”尚未兑现，最终签名单文件上必须继续测量；在此之前只能宣传参考环境下的实测值，不能承诺所有机器均 `<1s`。

## 二、单文件实现

- **托管合并**：Costura.Fody 6.2.0 + Fody 6.9.3，压缩嵌入；`Apage.Core`、`System.Text.Json`、WebView2 托管程序集、`Microsoft.Data.Sqlite`、SQLitePCLRaw 均在 EXE 内。
- **原生库**：以 `Apage.Native.win-x64.*` 逻辑名嵌入，由 `NativeBootstrap` 释放到 `%TEMP%\ApageNativeSpike\<内容哈希前16位>\`，在首次 P/Invoke 前 `LoadLibraryW` 全路径预加载。
- **排除 Costura 原生重复**：`FodyWeavers.xml` 使用 `ExcludeAssemblies="e_sqlite3|WebView2Loader"`。否则同一原生 DLL 会再以 `costura.*.compressed` 打包一次，只增体积、不参与本方案的加载。此项移除后 EXE 从 4,176,384 降至 **2,950,144** 字节。
- **SQLite provider**：不使用 `bundle_e_sqlite3`（其 `Batteries_V2.Init` 依赖 `Assembly.Location`，单文件下为空）；改用 `Microsoft.Data.Sqlite.Core` + 静态 `SQLitePCLRaw.provider.e_sqlite3`，显式 `SetProvider` + `FreezeProvider`。
- **原生 SQLite 版本**：`SQLitePCLRaw.lib.e_sqlite3` 3.50.3，规避 `2.1.11` 携带的 CVE-2025-6965；构建零 NuGet 告警。
- **仅 x64**：单文件与真实应用均固定 `PlatformTarget=x64`、`Prefer32Bit=false`，只嵌入 x64 原生库。

## 三、真实应用启动优化（S1-B）

首次测量发现两个问题：

1. `Apage.Portable` 原先以 **x86** 进程运行（AnyCPU + Prefer32Bit 默认），与 x64 首发口径冲突；已显式改为 x64。
2. 周统计加载和会话恢复位于窗口显示前，且小文件设置读取走线程池，累加首屏延迟；已调整。

调整后启动顺序：

```text
进程启动
→ 同步读取极小 settings.json
→ 创建 Environment 门面 + NTP 资源落盘 + 本地规则加载
→ MainWindow 构造并 Show（首帧可见）
→ 首帧后并行/顺序加载周统计、会话，再创建首个标签
→ WebView2 初始化并加载 NTP
```

约束：首个外部网页仍必须等隐私管线就绪；本次只延后**窗口呈现**，没有让网页绕过 DNT/GPC、Cookie 剥离或广告拦截。

### 优化后两批基准（真实 Apage.Portable，x64，隔离便携目录）

| 批次 | 首启 ContentRendered | 热启动范围 | 热启动中位 |
|---|---:|---:|---:|
| 18:11 | 978.4ms | 903.4–923.0ms | 905.3ms |
| 18:13（最新） | 1,016.1ms | 873.7–915.1ms | **900.3ms** |

最新一批明细：

| 轮次 | 类型 | 设置就绪 | 规则就绪 | 窗口 Show | ContentRendered | 首标签就绪 | WebView2 就绪 |
|---|---|---:|---:|---:|---:|---:|---:|
| 1 | 首启 | 313.2 | 325.5 | 928.4 | **1,016.1** | 952.7 | 1,769.3 |
| 2 | 热启 | 290.4 | 294.4 | 873.8 | **887.5** | 939.2 | 1,410.9 |
| 3 | 热启 | 285.9 | 288.8 | 862.2 | **873.7** | 926.5 | 1,617.6 |
| 4 | 热启 | 303.0 | 305.1 | 900.5 | **913.0** | 976.8 | 1,680.6 |
| 5 | 热启 | 289.6 | 292.1 | 900.8 | **915.1** | 977.2 | 1,453.2 |
| 中位 | — | 289.6 | 294.4 | 900.5 | **900.3**（热） | 958.0 | 1,535.4 |

- 5/5 为 x64，退出码 0；冒烟三信号通过。
- 窗口可见占约 0.9s；WebView2 就绪另需约 0.5s，产品口径只承诺“窗口可见”，不承诺内核已可交互。
- 首启样本受 Windows 文件扫描、字体/图形缓存影响明显：优化过程中观测 978.4ms、1,018ms、1,087ms、1,742ms 等值；最新为 1,016.1ms。**不对外承诺任意机器、任意首次安装均 `<1s`**；RC 必须在声明参考环境、签名后的最终单文件上重复测量。
- 对比优化前：热启动中位从 1,127ms 降至 900.3ms（-20.1%）；主要来自统计/会话移出首屏 + 设置同步读取。

## 四、运行记录

### SingleFile（3 轮，2.95MB 单 EXE）

| 轮次 | 窗口可见(ms) | SQLite 就绪(ms) | WebView2 就绪(ms) | 退出(ms) | 退出码 |
|---|---:|---:|---:|---:|---:|
| 1 | 799.1 | 889.6 | 1,556.8 | 1,977.3 | 0 |
| 2 | 689.3 | 784.2 | 1,546.2 | 1,959.5 | 0 |
| 3 | 726.5 | 816.9 | 1,552.3 | 1,969.1 | 0 |

- 分发：**1 文件 / 2,950,144 字节**。
- SHA-256：`0A63ECD0612EC06BC492B6416EDDF3C335F30EB596BE2516F78855D956B93D8C`。
- 每轮独立 WebView2 profile；导航本地 `NavigateToString` 文档并断言标题 `ApageSingleFileSpike`，零网络。

### Baseline（3 轮，未合并对照）

| 轮次 | 窗口可见(ms) | SQLite 就绪(ms) | WebView2 就绪(ms) | 退出(ms) | 退出码 |
|---|---:|---:|---:|---:|---:|
| 1 | 846.3 | 951.3 | 1,760.4 | 2,216.1 | 0 |
| 2 | 808.2 | 906.7 | 1,719.7 | 2,140.8 | 0 |
| 3 | 953.1 | 1,080.5 | 2,083.9 | 2,626.6 | 0 |

- 分发：**21 文件 / 6,777,834 字节**（x64，不含 PDB）。
- EXE 本体 2,097,664 字节；单文件相对未合并目录减少 **3,827,690 字节（-56.5%）**。
- 单文件启动与未合并对照处于同一量级，合并未引入明显首屏回退。

## 五、体积预算

| 项目 | 字节 | 结论 |
|---|---:|---|
| 单文件 EXE（实测） | 2,950,144 | 5MB 硬门禁的 59.0% |
| 5MB 硬门禁余量 | 2,049,856 | 41.0% |
| 4.25MB 开发目标余量 | 1,299,856 | 30.6% |
| 修复原生重复嵌入节省 | 1,226,240 | 4,176,384 → 2,950,144 |
| 未合并 x64 输出（不含 PDB） | 2,628,360 | 17 文件 |
| 未合并对照分发 | 6,777,834 | 21 文件 |

体积风险已从“开发目标仅余 1.73%”降为“开发目标余 30.6%”。但以下项目仍未计入：生产 UI 资源、PSL 快照、规则快照、许可文本、设置/书签窗口。S2 每引入一项都要重新称重，不能提前宣布 5MB 必然达成。

已移除的实际无效依赖：

- `CommunityToolkit.Mvvm` 8.4.2：全仓零引用，仅占位说明；移除后未合并输出减少 147,784 字节。
- Costura 重复原生资源：节省 1,226,240 字节。

## 六、失败与修复记录

### 1. `Assembly.Location` 为空导致 SQLite 初始化失败

`bundle_e_sqlite3` 的默认 `Batteries_V2.Init` 选择 `dynamic_cdecl` provider，Costura 合并后 `Assembly.Location` 为空串，路径计算抛 `ArgumentException`。

修复：`Microsoft.Data.Sqlite.Core` + 静态 provider + 显式 `SetProvider` / `FreezeProvider`。

### 2. 原生库未被加载（`DllNotFoundException`）

手写 `costuraX64` Link 名不匹配 Costura 运行时识别前缀，`e_sqlite3` 未被预加载。

修复：自建 `NativeBootstrap`，资源名 → SHA-256 → 哈希隔离目录 → 原子写 → `LoadLibraryW` 全路径。

### 3. 损坏缓存永久阻断启动

缓存 DLL 被覆写后，`File.Move` 不覆盖现有文件，重试仍失败。

修复：写入前清理无效缓存；并发下若另一方已写入有效内容则复用。复测：100 字节损坏缓存 → **exit 0**，缓存重写且哈希一致。

### 4. 原生 SQLite 携带高危漏洞

`SQLitePCLRaw.lib.e_sqlite3` 2.1.11 触发 NU1903（CVE-2025-6965）。升级到 3.50.3 后零告警、读写通过，增加 171,008 字节。

### 5. 原生库被重复打包

Costura 将包自带的 `e_sqlite3` / `WebView2Loader` 再压缩嵌入一次，但 `NativeBootstrap` 不使用这些资源。

修复：`ExcludeAssemblies="e_sqlite3|WebView2Loader"`；EXE 减少 **1,226,240 字节**，功能 3/3 通过。

### 6. 真实应用实际以 x86 运行

`Apage.Portable` 未设置 `PlatformTarget` / `Prefer32Bit`，在 x64 Windows 上以 32 位进程启动，与 x64 首发口径不符，也限制浏览器可用地址空间。

修复：显式 `PlatformTarget=x64`、`Prefer32Bit=false`；插桩与启动基准均断言 `processBitness=x64`。

### 7. 启动超过 1 秒

初始热启动中位 1,127ms。细分发现统计/会话在 Show 前加载、设置小文件走线程池、首次 XAML/布局占主要成本。

修复：

- 设置文件同步读取（极小、首屏必需）；
- 统计和会话移到 Show 之后、首个标签创建之前；
- 保持 NTP 呈现前统计已加载；
- 不把外部网页导航提前到隐私管线就绪之前。

结果：热启动中位 900.3ms；首启最新样本 1,016.1ms，仍在阈值边缘。

## 七、故障注入与边界

| 场景 | 操作 | 结果 |
|---|---|---|
| 重复启动 | 多轮连续运行 | 全部通过；后续启动不再付出缓存释放成本 |
| 损坏原生缓存 | 缓存 DLL 覆写为 100 字节 | 修复后 exit 0，缓存重写且哈希一致 |
| 只读程序目录 | 拒绝当前用户写入程序目录 | exit 0；未向程序目录写文件 |
| 分发目录收敛 | 运行后重新枚举 | 仍只有 `SingleFileSpike.exe` |
| 真实应用隔离 | 基准副本启用 PortableCache | 未读取或删除用户 `%TEMP%\Apage` / 真实 `data/` |
| 首次启动 | 全新便携副本、空 profile | 首批 978.4ms，最新 1,016.1ms；历史样本有 1.09–1.74s 波动 |

## 八、后续要求

1. **生产打包必须复用**：静态 provider、`NativeBootstrap`、Costura 原生排除、仅 x64 原生库。
2. **补旧缓存清理**：生产版按版本前缀清理 `%TEMP%\ApageNative` 旧哈希目录，避免长期堆积。
3. **最终单文件复验**：S4 对真实 `Apage.Portable` 签名产物复测体积、首启/热启和只读/exFAT 场景；本报告的 spike 启动值不能直接替代。
4. **体积持续记账**：S2 每引入 PSL、规则快照、设置窗口，重新比较 4,250,000 字节开发预算。
5. **S2 可以启动**，但生产单文件打包与首启复测不得推迟到发布前临时处理。

## 九、环境

- Windows 11 家庭中文版 build 26200；Intel Core i9-14900HX；63.71 GiB RAM。
- WebView2 Runtime 156.0.4314.50；.NET SDK 8.0.427（`global.json` 工具链 8.0.423）。
- 单文件 spike 构建：`dotnet build -c Release -p:UseCostura=true -p:Platform=x64`。
- 真实应用构建：`build\build.ps1 -Configuration Release`（VS 2022 MSBuild）。

## 十、D1 生产接入结果（2026-10-29）

S1 的独立 spike 已正式接入生产 `Apage.Portable`：

- Fody 6.9.3 + Costura.Fody 6.2.0；原生库由 `Apage.Portable/NativeBootstrap.cs` 嵌入并预加载。
- `build\package.ps1` 产出 `artifacts\portable\Apage.Portable.exe`：**1 文件 / 3,068,928 字节**。
- SHA-256：`0BE6F45B3226DBFEE701D5EA3E3048AC7E4F7DE212A3D540B048745597C73F99`。
- 单文件 smoke 四信号通过：应用启动、窗口呈现、WebView2 就绪、SQLite 安全初始化；设置窗口构造同时验证。
- 4.25MB 开发目标余量 1,181,072 字节（27.8%）；5MB 硬门禁余量 1,931,072 字节（38.6%）。
- 尚未签名；D1 后启动性能未重测，最终签名产物仍须按 S4 复验。完整记录见 [S2 第一批实施记录](../../design/2026-10-29-s2-first-slice.md)。

## 十一、A2 隐私资产体积影响（2026-10-29）

- 内置 PSL 338,012 字节（MPL-2.0）+ EasyList/EasyPrivacy 兼容规则 2,246,626 字节（94,646 条，CC BY-SA 3.0）+ 许可文本。
- 单文件产物从 D1 的 3,068,928 增至 **3,970,560 字节**（+901,632）。
- 4.25MB 开发目标余量 279,440 字节（6.6%）；5MB 硬门禁余量 1,029,440 字节（20.6%）。
- SHA-256：`08E6717A20CEBD368623028B08103D5E214C0DEEE52E6DC3FDD00FC6967F57E8`。
- 五信号 smoke 通过；PSL/规则加载位于窗口首帧后、首个标签创建前。
- 完整来源、跳过原因和许可见 [A2 记录](../../design/2026-10-29-a2-psl-rule-snapshot.md)。

## 十二、D2 正式产物复测（2026-10-29）

- 版本元数据：`0.1.0.0` / `0.1.0-dev`；单文件 **3,972,608 字节 / 1 文件**。
- SHA-256（未签名）：`5DDE1010C7398EA303EF9A1A9AAC50B63EB12DDC36798B8C2031E53B6B6FA30D`。
- 直接以正式单 EXE 运行启动基准（3 轮）：首启 ContentRendered **882.6ms**，热启 806.0 / 798.6ms，中位 **802.3ms**，本机 `<1000ms` 门禁通过。
- 4.25MB 目标余 277,392 字节（6.5%）；5MB 硬门禁余 1,027,392 字节（20.5%）。
- 产物 `signatureStatus=NotSigned`；签名后必须按 S4 重新测量，不能复用本结果。
- 打包、许可与签名前证据见 [D2 记录](../../design/2026-10-29-d2-release-engineering.md)。
