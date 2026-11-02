#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Apage.Core.Services;

/// <summary>
/// 原子 JSON 存储所需的最小文件系统面。默认实现直接用 System.IO；
/// 测试可注入故障版本，验证 Replace/Copy/Move 各阶段失败时绝不先删除唯一正式副本。
/// </summary>
public interface IAtomicFileSystem
{
    bool Exists(string path);
    void CreateDirectory(string path);
    string ReadAllText(string path);
    void WriteAllText(string path, string contents);
    void Replace(string sourcePath, string destinationPath, string backupPath);
    void Move(string sourcePath, string destinationPath);
    void Copy(string sourcePath, string destinationPath, bool overwrite);
    void Delete(string path);
}

/// <summary>默认文件系统实现（生产路径）。</summary>
public sealed class DefaultAtomicFileSystem : IAtomicFileSystem
{
    public bool Exists(string path) => File.Exists(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public string ReadAllText(string path) => File.ReadAllText(path, Encoding.UTF8);
    public void WriteAllText(string path, string contents) =>
        File.WriteAllText(path, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    public void Replace(string sourcePath, string destinationPath, string backupPath) =>
        File.Replace(sourcePath, destinationPath, backupPath, ignoreMetadataErrors: true);
    public void Move(string sourcePath, string destinationPath) => File.Move(sourcePath, destinationPath);
    public void Copy(string sourcePath, string destinationPath, bool overwrite) =>
        File.Copy(sourcePath, destinationPath, overwrite);
    public void Delete(string path) => File.Delete(path);
}

/// <summary>
/// 设置 / 会话 / 隐私统计共用的小型 JSON 原子存储。
/// 写入顺序：临时文件 → Replace（带 .bak）；Replace 不可用时先复制旧文件到 .bak，
/// 再覆盖正式文件。任何失败路径都不先删除唯一正式副本。
/// 读取按 正式文件 → .bak → .tmp 顺序尝试，损坏或缺失时返回 false，由调用方回落默认值。
/// </summary>
public static class AtomicJsonStore
{
    public static Task SaveAsync(
        string filePath,
        string json,
        CancellationToken cancellationToken = default,
        IAtomicFileSystem? fileSystem = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("文件路径不能为空。", nameof(filePath));
        if (json == null)
            throw new ArgumentNullException(nameof(json));

        var fs = fileSystem ?? new DefaultAtomicFileSystem();
        return Task.Run(() => SaveCore(filePath, json, fs, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// 读取第一个可反序列化的候选文件：正式文件、.bak、.tmp。
    /// 不修改磁盘；调用方下一次成功保存会把正式文件修复为当前状态。
    /// </summary>
    public static bool TryReadJson<T>(
        string filePath,
        JsonSerializerOptions options,
        out T? value,
        IAtomicFileSystem? fileSystem = null)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var fs = fileSystem ?? new DefaultAtomicFileSystem();
        foreach (var candidate in GetReadCandidates(filePath))
        {
            try
            {
                if (!fs.Exists(candidate))
                    continue;

                var decoded = JsonSerializer.Deserialize<T>(fs.ReadAllText(candidate), options);
                if (decoded != null)
                {
                    value = decoded;
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is JsonException || ex is ArgumentException || ex is NotSupportedException)
            {
                // 该候选不可用，继续尝试下一代
            }
        }

        return false;
    }

    private static IReadOnlyList<string> GetReadCandidates(string filePath) =>
        new[] { filePath, filePath + ".bak", filePath + ".tmp" };

    private static void SaveCore(
        string filePath,
        string json,
        IAtomicFileSystem fs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(directory))
            fs.CreateDirectory(directory);

        var tempPath = filePath + ".tmp";
        var backupPath = filePath + ".bak";
        fs.WriteAllText(tempPath, json);

        cancellationToken.ThrowIfCancellationRequested();

        if (!fs.Exists(filePath))
        {
            fs.Move(tempPath, filePath);
            return;
        }

        try
        {
            fs.Replace(tempPath, filePath, backupPath);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                   ex is PlatformNotSupportedException || ex is NotSupportedException)
        {
            // 降级路径：先把旧正式文件安全复制到 .bak，再覆盖正式文件。
            // 即使覆盖中途失败，加载端仍能从 .bak 恢复上一份完整内容。
            fs.Copy(filePath, backupPath, overwrite: true);
            fs.Copy(tempPath, filePath, overwrite: true);
            fs.Delete(tempPath);
        }
    }
}
