# S2 第一批（D1 / A1 / B1 / C1）实施记录

> 日期：2026-10-29 ｜ 状态：**已完成** ｜ 上游：[v0.1 发布收敛计划](2026-10-28-v01-release-convergence.md) §9.1

## 一、结论

第一波四个 P0 首切片全部落地并通过质量门禁：

| 包 | 结果 | 关键证据 |
|---|---|---|
| **D1 生产单文件** | 完成 | 1 文件 / **3,068,928 字节**；真实单文件 smoke 四信号通过 |
| **A1 规则安全与统计纠偏** | 完成 | 不支持 `$` 修饰符 fail-closed；统计 2 秒批次落盘；仅实际移除 Cookie 才计数 |
| **B1 SQLite 安全基础** | 完成 | schema v1、事务迁移、迁移前备份、损坏不删库、并发初始化测试通过 |
| **C1 设置与本地诊断** | 完成 | 真实设置窗口 + 菜单/NTP 入口；滚动日志与手动导出，URL/Cookie 默认脱敏 |

质量门禁实测：

- `dotnet test Apage.Core.Tests/Apage.Core.Tests.csproj -c Release`：**152 通过 / 0 失败**。
- `build\check-redlines.ps1`：五条全部通过。
- `build\build.ps1 -Configuration Release`：成功。
- `build\package.ps1 -SkipBuild`：打包与单文件 smoke 通过。

## 二、D1 生产单文件

### 2.1 实现

- `Apage.Portable.csproj` 接入 Fody 6.9.3 + Costura.Fody 6.2.0；排除 `e_sqlite3|WebView2Loader` 的重复原生嵌入。
- 新增 `Apage.Portable/NativeBootstrap.cs`：将 x64 WebView2Loader/e_sqlite3 释放到 `%TEMP%\ApageNative\{内容哈希}\`，首次 P/Invoke 前以完整路径预加载；损坏缓存重写，旧哈希目录清理失败不阻断。
- SQLite 使用 `Microsoft.Data.Sqlite.Core` + 静态 `SQLitePCLRaw.provider.e_sqlite3`，不使用依赖 `Assembly.Location` 的 dynamic provider。
- `build\package.ps1` 负责正式打包、单文件断言、体积门禁、SHA-256、隔离 smoke 和 JSON 清单；`build\smoke.ps1` 新增 `-ExePath`，可验证任意干净产物。

### 2.2 产物

| 项目 | 值 |
|---|---|
| 分发路径 | `artifacts\portable\Apage.Portable.exe` |
| 文件数 | **1** |
| 字节 | **3,068,928** |
| 5MB 硬门禁余量 | 1,931,072（38.6%） |
| 4.25MB 开发目标余量 | 1,181,072（27.8%） |
| SHA-256 | `0BE6F45B3226DBFEE701D5EA3E3048AC7E4F7DE212A3D540B048745597C73F99` |
| 清单 | `artifacts\portable.metrics.json` |

复现：

```powershell
build\build.ps1 -Configuration Release
build\package.ps1 -SkipBuild -Configuration Release
```

### 2.3 smoke 口径扩展

真实单文件 smoke 现在验证四信号：应用启动、主窗口呈现、WebView2 内核就绪、**SQLite 安全初始化完成**；并额外真实构造设置窗口，捕获 XAML 资源/构造异常。实测 **PASS，约 2 秒**。

## 三、A1 规则与统计

- 带 `$` 修饰符的规则在引擎不能完整求值时整条拒收，并计入 `UnsupportedModifierRuleCount`；禁止去掉修饰符后宽泛匹配。`@@` 例外规则同样适用。
- `PrivacyStatsService` 改为脏标记 + 2 秒一次性 Timer 批量原子落盘，正常退出仍显式保存；并发保存由信号量串行化，崩溃最多损失一个批次。
- `BrowserTabView` 只有在请求确实携带 `Cookie` 且被成功移除时才记录“追踪拦截”，消除无 Cookie 子资源虚增。
- 定向测试 51 项通过，覆盖拒收、例外不可绕过、自动落盘、并发保存、跨周与损坏回落。

## 四、B1 SQLite

- 新增 `Apage.Core/Data/`：
  - `SqliteDatabase`：PRAGMA 基线、`integrity_check`、迁移前备份、`user_version` 事务迁移、失败回滚、版本降级拒绝。
  - `SqliteSchema` v1：`bookmarks`、`history_entries`、`downloads` 及必要索引。
  - `SqliteProvider`：静态 e_sqlite3 provider，单文件安全。
- 数据库初始化在窗口显示后与首个标签并行执行；失败写入本地日志且不拖垮浏览外壳。
- 6 项定向测试通过：建库/PRAGMA、幂等与并发、迁移前备份、迁移失败回滚、损坏不删库、备份恢复；schema 升级/降级边界由测试覆盖。

## 五、C1 设置与诊断

- 新增 `SettingsWindow`：只展示已有消费方的搜索、第三方 Cookie、DNT、GPC、WebRTC 和缓存位置；WebRTC/缓存明确标注重启生效；无 Theme/ClearOnExit/在线建议等死开关。
- 保存采用“草稿 → 写盘成功 → 应用到运行中实例”，失败回滚；设置可从工具栏菜单与 NTP `openSettings` 打开。
- `LocalLogService`：本地滚动（默认 1MB × 3）、手动导出、线程安全；URL 与敏感头自动脱敏，异常只写类型/HResult/堆栈，不写 Message；所有 IO 失败返回 `false`，不升级为应用故障。
- App 已接入 UI、AppDomain、未观察任务异常钩子；设置窗口构造和资源加载由 smoke 验证。

## 六、已知限制与下一波

仍未完成，属于后续包而非本批缺陷：

1. 记录本批时规则快照、PSL 和手动更新（A2/A3）尚未交付；**A2 随后已完成**（见 [PSL 与离线规则快照](2026-10-29-a2-psl-rule-snapshot.md)），A3 手动更新仍未实现。
2. SQLite 仅有安全基础设施与 schema，书签/历史/下载 CRUD 与导入（B2）未实现。
3. 最终 Authenticode 签名、签名后复验、干净虚拟机/exFAT 全矩阵属于 S4。
4. 设置窗口已做构造级 smoke，保存交互仍需真机点击复验。
5. D1 后未重新测量启动性能；S1 的 900.3ms 热启动中位仍是上一产物结果，最终 RC 必须复测。

下一波按 `A2 ✅ / D2 ✅ / B2 / A3` 展开，具体依赖见收敛计划 §9.2。
