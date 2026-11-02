#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Apage.Core.Configuration;

namespace Apage.Portable;

/// <summary>
/// 新标签页资产落盘（docs/design/2026-10-23-privacy-stats-ntp.md §4.3）。
/// csproj 通配符把 Assets\webui\* 编译为内嵌 Resource，运行时抽取到 {数据目录}\webui\，
/// 空白标签经 file:// URI 导航到 index.html——Phase 4 视觉差异化（壁纸/搜索/快捷方式/时钟/统计卡）的入口。
/// .version 标记判重：缺失或不匹配才重写，避免每次启动重复 IO；抽取失败不抛（新标签页不可用但不阻断启动）。
/// </summary>
internal static class WebUiAssets
{
    /// <summary>资产版本：改动 webui 内容时 +1，触发重新抽取。</summary>
    private const string Version = "2";

    /// <summary>参与抽取的文件清单（与 Assets\webui\ 实际文件一一对应）。</summary>
    private static readonly string[] Files = { "index.html", "app.js", "style.css", "tokens.css", "error.html" };

    private static readonly Lazy<string> IndexUriLazy = new Lazy<string>(() =>
        new Uri(Path.Combine(Folder, "index.html")).AbsoluteUri);

    /// <summary>新标签页 index.html 的完整 file:// URI：空白标签的导航目标；会话保存时排除（与 about: 同等对待）。</summary>
    public static string IndexUri => IndexUriLazy.Value;

    /// <summary>webui 落盘目录（{数据目录}\webui；data/ 已被 .gitignore 忽略，不会入库）。</summary>
    public static string Folder => Path.Combine(AppPaths.DataDirectory, "webui");

    /// <summary>确保资产已抽取且为当前版本。任何单文件失败仅记 Trace，不阻断启动。</summary>
    public static void EnsureExtracted()
    {
        try
        {
            var dir = Folder;
            Directory.CreateDirectory(dir);

            var versionFile = Path.Combine(dir, ".version");
            if (File.Exists(versionFile) && File.ReadAllText(versionFile).Trim() == Version)
                return;

            foreach (var name in Files)
            {
                var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/webui/" + name));
                if (resource == null)
                {
                    Trace.WriteLine($"[WebUiAssets] 内嵌资源缺失：{name}");
                    continue;
                }

                var target = Path.Combine(dir, name);
                using (var source = resource.Stream)
                using (var dest = File.Create(target))
                {
                    source.CopyTo(dest);
                }
            }

            File.WriteAllText(versionFile, Version);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[WebUiAssets] 抽取失败：{ex.Message}");
        }
    }
}
