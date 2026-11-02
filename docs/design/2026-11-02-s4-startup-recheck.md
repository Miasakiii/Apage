# S4 启动复验（2026-11-02）

- 日期：2026-11-02
- 范围：S0–S3 提交基线后的单文件启动复验、打包门禁与当前发布判断
- 前置：[v0.1 发布收敛计划](2026-10-28-v01-release-convergence.md)、[S3 页内查找](2026-10-30-s3-find-in-page.md)、[S0 基线与契约](2026-10-28-s0-baseline-contracts.md)
- 代码基线：`0d50f96`，工作区干净
- 产物：`artifacts/portable/Apage.Portable.exe`

## 一、结论

本轮正式打包门禁**条件通过**，但不满足完整发布验收：

| 项目 | 结果 |
|---|---|
| 单文件 | 1 文件 / **4,060,160 字节** |
| SHA-256 | `5D155777898F0E9210E19FD1A718EFB9307FC10E5DF6E3A086413A3D1B3140F6` |
| 版本 / 签名 | 0.1.0.0（0.1.0-dev） / **NotSigned** |
| 许可与隐私资产 | 通过；PSL `2026-10-28`，规则 94,646 接受 / 38,432 跳过 |
| 五信号 smoke | 通过（应用 / 窗口 / WebView2 / SQLite / 隐私资产） |
| 首启 ContentRendered | **1077.5ms（超过 1000ms）** |
| 热启 ContentRendered | 965.4 / 912.9ms，中位 **939.15ms** |
| 打包脚本门禁 | **PASSED**（只判定热启中位） |
| 完整发布验收 | **未通过**：首启仍超过 1s，且测量环境不是稳定参考环境 |

因此当前 EXE 是可复验的候选产物，不是已放行 RC。**没有修改 `package.ps1` 阈值，也没有为通过门禁做代码特化。**

## 二、测量环境

- Windows 开机后测量；采样时系统已运行约 25.5 分钟。
- 五轮 CPU 采样：38 / 27 / 38 / 33 / 32%，均值 **33.6%**，峰值 **38%**；更早一次采样的均值为 36.2%、峰值 44%。
- 持续占用主要来自 `wallpaper32`（动态壁纸）与 `msedgewebview2` 后台进程。
- 用户说明该负载包含开机阶段影响；但开机 25 分钟后仍维持约 33%，因此**不能认定为安静参考机**。
- 产物位于本地磁盘，基准使用隔离副本与便携缓存，不读取真实 `data/`。

## 三、五轮探索性基准

命令：

```powershell
build\startup-bench.ps1 `
  -ExePath artifacts\portable\Apage.Portable.exe `
  -Iterations 5 -OutputDirectory artifacts\startup-2026-11-02 `
  -WarmMedianLimitMs 1000
```

| 轮次 | 类型 | ContentRendered | MainWindowXaml | WindowShown |
|---|---|---:|---:|---:|
| 1 | first | 1159.8ms | 722.5ms | 1091.3ms |
| 2 | warm | 959.1ms | 598.6ms | 933.5ms |
| 3 | warm | 999.4ms | 629.1ms | 970.9ms |
| 4 | warm | 1107.2ms | 665.5ms | 1032.2ms |
| 5 | warm | 912.2ms | 554.7ms | 877.6ms |

- 热启中位：**979.25ms**，条件通过。
- 热启范围：912.2–1107.2ms，极差 **195.0ms**；单轮仍可越过 1000ms。
- 首启 1159.8ms，明显高于热启中位。

## 四、正式打包复验

命令：

```powershell
build\package.ps1 -SkipBuild -Configuration Release -RequireCleanWorktree
```

结果：

- 许可 / 五信号 smoke / 单文件 / 体积 / SHA-256 / legal bundle / manifest 全部通过。
- 启动首启 **1077.5ms**；热启 965.4 / 912.9ms，中位 **939.15ms**。
- 相对 1000ms 阈值余量 **60.85ms（约 6.1%）**。
- 体积相对 4.25MB 开发目标余 **189,840 字节（4.5%）**；相对 5MB 硬上限余 **939,840 字节（18.8%）**。

分项轨迹（三次运行）：

| 阶段 | 范围 |
|---|---|
| OnStartup | 116.1–190.3ms |
| Settings | 295.7–377.5ms |
| NtpAssets | 296.7–387.5ms |
| MainWindowXaml | 555.5–691.7ms |
| WindowShown | 849.9–1051.9ms |
| ContentRendered | 912.9–1077.5ms |
| TabsReady | 1152.3–1333.4ms |
| WebViewReady | 1863.2–2069.1ms |

`MainWindowXaml` 是窗口显示前最大的单项；`Show()` 到首帧还会增加约 **286–348ms**。TabsReady、隐私资产与 WebView2 均发生在 ContentRendered 之后，当前门禁口径下它们不是首屏阻塞项。

## 五、判断与限制

1. **门禁条件通过，不等于发布验收通过**：打包脚本只要求热启中位 <1000ms；S0 发布目标同时要求记录首启，而首启仍为 1077.5ms。
2. **当前数据不能证明代码回归**：历史安静负载读数约 800–900ms，本轮在 33%–36% CPU 持续占用下测得 939.15ms；没有同环境 A/B 对照。
3. **当前数据也不能证明稳定达标**：热启极差 195ms、余量仅 60.85ms，且存在 1107.2ms 的单轮。
4. **不做盲目优化**：在噪声主导的环境里改 XAML 无法证明收益，反而可能破坏已验证的界面行为。优化必须以稳定参考机复测为前提。
5. **签名会改变最终产物**：当前哈希只对应未签名单文件；签名后必须对同一产物重跑全部门禁。

## 六、S4 剩余门禁

### 6.1 启动与性能

- 在稳定参考环境复测首启与热启；记录 CPU、电源计划、Windows / WebView2 版本与后台进程。
- 至少 5 轮，首启单独报告，热启用中位数；不引用单次最好值。
- 若热启仍无稳定余量，再按 `MainWindowXaml → Show → Settings` 顺序定位，优先检查首屏资源字典与非必要控件延迟实例化。

### 6.2 交互与故障矩阵

- 真机点击：Ctrl+F 全路径、Toast 可见性与点击穿透、地址栏建议、隐私盾、下载打开/取消。
- 真实网络规则更新一次：按钮 → HTTPS 下载 → 校验 → 原子替换 → 新标签生效。
- 故障注入：WebView2 缺失、SQLite 损坏、缓存损坏、只读目录、exFAT、异常退出残留。
- 网络审计：未点击规则更新时，主机侧新增连接为零；确认唯一出站文件豁免仍有效。

### 6.3 发布

- 冻结功能，关闭所有 P0/P1 缺陷。
- 签名最终 EXE，验证 Authenticode 与 SHA-256。
- 对签名后的同一产物重跑：五信号 smoke、体积、首启/热启、只读/exFAT、网络审计。
- 更新 README、roadmap、release manifest 后，才可标记 v0.1 可发布。

## 七、复现命令

```powershell
dotnet test Apage.Core.Tests/Apage.Core.Tests.csproj -c Release
build\build.ps1 -Configuration Release
build\check-redlines.ps1
build\check-licenses.ps1
build\package.ps1 -SkipBuild -Configuration Release -RequireCleanWorktree
build\verify-signature.ps1
```
