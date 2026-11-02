# Cookie 头删除语义真机验证（2026-11-02）

- 探针：`spikes/CookieProbe`（net8.0-windows + WebView2 1.0.4078.44）
- 运行时：WebView2 Runtime `156.0.4314.58`
- 输出：`artifacts/cookie-probe/result.json`（忽略目录）
- 关联：[P0/P1 硬化记录](../../design/2026-11-02-p0-p1-hardening.md)

## 目标

验证在 `CoreWebView2.WebResourceRequested` 中执行：

```csharp
if (!string.IsNullOrEmpty(e.Request.Headers.GetHeader("Cookie")))
    e.Request.Headers.RemoveHeader("Cookie");
```

是否能让服务器**实际收不到** Cookie。

## 方法

1. 本机 `TcpListener` 起 HTTP 服务（端口随机），页面与图片同源 `127.0.0.1`。
2. 页面响应设置 `sid=probe-cookie-value; Path=/; SameSite=Lax`，HTML 内嵌图片请求。
3. baseline：不修改请求头，确认服务器收到 Cookie。
4. removal：在事件中读取并删除 Cookie，再确认服务器是否收到。
5. 同源设计排除第三方 Cookie 策略与 SameSite 干扰；它验证的是“事件级头部修改能否阻止网络栈发送 Cookie”这一必要前提。

## 结果

| 轮次 | 事件内读到 | 删除次数 | 服务器收到 |
|---|---|---:|---|
| baseline | `sid=probe-cookie-value` | 0 | 是 |
| removal | `sid=probe-cookie-value` | 1 | **是** |

```json
{
  "baseline": {
    "eventCookie": "sid=probe-cookie-value",
    "serverCookie": "sid=probe-cookie-value"
  },
  "removal": {
    "observedEventCookie": "sid=probe-cookie-value",
    "removedHeaderCount": 1,
    "serverCookie": "sid=probe-cookie-value"
  },
  "verdict": "ineffective-server-still-received-cookie"
}
```

## 结论

- 删除操作确实执行了，但 WebView2 网络栈在事件之后重新附加 Cookie；服务器仍收到。
- 官方文档同样说明：`WebResourceRequested` 修改头之后，网络栈还可以添加 Cookie / Authorization 等头。
  <https://learn.microsoft.com/en-us/microsoft-edge/webview2/how-to/webresourcerequested>
- 因此该实现不能作为“第三方 Cookie 剥离”或“追踪拦截”的能力依据。Apage 已下架该能力与对应统计/宣传，不保留假防护。

## 复现

```powershell
dotnet build spikes/CookieProbe/CookieProbe.csproj -c Release
spikes/CookieProbe/bin/Release/net8.0-windows/CookieProbe.exe --output artifacts/cookie-probe/result.json
```

退出码 0 表示探针完成且 baseline 已收到 Cookie；结论读取输出 JSON 的 `verdict`。
