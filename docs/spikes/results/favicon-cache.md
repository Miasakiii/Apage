# 站点图标（favicon）真机验证（2026-11-02）

> 目标：确认「新标签页用站点自带图标」的两条来源——① 浏览衍生（零额外请求）；② 搜索引擎图标一次性抓取（受控出站）。

## 结论

成立。WebView2 在加载页面时本来就会取回该站 favicon，`CoreWebView2.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png)`
能取到 PNG 字节；宿主把同一份字节写入 `{数据目录}\favicons`，新标签页按主机名读回并包成 data URL 显示。
**没有请求 `favicon.ico`，没有调用第三方图标服务，没有做远程代理。**

## 来源二：引擎图标一次性抓取（2026-11-02 追加）

新标签页首屏要显示四个引擎图标，用户未必访问过这些站点，因此允许对**白名单主机**各抓一次
`favicon.ico`。门控在宿主侧，页面无法指定任意主机：

| 层 | 规则 |
|---|---|
| 主机 | `SearchEngineHosts` 白名单（4 个引擎 + 别名，共 9 个主机）；子域仿冒（`www.bing.com.evil.com`）与外部主机一律拒绝 |
| 开关 | `AppSettings.FetchEngineIcons`（默认开，设置里可关）；关闭后直接回空，不发请求 |
| 缓存 | 本地已有图标就直接回，不发请求 |
| 频次 | 每主机每会话最多尝试一次，失败不重试 |
| 协议 | 仅 HTTPS、`AllowAutoRedirect=false`、6 秒超时、单次 ≤64KB、只接受图片魔数（ICO/PNG/JPEG/GIF/WEBP） |
| 归一化 | 一律重编码为 32×32 PNG 再入库，页面只处理 `data:image/png;base64,` |

出口文件唯一：`Apage.Portable/Services/FaviconFetcher.cs`（`build\check-redlines.ps1` 文件级白名单，与规则更新并列）。

真机验证：启动正式产物后 8 秒内，`{数据目录}\favicons` 自动出现 `www.bing.com` 的图标
（`275ebf4fb0c00c59e5c55bf1763c82c2baf29b07.png`，32×32 PNG，1101 B），全程无需点击。

冒烟门禁：非白名单主机的 `fetchEngineIcon` 必须被静默丢弃（哨兵 700ms 内不得收到回包）。

## 真机数据

探针：`spikes/FaviconProbe`（不加入 `Apage.sln`）

```
dotnet build spikes/FaviconProbe/FaviconProbe.csproj -c Release
spikes/FaviconProbe/bin/Release/net8.0-windows/FaviconProbe.exe <输出png路径>
```

本机结果（Windows，WebView2 Runtime `156.0.4314.58`）：

| 项 | 值 |
|---|---|
| 目标 | `https://github.com` |
| 取到字节 | 755 B |
| PNG 签名 | 通过（`89 50 4E 47`） |
| 落盘文件 | `c2208abde9668e8e9815c3690855edd1e63abeac.png`（SHA-1(host)） |
| 读回一致 | 是 |
| data URL 长度 | 1030 字符 |
| FaviconChanged 次数 | 1 |
| 结论 | **PROBE PASSED** |

## 缓存口径

- 目录：`{数据目录}\favicons`，文件名 = `SHA-1(规范化主机名).png`；
- 上限 256 条，超出按最后写入时间删最旧；
- 主机名归一化：小写、剥端口（仅当冒号后全为数字）、去尾点、只接受 `[a-z0-9._-]`；
  非法输入（含空格、路径分隔符、`a:b:c`）一律拒绝，避免写进奇怪文件名；
- 读取缺失、目录不可写、文件损坏一律静默回落——图标是装饰性数据，不能影响浏览；
- 页面侧：`getFavicons` 只上报自己当前快捷方式的主机名（上限 32），宿主只回命中项；
  未命中保持品牌色字标。

## 验证覆盖

| 层 | 方式 | 状态 |
|---|---|---|
| 引擎取图标 + 落盘 + 读回 + data URL | `spikes/FaviconProbe` 真机 | ✅ |
| 主机名归一化 / 覆盖写 / 上限裁剪 / 缺失回落 | `Apage.Core.Tests/FaviconStoreTests`（19 用例） | ✅ |
| `getFavicons` 桥协议（页面能收到 icons 对象） | `--smoke` 快捷方式探针内新增 | ✅ |
| 页面渲染真实图标（data URL → 白圆内 img） | 无头渲染 + 桩桥截图 | ✅ |
| WPF 端到端（访问站点 → 回新标签页看到图标） | 需真机点击，未自动化 | ⚠️ 人工复验 |

## 已知边界

- 未访问过的站点没有图标（设计结果，不联网补图）；
- 图标质量取决于站点提供的最大尺寸，16×16 的站点放大到 28px 会略糊；
- 站点换图标后，需再次访问该站才会刷新缓存（覆盖写）。
