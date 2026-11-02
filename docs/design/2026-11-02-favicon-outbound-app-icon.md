# favicon 出站路径与品牌图标接入实施记录（2026-11-02）

> 日期：2026-11-02 ｜ 状态：已落地，验证通过 ｜ 关联：[favicon 真机探针](../spikes/results/favicon-cache.md)、[v0.1 发布收敛计划](2026-10-28-v01-release-convergence.md)

## 一、背景与决策

v0.1 收敛期发现新标签页快捷方式无图标（回落品牌色字标），引擎徽标也为纯色块。两条可选路径：第三方图标服务（违反零出站红线）或本地化方案。决策：

1. **站点图标 = 浏览衍生，零额外请求**：WebView2 加载页面时本来就会取回 favicon，宿主用 `GetFaviconAsync` 把同一份字节落盘到 `{数据目录}\favicons`，新标签页从本地读。**不请求 favicon.ico、不调用第三方图标服务、不做远程代理**。
2. **引擎图标 = 受控一次性出站（第三条出站路径）**：四个搜索引擎（百度/必应/DuckDuckGo/Google）主机白名单各抓一次 `favicon.ico`，仅本地无缓存时触发，`AppSettings.FetchEngineIcons` 默认开、设置可关。这是 `FetchEngineIcons` 成为唯一默认开启出站开关的原因，AGENTS.md 红线 2 已同步。
3. **红线白名单精确豁免**：`build\check-redlines.ps1` 对 `Apage.Portable\Services\FaviconFetcher.cs` 做文件级豁免（与 `RuleUpdateClient.cs` 并列），仍为文件级而非模式级。
4. **品牌图标资产**：新增 `resources/icons/` 源目录（母版 + build_icons.py + 各尺寸导出），产物 `apage.ico`（全尺寸，csproj `<ApplicationIcon>`）与 `apage-256.png`（WPF 资源）接入 `Apage.Portable/Assets/`；`Themes/Icons.xaml` 补充同 24 viewBox stroke 风格线性图标。

## 二、实现要点

| 组件 | 职责 |
|---|---|
| `Apage.Core/Services/FaviconStore.cs` | 主机名归一化（小写/剥数字端口/去尾点/字符白名单/≤255）、SHA-1(host) 文件名、256 条上限按最后写入裁剪、IO 失败静默回落、data URL 包装 |
| `Apage.Core/Services/SearchEngineHosts.cs` | 四引擎主机白名单（含别名共 9 主机）、子域与仿冒拒绝（`www.bing.com.evil.com`）、引擎 id → 首选主机 |
| `Apage.Portable/Services/FaviconFetcher.cs` | 唯一出站出口：仅 HTTPS、不跟随重定向、6s 超时、≤64KB、图片魔数校验、重编码 32px PNG、每主机每会话最多一次 |
| `BrowserTabView` | `FaviconChanged` + `GetFaviconAsync` 落盘；ntp 桥 `getFavicons`/`fetchEngineIcon` 带 Source 门控 |
| `AppSettings.FetchEngineIcons` | 默认 true；关闭后 `fetchEngineIcon` 直接回空不发请求 |

失败路径口径（与 README 一致）：抓取失败仅在**本次会话内**不重试（进程级 HashSet），不落盘；重启后会再次尝试一次。

## 三、验证证据（2026-11-02，本机）

| 门禁 | 结果 |
|---|---|
| `dotnet test Apage.Core.Tests` | **350 通过 / 0 失败**（新增 FaviconStoreTests 19 例、SearchEngineHostsTests、AppSettings 默认值用例） |
| `build\build.ps1`（Release） | BUILD SUCCEEDED |
| `build\smoke.ps1` | SMOKE PASSED（五信号 + `getFavicons` 协议探针 + 非白名单主机 `fetchEngineIcon` 静默丢弃哨兵） |
| `spikes/FaviconProbe` 真机 | github.com favicon 755B 取到、落盘读回一致、data URL 1030 字符（详见探针文档） |
| 正式产物真机 | 启动 8 秒内 `favicons\www.bing.com` 自动出现 32×32 PNG（1101B），全程无需点击 |

## 四、已知边界

- 未访问过的用户站点没有图标（设计结果，不联网补图）；16×16 源放大到 28px 略糊；
- 站点换图标后需再次访问该站才刷新（覆盖写）；
- WPF 端到端（访问站点 → 回新标签页看到图标）为人工复验项，未自动化。

## 五、体积记账

新增资源：`apage.ico` + `apage-256.png` + `resources/icons/` 源（母版 2048² PNG 与 512 预览图只在源目录，不进单文件）；`Themes/Icons.xaml` 补充图标为 StreamGeometry 文本。出站抓取逻辑为代码不增体积。

**2026-11-02 超支与优化（同一日追加）**：图标资产以未优化形态随 csproj `Assets\**` Resource + `ApplicationIcon` 嵌入后，单文件达 4,365,824 字节，超过 4.25MB 开发目标（5MB 硬顶仍过）。处理（收敛计划 §8「再去优化资源格式」）：`resources/icons/optimize_icons.py` 从同一母版重生成——PNG 量化 256 色 + optimize（256px 61,260→8,791 字节）、ICO 只嵌 16/32/48/256 四档（93,679→13,698 字节），便携资产同步替换。重建后单文件 **4,152,832 字节**，回到开发目标内，共省约 213KB。
