# WebMessage 通信链路验证（roadmap Phase 0 / product-design.md §4.4）

- 证据日期：2026-07-19（首次运行，FAIL，根因为脚手架缺陷）；**补跑：2026-10-26（PASS，exit 0）**；本文整理：2026-10-23，补跑后更新：2026-10-26
- 环境：.NET 8（`net8.0-windows`，WPF）；`Microsoft.Web.WebView2` SDK **1.0.4078.44**（两次运行同版本）；本机 WebView2 Runtime：2026-07-19 为 **150.0.4078.65**、2026-10-26 为 **155.0.4283.62**
- 代码：`spikes/WebMessageSpike/`（独立 WPF 项目，未加入 `Apage.sln`）
- 复现：`dotnet run --project spikes/WebMessageSpike`（`--stay` 保留窗口便于观察；看门狗 120s 未完成即强退 exit 3）
- 运行日志：`spikes/WebMessageSpike/bin/Debug/net8.0-windows/webmessage-spike-run.log`（自动落盘，随每次运行覆盖；`*.log` 不入库，本文嵌入原文）

## 验证什么（roadmap Phase 0 该验证项）

> WebMessage 通信链路验证——`WebMessageReceived` ↔ `postMessage` 请求/响应 id 关联，覆盖注入脚本场景。

拆成三个可判定问题：

1. **请求/响应 id 关联是否成立**（并发、乱序响应下每条响应能否解析回自己的请求）；
2. **异步非阻塞是否证实**（JS↔C# 往返不阻塞渲染/UI 线程）；
3. **注入脚本场景是否覆盖**（经 `AddScriptToExecuteOnDocumentCreated` 注入的桥在 document-start 即可用）。

## 方案（product-design.md §4.4）

通道即 §4.4 设计：JS 经 `window.chrome.webview.postMessage({id,type,payload})` 上行，C# 在 `WebMessageReceived` 中异步分发后经 `PostWebMessageAsJson({id,ok,data})` 下行；JS 侧以 `id → {resolve,reject}` 的 `pending` Map 维护请求/响应关联（bridge.js:9-20）。

- **注入**：`bridge.js` 作为嵌入资源，`AddScriptToExecuteOnDocumentCreatedAsync` 注册（MainWindow.xaml.cs:71-73），document-start 执行。
- **离线可复现**：`SetVirtualHostNameToFolderMapping("apage-spike.local", wwwroot)` 把测试页映射到本地（MainWindow.xaml.cs:69），导航 `https://apage-spike.local/testpage.html`，无外部网络依赖。
- **后端分发**（BackendDispatcher.cs）：`ping`（心跳 + 延迟实测）、`echo`（原样回显，验 id 关联）、`delayedEcho`（按 `delayMs` 延时后回显，人为制造乱序）、`boom`（抛异常，验错误路径）。
- **断言集中在 JS 侧**（bridge.js `__runSpikeTests`，结果写入 `window.__spikeResults`，C# 轮询读回）：

| # | 断言 | 对应问题 |
|---|---|---|
| A1 | 5 次顺序 `echo`，每个响应 payload 与请求一一对应 | ① id 关联（顺序） |
| A2 | 20 次并发 `delayedEcho`，先发者延迟最长 → 响应逆序到达，各自解析回自己的 `n` | ① id 关联（并发乱序） |
| A3 | document-start（测试页 head 内联脚本）即可调用 `callBackend('ping')` 并收到 `pong` | ③ 注入脚本场景 |
| A4 | `boom` → JS reject 且错误信息含 `spike-boom`，随后 `ping` 仍成功（通道可复用） | 错误路径 |
| A5 | 100 次顺序 `ping`，统计 avg/p50/p95/max 延迟 | ② 异步 + 量化延迟 |
| A6 | 结束后 `pending` Map 无残留 | 无泄漏 |

## 运行记录一（2026-07-19，FAIL，据日志原文）

```
[15:45:07.805] == WebMessage Spike 开始（docs/product-design.md §4.4）==
[15:45:07.836] WebView2 Runtime 版本: 150.0.4078.65
[15:45:08.149] 注入脚本已注册（AddScriptToExecuteOnDocumentCreatedAsync）
[15:45:08.152] 导航: https://apage-spike.local/testpage.html
[15:45:08.218] 导航完成: IsSuccess=True WebErrorStatus=Unknown
[15:45:08.741] ExecuteScriptAsync(全量断言) 返回，耗时 4 ms
[15:45:08.762] EXCEPTION: System.Collections.Generic.KeyNotFoundException: The given key was not present in the dictionary.
   at System.Text.Json.JsonElement.GetProperty(String propertyName)
   at Apage.Spikes.WebMessage.MainWindow.ReportResults(String json) in ...\MainWindow.xaml.cs:line 159
   at Apage.Spikes.WebMessage.MainWindow.RunSpikeAsync() in ...\MainWindow.xaml.cs:line 97
[15:45:08.772] == SPIKE 结果: FAIL (exit 1) ==
```

时间戳佐证这是当前源码的真实运行：日志（15:45:08）晚于全部源码最后修改（最晚 `MainWindow.xaml.cs` 15:44:10）。

**根因**：`ExecuteScriptAsync` 在 4ms 内返回，随后 `ReportResults` 在 `MainWindow.xaml.cs:159`（`root.GetProperty("assertions")`）抛 `KeyNotFoundException`——即返回的 JSON 里**没有** `assertions` 键。归因链：

1. `__runSpikeTests` 是 `async function`，调用它得到的是一个 **Promise**（bridge.js:35）。
2. SDK 1.0.4078.44 的 `ExecuteScriptAsync` 官方语义是「把脚本结果做 JSON 编码返回」（见随包 `Microsoft.Web.WebView2.Core.xml`：*"A JSON encoded string that represents the result of running the provided JavaScript"*），**不 await / 不解析返回的 Promise**。Promise 序列化为 JSON 得 `{}`（无自有可枚举属性）。
3. 因此 `ExecuteScriptAsync("window.__runSpikeTests()")`（MainWindow.xaml.cs:93）拿到的是 `{}`，`GetProperty("assertions")` 即抛 `KeyNotFoundException`。
4. 旁证：A2 并发延时最长 `(20-1)×15 ≈ 285ms`、A5 有 100 次顺序往返，若 promise 被 await 绝无可能 4ms 返回——4ms 恰好对应「只序列化了一个 Promise 对象、未等其 resolve」。

**结论**：这次 FAIL 是 C# 报告读取侧用错了 `ExecuteScriptAsync`（对 async 函数未取回 resolve 值），**不能**据此判定 WebMessage 通道本身有问题；但仓库内也没有一次拿到有效判定的绿色运行，故通道断言的实际通过情况同样无法从材料确证。

## 修法（2026-10-26 落地，即 2026-10-23 版落地建议 #1）

- **bridge.js**：`__runSpikeTests` 保持原样；新增入口 `__runSpikeTestsRecorded`——执行全部断言后把结果 `JSON.stringify` 写入 `window.__spikeResults` 并置位 `window.__spikeDone`；执行器自身异常也落定为一条失败断言，保证 C# 轮询必然等到终态。
- **MainWindow.xaml.cs**：`ExecuteScriptAsync("window.__runSpikeTestsRecorded()")` 触发（返回值不再依赖）；以 100ms 间隔轮询 `window.__spikeDone === true`（60s 超时抛 `TimeoutException`）；随后 `ExecuteScriptAsync("window.__spikeResults")` 读回并剥掉外层 JSON 字符串编码再进 `ReportResults`。

该修法不依赖「ExecuteScriptAsync 是否 await Promise」——两种语义下均能取回有效判定。

## 运行记录二（2026-10-26，PASS，据日志原文）

```
[02:00:30.578] == WebMessage Spike 开始（docs/product-design.md §4.4）==
[02:00:30.624] WebView2 Runtime 版本: 155.0.4283.62
[02:00:31.043] 注入脚本已注册（AddScriptToExecuteOnDocumentCreatedAsync）
[02:00:31.052] 导航: https://apage-spike.local/testpage.html
[02:00:31.106] 导航完成: IsSuccess=True WebErrorStatus=Unknown
[02:00:32.023] 断言执行完毕并读回，耗时 413 ms
[02:00:32.034] [PASS] A1 顺序往返：请求/响应 id 正确关联
[02:00:32.042] [PASS] A2 并发 20 请求：乱序响应全部正确关联 | allMatched=true outOfOrder=true resolutionOrder=19,18,17,16,15,14,13,12,11,10,9,8,7,6,5,4,3,2,1,0
[02:00:32.065] [PASS] A3 注入脚本 document-start 即可用 | available=true readyState@call=loading result={"reply":"pong"} err=null
[02:00:32.070] [PASS] A4 错误路径：C# 异常→JS reject 且通道可复用 | rejected=true msg=spike-boom: 故意抛出的后端异常（错误路径测试）
[02:00:32.077] [PASS] A5 延迟实测：100 次 ping 全部成功 | {"count":100,"avgMs":0.13,"minMs":0,"p50Ms":0.1,"p95Ms":0.2,"maxMs":3.3}
[02:00:32.082] [PASS] A6 pending Map 无泄漏 | pending=0
[02:00:32.086] 延迟实测: count=100 avg=0.13ms min=0ms p50=0.1ms p95=0.2ms max=3.3ms
[02:00:32.087] == SPIKE 结果: PASS (exit 0) ==
```

413ms 的总耗时与断言设计量级吻合（A2 尾延迟 ≈285ms + A5 一百次往返 + A3 轮询等待），证明这次真正执行了全部断言——对照运行一的 4ms 即知差异所在。

## 三个 Phase 0 问题的结论（2026-10-26 补跑后确证）

| 问题 | 结论 | 实测依据 |
|---|---|---|
| ① 请求/响应 id 关联 | **确证通过**。顺序 5/5、并发 20/20 正确关联；`resolutionOrder` 严格逆序（19→0），证明「先发者延迟最长 → 响应逆序到达」的乱序设计生效且无一错配 | 运行二 A1/A2 |
| ② 异步非阻塞 | **确证通过并量化**。100 次顺序往返 avg=0.13ms、p50=0.10ms、p95=0.20ms、max=3.30ms；全程 UI 不卡（断言在页面内 await，渲染无阻塞形态） | 运行二 A5 |
| ③ 注入脚本场景覆盖 | **确证通过**。document-start（`readyState=loading`，DOM 未就绪）即可调用 `callBackend('ping')` 并收到 pong——`AddScriptToExecuteOnDocumentCreatedAsync` 注入桥在页面脚本之前可用 | 运行二 A3 |

错误路径（A4）亦通过：C# 异常 → JS reject（错误信息含 `spike-boom`），随后 `ping` 正常，通道可复用；A6 确认 `pending` Map 零残留。

## 落地建议（2026-10-26 更新）

1. ~~先修脚手架再重跑~~ **已完成**：修法即上文「修法」节，重跑 PASS（exit 0），Phase 0 该项已具备勾选依据。
2. 建议重点记录的两个数已有实测值：**A2 并发乱序 20/20 正确**、**A5 p95=0.2ms（max 3.3ms）**——GM_* 高频调用体感无忧；decision #14 的注入时内联 KV 仍保留（其价值在于消除 document-start 同步语义窗口，而非往返延迟本身）。
3. `boom` 错误路径（A4）通过后，`ScriptBridgeService` 的异常回 `{id,ok:false,error}` 约定即可照搬本 spike 的 `OnWebMessageReceived` 形态（MainWindow.xaml.cs:126-144）。
4. 复用时注意两处已在源码中处理的坑：`JsonElement` 跨 `await` 必须 `Clone()`（MainWindow.xaml.cs 注释）；`PostWebMessageAsJson` 须回 UI 线程（MainWindow.xaml.cs `PostJson`）。
