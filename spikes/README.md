# Apage 独立验证项目

本目录保存不进入 `Apage.sln` 的 spike / probe 项目，用于验证 WebView2、单文件打包、规则更新和用户脚本运行时等边界行为。每个项目都独立构建，不参与生产 EXE 打包。

## 项目清单

| 项目 | 目标 | 主要证据 |
|---|---|---|
| `AdBlockSpike` | ABP 规则索引与匹配性能 | 本地探针输出 |
| `CookieProbe` | WebView2 请求管线删除 Cookie 的实际效果 | 本机探针结论：请求事件删除后网络栈仍会重新附加 Cookie |
| `ExtensionSpike` | MV3 扩展能力边界 | 本地运行日志 |
| `FaviconProbe` | 网页自带 favicon 与引擎图标的本地缓存 | 图标字节与 data URL 验证 |
| `RuleUpdateProbe` | 手动规则更新的真实 HTTPS 下载、校验、回滚 | 更新结果与用户规则保留验证 |
| `SingleFileSpike` | Costura + 原生库自解压的单文件可行性 | `spikes/results/single-file.md` 等本地证据 |
| `WebMessageSpike` | WebMessage 异步桥与 document-start 注入时序 | 真实 WebView2 断言 |
| `UserScriptInjectionSpike` | Phase 7a：GM 存储内联注入、权限门控、令牌、响应头 CSP、快照刷新 | `artifacts/user-script-injection-results.json` |

## UserScriptInjectionSpike

构建：

```powershell
dotnet build spikes\UserScriptInjectionSpike\UserScriptInjectionSpike.csproj -c Release
```

运行：

- 该 spike 是 WPF/WebView2 GUI 程序，需在桌面会话中启动；它只访问离线虚拟主机，不请求外部网站。
- 默认结果写入 `bin\Release\net8.0-windows\user-script-injection-results.json`。
- 正式证据复制到 `artifacts\user-script-injection-results.json`；当前基线为 34/34 断言通过、两代导航、CSP 响应 handler 命中 2 次。

覆盖范围：document-start 同步读取内联 KV、写后立即读、脚本隔离、特殊键、显式 `null`、未授权调用拒绝、JSON 不兼容值拒绝、响应头 CSP（无 `unsafe-eval`）、页面序列化钩子无法嗅探 `bridgeToken`、消息 Source 门控、令牌轮换、移除旧注册后刷新快照。

边界：该实现提供 GM 存储语义与脚本命名空间隔离，**不是安全沙箱**；生产接入仍需 Source/frame 校验、SQLite 持久化、写入 ACK/重试及管理 UI。

## 证据与清理约定

- `artifacts/` 为本地发布证据目录，不进入公开仓库。
- 必须保留：`artifacts/portable/`、`portable.metrics.json`、`portable.sha256`、`release-waiver-startup.json`、`legal/`、`release-candidates/`、`fault-matrix/`、`ui-walkthrough-final4/`、`user-script-injection-results.json`、`release-startup/metrics.json`、`signature-pre-sign.json`。
- 旧 UI 走查、旧基准、临时诊断副本可清理；不得清理 `.workbuddy/` 或上述发布证据。
- `bin/`、`obj/`、`FodyWeavers.xsd` 和 `spike-run.log` 均为可重建或调试产物，不入库。
