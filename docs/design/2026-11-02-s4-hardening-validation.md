# S4 硬化验证：真实更新、网络与故障注入（2026-11-02）

- 日期：2026-11-02
- 范围：阶段 C 中可在本机自动执行的部分
- 关联：[P0/P1 硬化记录](2026-11-02-p0-p1-hardening.md)、[Cookie 探针](../spikes/results/cookie-privacy.md)
- 产物：`artifacts/portable/Apage.Portable.exe`（4,068,352 字节，NotSigned）

## 一、通过项

### 1. 真实 HTTPS 规则更新

探针：`spikes/RuleUpdateProbe`，复用生产 `RuleUpdateClient` + `RuleUpdateService`，在独立临时目录执行。

```text
success=True
accepted=96002; skipped=38457; elapsedMs=3518
diskLoaded=True; origin=updated; userRuleKept=True
manifestAccepted=96002
sha256=a84f84d149a2c9ec2948c7b9439458924ed8eb7d205c6d9539a596c248e29b0e
```

验证点：

- EasyList / EasyPrivacy 真实 HTTPS 下载成功；
- 共用筛选、原子快照、回读校验、provider 热替换成功；
- 临时 `adblock/custom.txt` 在更新后仍生效；
- 未放宽红线的唯一出站文件边界。

### 2. 动态出站网络采样

隔离副本、独立数据目录启动单文件 EXE，20 秒内 4 次采样应用及其全部 `msedgewebview2.exe` 子进程：

```json
{ "Samples": 4, "DurationSec": 20, "RemoteCount": 0 }
```

- 仅排除了 loopback / unspecified 地址；未发现远程 TCP 连接。
- 这是运行期采样证据，不等于防火墙级全量审计；静态红线仍锁定唯一网络类型文件为 `RuleUpdateClient.cs`。

### 3. 损坏 SQLite

在隔离 `data/apage.db` 写入非 SQLite 字节后启动正常模式：

```json
{ "StayedAlive": true, "DbPreserved": true, "SqliteFailureLogged": true }
```

- 应用窗口继续可用；
- 数据库文件字节未被删除或覆盖；
- `sqlite-init` 异常写入本地日志，数据能力降级而非拖垮外壳。

### 4. 原生库与 WebView2 缓存损坏

设置隔离 `%TEMP%`：

1. 首次 `--smoke` 初始化缓存，exit 0；
2. 篡改 `ApageNative/{hash}/WebView2Loader.dll` 前 64 字节；
3. 向 `Apage/cache/WebView2` 写入垃圾标记并破坏 `Local State`；
4. 第二次 `--smoke`，exit 0。

结论：原生库按哈希自愈，WebView2 缓存损坏未阻断五信号启动。

### 5. 只读数据目录

对隔离 `data/` 添加当前用户 `Deny Write` ACL 后启动正常模式：

```json
{ "StayedAlive": true, "FilesWrittenToReadOnlyData": 0 }
```

- 窗口保持可用，未因日志/设置/数据库不可写而崩溃；
- ACL 已在测试后恢复，临时目录已清理。
- 该场景下 session/统计/书库/SQLite 数据能力不可用，属预期降级。

## 二、未完成项（不得视为通过）

| 项 | 原因 | 所需条件 |
|---|---|---|
| exFAT 真盘矩阵 | 当前无 exFAT 移动介质 | 物理 U 盘/分区，复跑 AtomicJsonStore、SQLite、原生缓存与只读矩阵 |
| 真机点击 | 本环境无法可靠模拟真实鼠标/键盘走查 | Ctrl+F 全路径、隐私盾、下载打开/取消、设置保存 |
| 稳定参考机启动 | 当前机器存在持续后台负载 | 至少 5 轮首启/热启，记录 CPU、电源计划、Windows/WebView2 版本 |
| 代码签名 | 无可用 Authenticode 证书 | SignPath 审批或自有证书；签名后同产物全量复验 |

## 三、结论

P0/P1 硬化项的自动化证据已齐备；真实规则更新、SQLite/缓存/只读目录降级均通过。**v0.1 RC 仍不能放行**，阻断项为 exFAT 矩阵、真机交互、参考机启动和签名。
