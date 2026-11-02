# 参考仓库调研与优化方案实施记录（2026-11-02）

> 日期：2026-11-02 ｜ 状态：Phase 0–D 已实施完成（MorphCore 完整版 8 对与设置窗口重构除外，见 §6）｜
> 关联：[roadmap](../roadmap.md) 决策 #25–#31 与「已评估、不采纳」归档、[产品设计 §5.2](../product-design.md) 能力边界声明

## 一、背景

参考 5 个仓库（morphicons / ungoogled-chromium / brave-browser / helium / supermium）调研并规划设计优化。
核心结论：Apage 是 WebView2 外壳而非 Chromium fork，内核级能力（指纹噪音、Safe Browsing 源码级关闭、
qjz9zk 域名替换、adblock-rust）结构上全部不可得；真实差距在壳层——把「零遥测/隐私优先」从口号变成
可执行门禁与可验证证据，在不动隐私默认值红线的前提下解决误伤治理，用纯 Core 层资产补效率与质感。

调研方式：10 个智能体的工作流（5 路仓库调研 + 4 路代码/UI/构建/文档分析 + 1 路综合），
全程只读；MorphCore 可行性以 354 行隔离 spike 先行验证。

## 二、Phase 0：工作区收敛

favicon 切片（`db02fdf`）、品牌图标（`d313289`）、gitignore 收敛（`ca228ff`）三个垂直提交；
未跟踪目录分拣：`resources/icons/`（图标源）与 `spikes/FaviconProbe/`（探针）入仓，
`.stepcode/`（运行时）与 `Apage图标/`（第三方画布工具会话）入 .gitignore。

## 三、Phase A：P0 隐私证据与披露

| 提交 | 内容 | 关键证据 |
|---|---|---|
| `c4960ad` | R1 内核参数收口 `BrowserArguments`：stock 开关常驻 + 禁止清单 + 外部注入检测 | 14 项单测；真机探针证实开关出现在 msedgewebview2.exe 命令行 |
| `93bc030` | R2 `outbound-probe.ps1` 首启零非白名单出站门禁，串联 smoke/CI | 探针实测零出站；整合 smoke 端到端通过 |
| `7d5e8a9` | R3 能力边界声明 + 6 处文档漂移纠偏（统计卡下线/体积锚点/favicon 口径等） | 构建、smoke 通过 |
| `21b29a7` | R4 被拦请求返回透明空资源（BlockedResponse，Core 纯逻辑） | 21 项单测（GIF89a 魔数、Content-Length 一致性） |
| `cdaa357`+`bbde682` | R11 SVG→Path 转换脚本 + Icons.xaml 微门禁；图标资产优化 | 单文件 4,365,824→**4,152,832** 回到 4.25MB 目标内 |

期间发现并修复 R1/R3 自相矛盾：`--disable-features=msSmartScreenProtection` 实际已默认关闭
SmartScreen（`4dd5cfd` 三处口径更正）。SmartScreen 上传非名单 URL 给微软的隐私代价
与「URL 不外传」定位一致，但失去内核级钓鱼提示——能力边界表如实记录。

## 四、Phase B：误伤治理与可解释性

| 提交 | 内容 |
|---|---|
| `f1d48b9` | R5 SiteShieldStore 按站点覆盖（+设置页+绝不回写全局专测 19 项） |
| `96636ab` | R6 PageBlockCounter 页级计数 + 地址栏盾牌徽章（7 项） |
| `4395b9f` | R7 SiteAllowList 便携允许清单（10 项） |
| `65fdda0` | R9 NTP 首启引导卡（只读隐私展示 + 引擎选择） |
| `8bf495d` | quickWin：深色主题点亮（Dark.xaml 生效 + 外观设置 + 背景色跟随） |
| `05d3eee` | quickWin：Toast 对齐规范（顶部居中 + 下滑 200ms + 2.5s） |
| `3d372da` | quickWin：ListBoxItem 令牌样式 + 键盘焦点环 |
| `b255d1f` | quickWin：三个管理器空状态 |

诚实口径贯穿：引擎不区分广告/跟踪规则，页级计数只有一类；引导卡不提供隐私默认值改写入口。

## 五、Phase C：效率与质感

| 提交 | 内容 |
|---|---|
| `5341224` | R8 BangResolver 本地 bangs（curated 18 条 + 24 项单测）；边界按用户决策修正（决策 #29） |
| `f162e20` | R10 MorphCore pilot：六阶段内核 + Spring + MorphIcon + Menu↔Close 一对（22 项单测） |

MorphCore pilot 期间测试捕获一个真实缺陷：弹簧物理平衡点硬编码为 1，`Retarget(0)` 改道时
仍拉向 1——已修复为拉向 `_target`。WPF 帧成本（每帧冻结 StreamGeometry 的 GC/命中测试）
待人工验收后再定是否补齐其余 7 对（决策 #30）。

## 六、Phase D：归档

- 决策 #28（误伤治理模型）、#29（bangs 边界）、#30（MorphCore pilot 范围）、#31（信誉检查维持现状）入 roadmap 决策表；
- rejectedIdeas 14 条全部归档（指纹防护/region 变形/遥测端点名单/adblock-rust/ungoogled 内核机制/对齐 ungoogled 默认值/关闭 Runtime 诊断/重接 Cookie 剥离/P3A/maskTarget/化妆规则注入/R12 API 开关/bangs 白名单约束/MorphCore 完整版），每条附一句话理由；
- S5 用户脚本最小内核与 Phase 7d 维持原计划顺序。

## 七、验证基线与体积账本

| 项 | 值 |
|---|---|
| Core 单测 | 305 → **467 全绿**（新增 162 项） |
| 单文件 | 4,068,352（137bddb）→ **4,190,208 字节**（+121,856；bangs 表+MorphCore IL+引导卡+菜单，仍在 4.25MB 开发目标内） |
| 门禁 | 红线/许可/五信号 smoke/数据往返/布局探针/出站探针全过 |
| 启动门禁 | 本机用户负载下 warm median ~1080ms 未过 1000ms——既有 S4 参考机复验项，不放宽阈值 |

## 八、已知限制与后续

- MorphCore 仅 Menu↔Close 一对；其余 7 对待 WPF 帧成本人工验收；
- 设置窗口 760×560 重构与关于页、Omnibox 建议扩充、链接悬停浮层、文案资源化未做（v0.1.x）；
- 启动门禁参考机复验、签名与签名后复验仍属 S4；
- 按站点分轴开关（广告/跟踪分别开关）待规则来源分类后实施。
