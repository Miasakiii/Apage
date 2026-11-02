# Apage 品牌图标资产

> 创建：2026-11-02 | 品牌色：#3B82F6（冷静蓝）

## 品牌标志概念

**"A + 标签页"**：圆角方形蓝底（#3B82F6），白色几何字母 A，A 顶部悬浮一个白色圆角胶囊——既是 A 的视觉重心，也隐喻浏览器标签页（tab）。整体传达"一个页面，干净打开"。

- 风格：扁平、无渐变、无阴影、几何精确
- 圆角：约 22% 宽度（Windows 11 Fluent 风格）
- 最小可识别尺寸：16×16（蓝色方块 + 白色 A 轮廓）

## 文件清单

### 应用图标（已接入项目）

| 文件 | 位置 | 用途 |
|------|------|------|
| `apage.ico` | `Apage.Portable/Assets/apage.ico` | EXE 图标（嵌入 16/24/32/48/64/128/256 全尺寸） |
| `apage-256.png` | `Apage.Portable/Assets/apage-256.png` | WPF 资源（窗口图标、关于页等） |

csproj 已设置 `<ApplicationIcon>Assets\apage.ico</ApplicationIcon>`。

### 源文件与导出

| 文件 | 用途 |
|------|------|
| `src/logo-master.png` | AI 生成的 2048×2048 母版 |
| `apage-16.png` ~ `apage-256.png` | 各尺寸 PNG（透明背景） |
| `apage-512.png` | 商店/预览用大图 |
| `build_icons.py` | 重新导出脚本（修改母版后运行） |

### UI 图标（已接入项目）

全部线性图标（24×24 viewBox，stroke 风格，线宽 1.9，圆角线帽），定义在
`Apage.Portable/Themes/Icons.xaml`，键名 `Apage.Geo.*`。

**原有 17 个**：Back, Forward, Reload, Search, Lock, Shield, Download, Script,
Star, History, Menu, Plus, Close, WinMin, WinMax, WinRestore

**新增 25 个**：

| 键名 | 用途 |
|------|------|
| `Apage.Geo.Settings` | 设置齿轮 |
| `Apage.Geo.Wallpaper` | 壁纸/图片选择 |
| `Apage.Geo.Fullscreen` | 全屏 |
| `Apage.Geo.Volume` | 音量（喇叭+声波） |
| `Apage.Geo.Mute` | 静音（喇叭+X） |
| `Apage.Geo.Pin` | 固定标签 |
| `Apage.Geo.Copy` | 复制链接 |
| `Apage.Geo.Trash` | 删除/清空 |
| `Apage.Geo.Edit` | 编辑 |
| `Apage.Geo.Info` | 关于/信息 |
| `Apage.Geo.Home` | 主页 |
| `Apage.Geo.Puzzle` | 扩展/脚本 |
| `Apage.Geo.ChevronDown` | 下拉箭头 |
| `Apage.Geo.ChevronRight` | 右箭头 |
| `Apage.Geo.ExternalLink` | 外部链接 |
| `Apage.Geo.MoreVertical` | 竖排三点菜单 |
| `Apage.Geo.ShieldCheck` | 隐私盾+勾选（拦截态） |
| `Apage.Geo.Warning` | 警告三角 |
| `Apage.Geo.Cookie` | Cookie |
| `Apage.Geo.Globe` | 网络/地球 |
| `Apage.Geo.ArrowUp` | NTP 搜索提交按钮 |
| `Apage.Geo.Folder` | 书签文件夹 |
| `Apage.Geo.NewTab` | 新标签页 |
| `Apage.Geo.Eye` | 显示密码 |
| `Apage.Geo.EyeOff` | 隐藏密码 |
| `Apage.Geo.ProgressRing` | 下载/加载进度环 |

用法：
```xml
<Path Style="{StaticResource Apage.Style.Icon}"
      Data="{StaticResource Apage.Geo.Settings}" />
```

## 重新生成

修改 `src/logo-master.png` 后运行：
```powershell
python resources\icons\build_icons.py
```
然后把 `apage.ico` 和 `apage-256.png` 复制到 `Apage.Portable/Assets/`。
