using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Apage.Core.Services;
using Xunit;

namespace Apage.Core.Tests;

/// <summary>
/// 本地诊断日志（C1）：滚动、手动导出、URL/Cookie 脱敏、异常消息不入库、IO 故障不抛。
/// </summary>
public sealed class LocalLogServiceTests : IDisposable
{
    private readonly string _dir;

    public LocalLogServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ApageTests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* 清理失败不影响结论 */ }
    }

    [Fact]
    public void Write_RedactsUrlsAndSensitiveHeaders()
    {
        var log = new LocalLogService(_dir);
        var written = log.Write(
            LocalLogLevel.Warning,
            "navigation",
            "failed https://example.com/private?q=secret Cookie: sid=abc123; Authorization: Bearer token");

        Assert.True(written);
        var text = File.ReadAllText(log.CurrentLogPath);
        Assert.DoesNotContain("example.com", text);
        Assert.DoesNotContain("sid=abc123", text);
        Assert.DoesNotContain("Bearer token", text);
        Assert.Contains("[redacted-url]", text);
        Assert.Contains("[redacted]", text);
    }

    [Fact]
    public void WriteException_StoresTypeAndStack_ButNotMessage()
    {
        var log = new LocalLogService(_dir);
        Exception captured;
        try
        {
            throw new InvalidOperationException("https://private.example/path?q=search-term");
        }
        catch (Exception ex)
        {
            captured = ex;
        }

        Assert.True(log.WriteException("startup", captured));
        var text = File.ReadAllText(log.CurrentLogPath);
        Assert.Contains("System.InvalidOperationException", text);
        Assert.DoesNotContain("private.example", text);
        Assert.DoesNotContain("search-term", text);
    }

    [Fact]
    public void Write_RollsFiles_AndKeepsConfiguredMaximum()
    {
        var log = new LocalLogService(_dir, maxFileBytes: 1024, maxFiles: 3);
        var payload = new string('x', 420);

        for (var i = 0; i < 14; i++)
            Assert.True(log.Write(LocalLogLevel.Info, "rotation", payload));

        var files = Directory.GetFiles(_dir, "apage*.log");
        Assert.InRange(files.Length, 2, 3);
        Assert.Contains(files, p => Path.GetFileName(p) == "apage.log");
        Assert.Contains(files, p => Path.GetFileName(p) == "apage.1.log");
        Assert.DoesNotContain(files, p => Path.GetFileName(p) == "apage.3.log");
    }

    [Fact]
    public async Task Export_ConcatenatesRolledLogs_CurrentFirst()
    {
        var log = new LocalLogService(_dir, maxFileBytes: 1024, maxFiles: 3);
        log.Write(LocalLogLevel.Info, "one", new string('a', 700));
        log.Write(LocalLogLevel.Info, "two", new string('b', 700));
        var destination = Path.Combine(Path.GetTempPath(), "ApageTests", Guid.NewGuid().ToString("N") + ".txt");

        try
        {
            var exported = await log.TryExportAsync(destination);

            Assert.True(exported);
            var text = await File.ReadAllTextAsync(destination);
            Assert.Contains("===== apage.log =====", text);
            Assert.Contains("===== apage.1.log =====", text);
        }
        finally
        {
            try { if (File.Exists(destination)) File.Delete(destination); }
            catch { /* 清理失败不影响结论 */ }
        }
    }

    [Fact]
    public void Write_IoFailure_ReturnsFalse_DoesNotThrow()
    {
        // 用一个普通文件占用“日志目录”路径，迫使目录创建失败。
        var parent = Path.Combine(Path.GetTempPath(), "ApageTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        var occupied = Path.Combine(parent, "not-a-directory");
        File.WriteAllText(occupied, "x");
        var log = new LocalLogService(occupied);

        var written = log.Write(LocalLogLevel.Error, "io", "should not throw");

        Assert.False(written);
        try { Directory.Delete(parent, recursive: true); } catch { }
    }

    [Fact]
    public void Sanitize_LeavesSafeDiagnosticTextUntouched()
    {
        Assert.Equal("settings-load-failed", LocalLogService.Sanitize("settings-load-failed"));
        Assert.Equal(string.Empty, LocalLogService.Sanitize(null));
    }

    [Fact]
    public async Task ConcurrentWrites_RemainWellFormed()
    {
        var log = new LocalLogService(_dir);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() =>
            Assert.True(log.Write(LocalLogLevel.Info, "thread", "entry-" + i)))));

        var lines = File.ReadAllLines(log.CurrentLogPath, Encoding.UTF8);
        Assert.Equal(20, lines.Length);
        Assert.All(lines, line => Assert.Contains("\tInfo\tthread\t", line));
    }
}
