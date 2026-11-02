#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Apage.Portable
{
    /// <summary>
    /// 可选的本地启动阶段计时（仅 --startup-metrics &lt;path&gt; 时启用）。
    /// 复用真实启动路径，在关键阶段写入 UTC 标记；不联网、不常驻、不影响正常启动。
    /// 由 build/startup-bench.ps1 读取并与进程启动时间相减，得到窗口可见与内核就绪耗时。
    /// </summary>
    internal static class StartupMetrics
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<string, DateTime> Marks = new(StringComparer.Ordinal);
        private static string? _filePath;
        private static bool _completed;

        internal static bool IsEnabled => _filePath != null;

        /// <summary>解析 --startup-metrics；未提供时不产生任何额外工作。</summary>
        internal static void Arm(string[] args)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--startup-metrics", StringComparison.OrdinalIgnoreCase))
                {
                    _filePath = Path.GetFullPath(args[i + 1]);
                    return;
                }
            }
        }

        /// <summary>记录一个阶段（同一阶段只保留首次）。</summary>
        internal static void Mark(string name)
        {
            if (_filePath == null)
                return;

            lock (Gate)
            {
                if (!Marks.ContainsKey(name))
                    Marks[name] = DateTime.UtcNow;
            }
        }

        /// <summary>写入结果文件；重复调用只保留第一次。</summary>
        internal static void Complete(string outcome, int exitCode)
        {
            if (_filePath == null)
                return;

            Dictionary<string, DateTime> snapshot;
            lock (Gate)
            {
                if (_completed)
                    return;
                _completed = true;
                Marks["Completed"] = DateTime.UtcNow;
                snapshot = new Dictionary<string, DateTime>(Marks, StringComparer.Ordinal);
            }

            try
            {
                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                var json = JsonSerializer.Serialize(new
                {
                    outcome,
                    exitCode,
                    processBitness = Environment.Is64BitProcess ? "x64" : "x86",
                    marks = snapshot
                });
                File.WriteAllText(_filePath, json);
            }
            catch
            {
                // 测量失败不影响应用正常退出，由基准脚本报告缺失。
            }
        }
    }
}
