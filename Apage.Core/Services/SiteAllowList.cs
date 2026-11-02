#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Apage.Core.Services;

/// <summary>
/// 便携站点允许清单（R7，Brave BraveShieldsDisabledForUrls 组策略的便携等价物）。
/// <para>
/// 用途：企内网、离线机、被反拦截墙阻断的环境里，随分发明文配置一批「关闭防护」的站点，
/// 比让用户逐站点点开关实用。语义与 <see cref="SiteShieldStore"/> 的站点覆盖相同：
/// 命中清单的站点跳过拦截与计数（DNT/GPC 声明性头仍发送）。
/// </para>
/// <para>
/// 文件格式（{数据目录}\allowlist.txt，UTF-8，用户可见可编辑的明文）：
/// <code>
///   # 注释行
///   example.com            # 精确主机
///   *.example.com         # 通配子域
///   .example.com          # 等价于 *.example.com 的点前缀形式
/// </code>
/// </para>
/// <para>
/// 诚实口径：这是用户可审计的明文配置，不是隐藏后门；清单内容不得进入日志导出。
/// 文件缺失 = 空清单（全部站点走全局默认）；损坏行跳过不抛——清单是便利设施，
/// 任何 IO/解析问题都不得阻断浏览（对齐 SettingsService 容错范式）。
/// </para>
/// </summary>
public sealed class SiteAllowList
{
    private readonly string _filePath;
    private List<string> _patterns = new();       // 精确主机（小写）
    private List<string> _suffixes = new();       // 通配后缀（形如 ".example.com"）
    private bool _loaded;

    /// <param name="filePath">allowlist.txt 完整路径（通常 {数据目录}\allowlist.txt）。</param>
    public SiteAllowList(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("文件路径不能为空。", nameof(filePath));
        _filePath = filePath;
    }

    /// <summary>文件路径（测试与诊断用）。</summary>
    public string FilePath => _filePath;

    /// <summary>清单是否非空（设置页/诊断展示用）。</summary>
    public bool HasEntries
    {
        get { lock (_gate) { return _patterns.Count + _suffixes.Count > 0; } }
    }

    private readonly object _gate = new();

    /// <summary>
    /// 加载清单（幂等）。文件缺失/不可读/损坏均回落空清单，不抛异常。
    /// </summary>
    public void Load()
    {
        lock (_gate)
        {
            if (_loaded) return;
            _patterns = new List<string>();
            _suffixes = new List<string>();
            try
            {
                if (File.Exists(_filePath))
                {
                    foreach (var raw in File.ReadAllLines(_filePath))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                            continue;
                        // 行内注释：# 之后的内容忽略（主机名不含 #）
                        var hash = line.IndexOf('#');
                        if (hash >= 0) line = line.Substring(0, hash).Trim();
                        if (line.Length == 0) continue;

                        var host = NormalizePattern(line);
                        if (host == null) continue; // 非法行跳过
                        if (host.StartsWith("*.", StringComparison.Ordinal))
                            _suffixes.Add(host.Substring(1)); // "*.example.com" → ".example.com"
                        else if (host.StartsWith(".", StringComparison.Ordinal))
                            _suffixes.Add(host);
                        else
                            _patterns.Add(host);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // 读取失败：保持空清单，浏览不受影响
                _patterns = new List<string>();
                _suffixes = new List<string>();
            }
            _loaded = true;
        }
    }

    /// <summary>
    /// 主机是否命中「关闭防护」清单。未加载时先惰性加载；非法主机返回 false。
    /// </summary>
    public bool IsShieldsDisabled(string? host)
    {
        var key = NormalizeHost(host);
        if (key == null) return false;
        Load();
        lock (_gate)
        {
            if (_patterns.Contains(key)) return true;
            foreach (var suffix in _suffixes)
            {
                if (key.EndsWith(suffix, StringComparison.Ordinal) && key.Length > suffix.Length)
                    return true;
            }
            return false;
        }
    }

    /// <summary>快照（设置页/诊断展示）：精确主机 + 通配模式，按序排序。</summary>
    public IReadOnlyList<string> Snapshot()
    {
        Load();
        lock (_gate)
        {
            return _patterns
                .Concat(_suffixes.Select(s => "*" + s))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
        }
    }

    private static string? NormalizePattern(string pattern)
    {
        var p = pattern.Trim().ToLowerInvariant();
        if (p.Length == 0 || p.Length > 255) return null;
        foreach (var c in p)
        {
            if (!(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' || c == '*'))
                return null;
        }
        // 仅接受 "*." 前缀或点前缀或裸主机；拒绝 "*example.com"（语义模糊）
        if (p.Contains('*') && !p.StartsWith("*.", StringComparison.Ordinal))
            return null;
        return p;
    }

    private static string? NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        var h = host.Trim().ToLowerInvariant();
        var colon = h.IndexOf(':');
        if (colon > 0)
        {
            var tail = h.Substring(colon + 1);
            var allDigits = tail.Length > 0;
            foreach (var c in tail)
            {
                if (!char.IsDigit(c)) { allDigits = false; break; }
            }
            if (allDigits) h = h.Substring(0, colon);
        }
        while (h.EndsWith(".", StringComparison.Ordinal)) h = h.Substring(0, h.Length - 1);
        if (h.Length == 0 || h.Length > 255) return null;
        foreach (var c in h)
        {
            if (!(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_'))
                return null;
        }
        return h;
    }
}
