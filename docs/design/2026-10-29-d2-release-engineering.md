# D2：可重复打包、启动门禁与签名前发布工程

> 日期：2026-10-29 ｜ 状态：**已完成（签名执行留 S4）** ｜ 上游：[v0.1 发布收敛计划](2026-10-28-v01-release-convergence.md) §9.2

## 一、结论

D2 将 D1 的“能生成单 EXE”升级为可重复执行的发布入口：

| 能力 | 结果 |
|---|---|
| 正式入口 | `build\package.ps1` 串联构建、许可校验、单文件断言、体积门禁、五信号 smoke、启动基准、release manifest |
| 版本 | **0.1.0.0** / ProductVersion `0.1.0-dev` |
| 当前产物 | **3,972,608 字节 / 1 文件** |
| SHA-256 | `5DDE1010C7398EA303EF9A1A9AAC50B63EB12DDC36798B8C2031E53B6B6FA30D` |
| 4.25MB 目标余量 | 277,392 字节（6.5%） |
| 5MB 硬门禁余量 | 1,027,392 字节（20.5%） |
| 首次 ContentRendered | **882.6 ms** |
| 热启动中位 ContentRendered | **802.3 ms**（本机门禁 <1000ms，通过） |
| 许可 | MIT（项目）+ MPL-2.0（PSL）+ CC BY-SA 3.0（EasyList 快照） |
| 签名 | **NotSigned**；SignPath 申请/密钥/证书尚未启用 |
| 构建来源 | commit `88e08a9…`，当前工作区 `worktreeDirty=true`（开发态） |

## 二、可重复打包

```powershell
# 正式发布：构建 + 全部门禁 + 启动基准
build\package.ps1 -RequireCleanWorktree

# 已构建后复验
build\package.ps1 -SkipBuild -RequireCleanWorktree

# 只做包体/许可，不跑启动基准（调试用，不得用于发布）
build\package.ps1 -SkipBuild -SkipStartupBench
```

`package.ps1` 的强制项：

1. `check-licenses.ps1`：项目/第三方许可、来源、PSL/规则 SHA-256、许可资源是否嵌入。
2. 分发目录只能有 `Apage.Portable.exe`。
3. **4.25MB 开发目标**超限直接失败；确需例外必须显式 `-AllowDevTargetOverage`。
4. 5MB 硬门禁。
5. 五信号 smoke 在隔离副本中运行，分发目录保持单文件。
6. 启动基准直接对打包单 EXE 运行，不是对 `bin\Release` 多文件目录。
7. manifest 写入版本、git commit、工作区状态、签名状态、启动结果、PSL/规则摘要。

当前 Artifact 版本元数据由 `Apage.Portable/Properties/AssemblyInfo.cs` 固定为 `0.1.0.0`，避免出现 `0.0.0.0` 的不可审计产物。

## 三、启动门禁

`build\startup-bench.ps1` 新增：

- `-ExePath`：只复制该 EXE 到隔离 app 目录，真实测量单文件产物；
- `-OutputDirectory`：限制在 `artifacts/` 下，输出稳定供 CI/发布读取；
- `-WarmMedianLimitMs`：热启动中位 ContentRendered 硬门禁，本地默认 1000ms；
- JSON 结果包含首启、热启中位、TabsReady、PrivacyAssetsReady、WebView2 就绪、门槛与通过状态。

本次 D2 实测（本机参考环境）：

| 轮次 | 类型 | ContentRendered |
|---|---|---:|
| 1 | 首启 | 882.6ms |
| 2 | 热启 | 806.0ms |
| 3 | 热启 | 798.6ms |
| 中位 | 热启 | **802.3ms** |

GitHub Actions runner 不是 S0 的参考硬件，CI 使用 `-StartupLimitMs 1500` 记录非参考门禁；正式发布必须在参考机按 1000ms 复测，不能用 CI 数值替代。

## 四、许可 bundle 与签名前检查

`artifacts/legal/` 生成独立可上传的法律包（分发目录仍只有一个 EXE）：

- `LICENSE-MIT.txt`
- `LICENSE-MPL-2.0.txt`
- `LICENSE-CC-BY-SA-3.0.txt`
- `THIRD-PARTY-NOTICES.txt`
- `components.json`（来源、版本、哈希、许可、文件哈希）

许可全文同时嵌入 Core 资源，单文件分发不会丢失 notice。`build/check-licenses.ps1` 在本地和 CI 都执行。

签名前的可执行检查：

```powershell
build\verify-signature.ps1 -ExePath artifacts\portable\Apage.Portable.exe
```

当前应输出 `Signature: NotSigned`，并记录 SHA-256。签名申请、SignPath artifact configuration 与签名后复验步骤见 [signing.md](../release/signing.md)。

## 五、CI

- `core-and-tests`：红线 + 许可检查 + Core 测试。
- `portable-build`：Release 构建 + package（含单文件、体积、五信号 smoke、启动基准）+ 上传 EXE、SHA、manifest、legal、startup metrics。
- CI 使用 `-RequireCleanWorktree`，要求签名/打包来源可对应提交；`FodyWeavers.xsd` 为生成物，已加入忽略，避免把构建噪声误判为脏工作区。

## 六、边界与未完成项

1. **未签名**：SignPath 申请和证书尚未完成，`signatureStatus=NotSigned`；S4 才可执行签名。
2. **非逐字节复现验证**：脚本可重复执行，并记录 commit/脏状态；尚未对同一提交做两次独立构建的 bit-for-bit 比对。
3. **CI 启动阈值非参考**：CI 1500ms 仅记录/防回退，发布仍按 1000ms。
4. **未做干净虚拟机/exFAT/只读目录全矩阵**：S4。
5. 4.25MB 余量仅 6.5%，A3/B2 继续增加代码或资源时必须先看 package manifest。
6. 签名后必须重新跑 smoke、启动、体积与 Authenticode；不能复用未签名 hash。
