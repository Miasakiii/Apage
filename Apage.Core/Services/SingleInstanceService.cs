using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Apage.Core.Services;

/// <summary>
/// 单实例判定（R13）：按「数据目录」而非按机器。
/// Mutex 名 = "Apage/" + 数据目录全路径 SHA256 的前 16 位 hex。
/// 同一数据目录只允许一个实例；同 U 盘插不同机器、一机多副本（数据目录不同）互不冲突。
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    private readonly string _mutexName;
    private Mutex _mutex;
    private bool _ownsMutex;
    private bool _isFirstInstance;

    /// <param name="dataDirectory">数据目录（任意形式，内部规范化为全路径后再哈希）。</param>
    public SingleInstanceService(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("数据目录不能为空。", nameof(dataDirectory));
        _mutexName = BuildMutexName(dataDirectory);
    }

    /// <summary>
    /// 尝试成为该数据目录的运行实例。
    /// 返回 true = 已获得所有权（继续运行）；false = 已有实例持有该数据目录。
    /// isFirstInstance = 本次是否由我们创建了 Mutex（此前无任何实例运行过）。
    /// 前任崩溃未释放（abandoned）时会自动接管。重复调用幂等。
    /// </summary>
    public bool TryAcquire(out bool isFirstInstance)
    {
        if (_mutex != null)
        {
            isFirstInstance = _isFirstInstance;
            return _ownsMutex;
        }

        // initiallyOwned: false，避免命名 Mutex 已被其他进程持有时构造函数阻塞排队
        var mutex = new Mutex(initiallyOwned: false, _mutexName, out var createdNew);

        // 零等待尝试夺取（正常被持有时立即返回 false，第二个实例可立即退出）
        try
        {
            _ownsMutex = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true; // 前任进程崩溃，Mutex 已归当前线程所有
        }

        if (_ownsMutex)
        {
            _mutex = mutex;
        }
        else
        {
            mutex.Dispose();
        }

        _isFirstInstance = createdNew;
        isFirstInstance = createdNew;
        return _ownsMutex;
    }

    /// <summary>释放实例所有权（正常退出时调用）。重复调用安全。</summary>
    public void Release()
    {
        if (!_ownsMutex)
            return;
        _ownsMutex = false;
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 非持有线程调用等边界情况，退出路径上不再抛错
        }
    }

    public void Dispose()
    {
        Release();
        _mutex?.Dispose();
        _mutex = null;
    }

    private static string BuildMutexName(string dataDirectory)
    {
        // 规范化：全路径 + 去尾部分隔符 + 大写（Windows 路径不区分大小写），保证同一目录得到同一名
        var fullPath = Path.GetFullPath(dataDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(fullPath));
        var hex = new StringBuilder(16);
        for (var i = 0; i < 8; i++) // 前 8 字节 = 16 位 hex
            hex.Append(hash[i].ToString("x2"));
        return "Apage/" + hex;
    }
}
