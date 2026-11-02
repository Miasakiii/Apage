# P0/P1 硬化：隐私承诺纠偏、下载状态机与持久化安全（2026-11-02）

- 日期：2026-11-02
- 范围：阶段 A（2 个 P0）与阶段 B（5 组 P1）
- 代码基线：`cfe9472` 之后的工作区改动
- 关联：[v0.1 发布收敛计划](2026-10-28-v01-release-convergence.md)、[S4 启动复验](2026-11-02-s4-startup-recheck.md)、[Cookie 探针结果](../spikes/results/cookie-privacy.md)

## 一、结论

1. **“第三方 Cookie 剥离”被真机证伪，已下架。** WebView2 会在 `WebResourceRequested` 之后由网络栈重新附加 Cookie；事件里读得到、删得掉，但服务器仍收到。产品不再提供该能力、不再计“追踪”数，也不在 UI/README 中宣传。
2. **请求管线不再把所有 `Document` 当顶层主文档。** 优先用 `Sec-Fetch-Dest`、回退到顶层 `NavigationStarting` 记录，只豁免真正主文档；第三方 iframe 文档重新进入广告规则判断。
3. **下载写入按 id 串行，终态不可回退；孤儿清理不再全表误伤。**
4. **会话恢复完成前关闭窗口不再覆盖 `session.json`。**
5. **设置/会话/统计统一 `AtomicJsonStore`。** Replace 不可用时先备份再覆盖，不再“删除唯一正式副本”。
6. **规则更新保留用户规则、全程串行、逐跳校验 HTTPS。**
7. **安全收尾完成：** NTP 消息使用 `e.Source` 精确校验；URL userinfo 不落库；日志导出拒绝覆盖源文件并原子替换目标；ABP authority 归一化覆盖 userinfo 与 DNS 尾点。

## 二、Cookie 真机探针

探针：`spikes/CookieProbe`。同源 HTTP 页面设置 Cookie，随后请求图片；第二轮在 `WebResourceRequested` 中读取并删除 Cookie 头，服务器记录实际收到的头。

| 轮次 | 事件内 Cookie | 删除次数 | 服务器收到 Cookie |
|---|---|---:|---|
| baseline | `sid=probe-cookie-value` | 0 | 是 |
| removal | `sid=probe-cookie-value` | 1 | **是** |

- Runtime：WebView2 `156.0.4314.58`。
- 结论：`verdict = ineffective-server-still-received-cookie`。
- 官方时序亦明确：事件修改头之后，WebView2 网络栈仍可附加 Cookie / Authorization（`https://learn.microsoft.com/en-us/microsoft-edge/webview2/how-to/webresourcerequested`）。
- 同源测试排除了 SameSite / 第三方策略干扰：若同源都能重新附加，事件级“剥离”不能作为第三方防护承诺。
- 未采用 `--ignore-certificate-errors` 等降低 TLS 校验的探针方案。

### 2.1 产品处理

- `AppSettings.BlockThirdPartyCookies` 保留为旧 JSON 兼容字段，默认 `false`，无 UI、无请求管线消费、无统计。
- 设置页明确写明：当前 WebView2 无可靠阻断入口，本版本不提供该能力。
- NTP 统计卡只显示本周广告拦截数；隐私盾删除“追踪”栏。
- Tracking Prevention 仍使用 WebView2 默认 Balanced；它按已知追踪器名单工作，不等价于“第三方 Cookie 全阻断”，因此不作超额承诺。

## 三、代码改动

| 领域 | 改动 |
|---|---|
| 请求分类 | `BrowserTabView` 增加有界顶层导航 URI 记录；`IsTopLevelDocumentRequest` 只豁免主文档；iframe Document 参与广告规则 |
| 下载 | `DownloadCoordinator` 每 id 一条写链；`DownloadRepository` 的进度/终态 SQL 限定 `state=in_progress`；新增按 id / 按启动时间裁剪的孤儿清理 |
| 会话 | `MainWindow._sessionRestoreCompleted` 门控；恢复未完成或异常时退出不写 session |
| 原子写 | 新增 `AtomicJsonStore` + `IAtomicFileSystem`；Settings/Session/PrivacyStats 共用；读按 正式文件 → `.bak` → `.tmp` |
| 规则更新 | `UserRuleStore` 统一用户规则读取；更新前预读、替换前内存叠加；`SemaphoreSlim` 串行整个更新；`AllowAutoRedirect=false` 逐跳验证 HTTPS |
| 安全 | NTP 要求 `e.Source` 与顶层 `core.Source` 都是 index.html；`UrlKey` 清空 userinfo；日志导出源路径冲突拒绝 + 临时文件替换；ABP URL authority 归一 |
| 隐私 UI | 设置移除 Cookie 开关并展示限制；NTP/隐私盾移除追踪计数 |

## 四、验证

| 门禁 | 结果 |
|---|---|
| Core 单测 | **305 / 305 通过** |
| 红线 | 通过；新增根目录路径匹配自测（`Apage.Core/Data` 合法、根 `data/` 必须命中） |
| 许可 | 通过 |
| Release 构建 | 通过 |
| 五信号 smoke | 通过；含数据往返、查找/Toast 布局与 NTP `getStats` 来源门控探针 |
| 单文件打包（干净工作区） | 通过；1 文件 / **4,068,352 字节** / SHA-256 `573E5F52…9E582FEE` / `worktreeDirty=false` / NotSigned |
| 正式启动基准 | 首启 **1026.2ms（>1s）**；热启中位 **959.25ms**；打包门禁按热启通过，完整发布未通过 |
| Cookie 探针 | 通过执行，结论为“事件删除无效” |

新增回归覆盖：原子写 Replace/Copy 故障、备份/临时文件恢复、下载终态不可回退、按 id/时间裁剪孤儿、用户规则热更新保留、并发更新串行、URL userinfo、日志导出源冲突、ABP userinfo/尾点。

## 五、剩余发布门禁

以下仍属 S4。可在本机自动执行的部分已记录于 [S4 硬化验证](2026-11-02-s4-hardening-validation.md)：

1. 干净工作区正式 `package.ps1 -RequireCleanWorktree` 已通过；首启 1026.2ms 仍超 1s，需稳定参考机 5 轮复测并与签名后同产物复验；
2. 稳定参考机至少 5 轮启动复测；
3. 真实网络规则更新已通过；真机点击矩阵（Ctrl+F / 隐私盾 / 下载打开与取消）待人工；
4. 只读目录、缓存与 SQLite 损坏已通过；exFAT 真盘与异常退出矩阵待物理介质；
5. 20 秒进程树出站采样为 0；防火墙级全量网络审计待参考环境。
