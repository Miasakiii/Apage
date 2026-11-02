#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Apage.Core.Services;

public enum LocalLogLevel
{
    Info,
    Warning,
    Error
}

/// <summary>
/// 本地滚动诊断日志。产品红线：不记录 URL、搜索词、Cookie、网页内容，也不上传。
/// 调用方只应传入来源/错误类型/阶段标记；<see cref="Write"/> 仍会二次脱敏 URL 与敏感头。
/// 任何文件 IO 失败都只返回 false，绝不把日志故障升级为应用故障。
/// </summary>
public sealed class LocalLogService
{
    private const string CurrentFileName = "apage.log";
    private static readonly TimeSpan RedactionTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly Regex UrlPattern = new Regex(
        @"\b(?:https?|file)://[^\s""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RedactionTimeout);

    private static readonly Regex SensitiveHeaderPattern = new Regex(
        @"(?im)\b(cookie|set-cookie|authorization|proxy-authorization)\s*:\s*[^\r\n]*",
        RegexOptions.CultureInvariant,
        RedactionTimeout);

    private readonly object _gate = new object();
    private readonly string _directory;
    private readonly long _maxFileBytes;
    private readonly int _maxFiles;

    public LocalLogService(string directory, long maxFileBytes = 1_000_000, int maxFiles = 3)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("日志目录不能为空。", nameof(directory));
        if (maxFileBytes < 1024)
            throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
        if (maxFiles < 2 || maxFiles > 20)
            throw new ArgumentOutOfRangeException(nameof(maxFiles));

        _directory = Path.GetFullPath(directory);
        _maxFileBytes = maxFileBytes;
        _maxFiles = maxFiles;
    }

    public string DirectoryPath => _directory;

    public string CurrentLogPath => Path.Combine(_directory, CurrentFileName);

    /// <summary>写入一条已脱敏的结构化日志；失败返回 false，不抛异常。</summary>
    public bool Write(LocalLogLevel level, string source, string message)
    {
        if (string.IsNullOrWhiteSpace(source))
            source = "app";
        var safeSource = Sanitize(source);
        var safeMessage = Sanitize(message ?? string.Empty);
        var line = string.Format(
            CultureInfo.InvariantCulture,
            "{0:O}\t{1}\t{2}\t{3}{4}",
            DateTimeOffset.Now,
            level,
            safeSource,
            safeMessage,
            Environment.NewLine);

        return TryAppend(line);
    }

    /// <summary>
    /// 记录异常类型、HResult 与堆栈；故意不写 Exception.Message，
    /// 因为消息可能包含当前 URL、搜索词或站点返回内容。堆栈仍会二次脱敏。
    /// </summary>
    public bool WriteException(string source, Exception exception)
    {
        if (exception == null)
            return false;

        var details = new StringBuilder();
        details.Append("exception=").Append(exception.GetType().FullName);
        details.Append("; hresult=").Append(exception.HResult.ToString("X8", CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(exception.StackTrace))
            details.AppendLine().Append(exception.StackTrace);

        return Write(LocalLogLevel.Error, source, details.ToString());
    }

    /// <summary>
    /// 手动导出所有轮转日志到用户选择的目标文件（当前日志在前）。
    /// 无日志或 IO 失败返回 false；导出路径由用户显式选择，不自动上传。
    /// </summary>
    public Task<bool> TryExportAsync(string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
            return Task.FromResult(false);

        return Task.Run(() =>
        {
            string content;
            string fullDestination;
            try
            {
                fullDestination = Path.GetFullPath(destinationPath);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException ||
                                       ex is PathTooLongException)
            {
                return false;
            }

            lock (_gate)
            {
                try
                {
                    if (!System.IO.Directory.Exists(_directory))
                        return false;

                    var files = System.IO.Directory.GetFiles(_directory, "apage*.log")
                        .OrderByDescending(GetLogOrder)
                        .ToList();
                    if (files.Count == 0)
                        return false;

                    // 目标若就是任一源日志，截断导出会反过来吃掉诊断数据；直接拒绝。
                    if (files.Any(file => string.Equals(
                            Path.GetFullPath(file), fullDestination, StringComparison.OrdinalIgnoreCase)))
                    {
                        return false;
                    }

                    var builder = new StringBuilder();
                    foreach (var file in files)
                    {
                        builder.AppendLine("===== " + Path.GetFileName(file) + " =====");
                        builder.AppendLine(File.ReadAllText(file));
                    }
                    content = builder.ToString();
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                           ex is ArgumentException || ex is NotSupportedException)
                {
                    return false;
                }
            }

            string? tempPath = null;
            try
            {
                var targetDirectory = Path.GetDirectoryName(fullDestination);
                if (!string.IsNullOrEmpty(targetDirectory))
                    System.IO.Directory.CreateDirectory(targetDirectory);

                tempPath = fullDestination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(tempPath, content, new UTF8Encoding(false));

                if (File.Exists(fullDestination))
                    File.Replace(tempPath, fullDestination, null, ignoreMetadataErrors: true);
                else
                    File.Move(tempPath, fullDestination);

                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is ArgumentException || ex is NotSupportedException ||
                                       ex is PlatformNotSupportedException)
            {
                // 目标已存在且当前文件系统不支持 Replace 时返回 false，绝不删除原目标。
                return false;
            }
            finally
            {
                try { if (tempPath != null && File.Exists(tempPath)) File.Delete(tempPath); }
                catch { /* 清理临时文件失败不影响结论 */ }
            }
        });
    }

    /// <summary>把可识别的高风险内容替换为占位符；无法识别的自由文本仍需调用方自律。</summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        try
        {
            var sanitized = SensitiveHeaderPattern.Replace(text!, match =>
                match.Groups[1].Value + ": [redacted]");
            return UrlPattern.Replace(sanitized, "[redacted-url]");
        }
        catch (RegexMatchTimeoutException)
        {
            // 极端输入下宁可丢弃正文，也不冒险把未脱敏内容落盘。
            return "[redaction-timeout]";
        }
    }

    private bool TryAppend(string line)
    {
        lock (_gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(_directory);
                RotateIfNeeded(Encoding.UTF8.GetByteCount(line));
                File.AppendAllText(CurrentLogPath, line, new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is ArgumentException || ex is NotSupportedException)
            {
                return false;
            }
        }
    }

    private void RotateIfNeeded(int incomingBytes)
    {
        var current = CurrentLogPath;
        var currentLength = File.Exists(current) ? new FileInfo(current).Length : 0;
        if (currentLength == 0 || currentLength + incomingBytes <= _maxFileBytes)
            return;

        var oldest = Path.Combine(_directory, $"apage.{_maxFiles - 1}.log");
        if (File.Exists(oldest))
            File.Delete(oldest);

        for (var index = _maxFiles - 2; index >= 1; index--)
        {
            var source = Path.Combine(_directory, $"apage.{index}.log");
            var destination = Path.Combine(_directory, $"apage.{index + 1}.log");
            if (File.Exists(source))
                File.Move(source, destination);
        }

        if (File.Exists(current))
            File.Move(current, Path.Combine(_directory, "apage.1.log"));
    }

    private static int GetLogOrder(string path)
    {
        var name = Path.GetFileName(path);
        if (string.Equals(name, CurrentFileName, StringComparison.OrdinalIgnoreCase))
            return int.MaxValue;
        var suffix = name.Substring("apage.".Length, name.Length - "apage.".Length - ".log".Length);
        return int.TryParse(suffix, out var index) ? index : 0;
    }
}
