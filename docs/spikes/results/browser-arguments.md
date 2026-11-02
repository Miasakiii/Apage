# 内核命令行参数收口真机验证（2026-11-02）

> 目标：确认 `Apage.Core/Services/BrowserArguments.cs` 拼装的 stock 开关经 WebView2 `AdditionalBrowserArguments`
> **真实生效**（Chromium 对无法识别的开关静默忽略，"参数写上了"不等于"加固了"）。
> 方法：启动 Release 产物 8 秒后抓取 `msedgewebview2.exe` 全进程命令行快照。

## 结论

**PROBE PASSED**。Apage 宿主进程（`--webview-exe-name=Apage.Portable.exe --webview-exe-version=0.1.0-dev`，
user-data-dir 指向 `%TEMP%\Apage\cache\WebView2\EBWebView`，Runtime `156.0.4314.58`）命令行包含全部收口参数：

```
--accept-lang=zh-CN
--disable-background-networking
--disable-client-side-phishing-detection
--disable-component-update
--disable-domain-reliability
--disable-features=msWebOOUI,msPdfOOUI,msSmartScreenProtection,OptimizationGuideModelDownloading,MediaRouter
--disable-sync
--force-webrtc-ip-handling-policy=disable_non_proxied_udp
--no-default-browser-check
--no-pings
```

- WebRTC 开关随 `AppSettings.WebRTCIPProtection`（默认 true）出现，与收口逻辑一致；
- 子进程（gpu/utility/renderer）继承同一 `--disable-features` 集合（Chromium 内部按字母序重排，集合一致）；
- 快照同时证实**禁止传入清单**未出现：ungoogled 私有 flags、Bromite canvas 噪声 flags 均不在任何进程命令行中。

## 边界与复核要求

- 开关名随 Edge Runtime 大版本漂移（`msSmartScreenProtection` 等为版本相关名）；Runtime 大版本更新后需重跑本探针复核。
- 本探针为一次性人工验证（脚本未入库）；CI 侧由 `BrowserArgumentsTests` 静态把守内容，运行时生效性以本证据为准。
- 外部注入 `--edge-webview-switches` 会覆盖本配置（微软文档优先级规则），启动时检测到即写本地日志告警（`App.xaml.cs` → `BrowserArguments.HasExternalOverride`）。

## 复现方式

```powershell
# 1. 构建 Release 产物
build\build.ps1
# 2. 启动产物，8 秒后抓取 WebView2 进程命令行（探针脚本为一次性，未入库）
$p = Start-Process Apage.Portable\bin\Release\Apage.Portable.exe -PassThru
Start-Sleep -Seconds 8
Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" |
  Where-Object { $_.CommandLine -like '*Apage.Portable.exe*' } |
  Select-Object -ExpandProperty CommandLine
$p | Stop-Process -Force
```
