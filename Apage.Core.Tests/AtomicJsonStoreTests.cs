using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Apage.Core.Services;
using Xunit;

namespace Apage.Core.Tests;

/// <summary>
/// AtomicJsonStore：Replace/Copy 故障下不得先删除唯一正式副本，读取可按 .bak/.tmp 恢复。
/// </summary>
public sealed class AtomicJsonStoreTests
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    [Fact]
    public async Task Save_ReplaceUnsupported_BacksUpOldFileBeforeOverwriting()
    {
        var fs = new FakeFileSystem { FailReplace = true };
        fs.Files[@"C:\data\settings.json"] = "{\"Value\":\"old\"}";

        await AtomicJsonStore.SaveAsync(
            @"C:\data\settings.json", "{\"Value\":\"new\"}", fileSystem: fs);

        Assert.Equal("{\"Value\":\"new\"}", fs.Files[@"C:\data\settings.json"]);
        Assert.Equal("{\"Value\":\"old\"}", fs.Files[@"C:\data\settings.json.bak"]);
        Assert.False(fs.Files.ContainsKey(@"C:\data\settings.json.tmp"));
    }

    [Fact]
    public async Task Save_OverwriteFails_LeavesOldPrimaryAndValidBackup()
    {
        var fs = new FakeFileSystem
        {
            FailReplace = true,
            FailCopyDestination = @"C:\data\settings.json",
        };
        fs.Files[@"C:\data\settings.json"] = "{\"Value\":\"old\"}";

        await Assert.ThrowsAsync<IOException>(() => AtomicJsonStore.SaveAsync(
            @"C:\data\settings.json", "{\"Value\":\"new\"}", fileSystem: fs));

        Assert.Equal("{\"Value\":\"old\"}", fs.Files[@"C:\data\settings.json"]);
        Assert.Equal("{\"Value\":\"old\"}", fs.Files[@"C:\data\settings.json.bak"]);
    }

    [Fact]
    public void Load_CorruptPrimary_FallsBackToBackup()
    {
        var fs = new FakeFileSystem();
        fs.Files[@"C:\data\settings.json"] = "{ not json";
        fs.Files[@"C:\data\settings.json.bak"] = "{\"Value\":\"backup\"}";

        var ok = AtomicJsonStore.TryReadJson<Sample>(
            @"C:\data\settings.json", JsonOptions, out var value, fs);

        Assert.True(ok);
        Assert.Equal("backup", value!.Value);
    }

    [Fact]
    public void Load_MissingPrimary_UsesDurableTempFile()
    {
        var fs = new FakeFileSystem();
        fs.Files[@"C:\data\settings.json.tmp"] = "{\"Value\":\"temp\"}";

        var ok = AtomicJsonStore.TryReadJson<Sample>(
            @"C:\data\settings.json", JsonOptions, out var value, fs);

        Assert.True(ok);
        Assert.Equal("temp", value!.Value);
    }

    [Fact]
    public async Task Save_NewFile_UsesMoveWithoutDeletingAnything()
    {
        var fs = new FakeFileSystem();

        await AtomicJsonStore.SaveAsync(
            @"C:\data\settings.json", "{\"Value\":\"new\"}", fileSystem: fs);

        Assert.Equal("{\"Value\":\"new\"}", fs.Files[@"C:\data\settings.json"]);
        Assert.False(fs.Files.ContainsKey(@"C:\data\settings.json.bak"));
        Assert.Empty(fs.DeletedPaths);
    }

    private sealed class Sample
    {
        public string Value { get; set; } = string.Empty;
    }

    private sealed class FakeFileSystem : IAtomicFileSystem
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> DeletedPaths { get; } = new();
        public bool FailReplace { get; set; }
        public string? FailCopyDestination { get; set; }

        public bool Exists(string path) => Files.ContainsKey(path);

        public void CreateDirectory(string path) { }

        public string ReadAllText(string path) =>
            Files.TryGetValue(path, out var value) ? value : throw new FileNotFoundException(path);

        public void WriteAllText(string path, string contents) => Files[path] = contents;

        public void Replace(string sourcePath, string destinationPath, string backupPath)
        {
            if (FailReplace)
                throw new PlatformNotSupportedException("replace unsupported");

            Files[backupPath] = Files[destinationPath];
            Files[destinationPath] = Files[sourcePath];
            Files.Remove(sourcePath);
        }

        public void Move(string sourcePath, string destinationPath)
        {
            Files[destinationPath] = Files[sourcePath];
            Files.Remove(sourcePath);
        }

        public void Copy(string sourcePath, string destinationPath, bool overwrite)
        {
            if (string.Equals(destinationPath, FailCopyDestination, StringComparison.OrdinalIgnoreCase))
                throw new IOException("copy failed");
            if (!overwrite && Files.ContainsKey(destinationPath))
                throw new IOException("destination exists");
            Files[destinationPath] = Files[sourcePath];
        }

        public void Delete(string path)
        {
            DeletedPaths.Add(path);
            Files.Remove(path);
        }
    }
}
