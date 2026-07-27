#nullable enable
using System.Collections.Generic;

namespace Apage.Core.Models;

/// <summary>会话中的单个标签（仅存可恢复信息：地址 + 标题）。</summary>
public sealed record SessionTab
{
    /// <summary>标签地址（仅 http/https/file 会被恢复）。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>标签标题（恢复前占位显示，页面加载后由 DocumentTitle 覆盖）。</summary>
    public string Title { get; set; } = string.Empty;
}

/// <summary>
/// 浏览会话快照（docs/roadmap.md Phase 2 会话保存 / Phase 3 会话恢复）：
/// 退出时记录打开的标签，启动时恢复上次标签。
/// 隐私标签绝不写入本快照（捕获时即排除，见 TabManager.CaptureSession，roadmap 红线）。
/// </summary>
public sealed record SessionState
{
    /// <summary>已保存的标签（顺序即显示顺序）。</summary>
    public List<SessionTab> Tabs { get; set; } = new List<SessionTab>();

    /// <summary>恢复后应激活的标签下标（越界由 SessionService.Normalize 收敛到有效范围）。</summary>
    public int ActiveIndex { get; set; }
}
