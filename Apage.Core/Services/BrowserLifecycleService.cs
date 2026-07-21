#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Apage.Core.Configuration;

namespace Apage.Core.Services;

/// <summary>
/// 创建 WebView2 Environment 的工厂委托。Core 层不引用 WebView2 SDK，
/// 由 Portable 层用 CoreWebView2Environment.CreateAsync 实现后以 object 形式回传。
/// </summary>
/// <param name="userDataFolder">Environment 的用户数据目录。</param>
public delegate Task<object> WebView2EnvironmentFactory(string userDataFolder);

/// <summary>
/// 浏览器内核生命周期：创建/持有共享 CoreWebView2Environment 的门面（§5.4）。
/// 普通标签共享一个 Environment；隐私标签共享另一个独立隔离 Environment
/// （独立 userDataFolder，见 §5.4「而非每标签一个环境」——否则会击穿内存目标）。
/// userDataFolder 一律落在 <see cref="AppPaths.CacheDirectory"/>（R7 缓存策略）。
/// </summary>
public sealed class BrowserLifecycleService : IDisposable
{
    private readonly WebView2EnvironmentFactory _environmentFactory;
    private readonly SemaphoreSlim _environmentLock = new SemaphoreSlim(1, 1);

    private object? _sharedEnvironment;
    private object? _privateEnvironment;
    private bool _disposed;

    public BrowserLifecycleService(WebView2EnvironmentFactory environmentFactory)
    {
        _environmentFactory = environmentFactory ?? throw new ArgumentNullException(nameof(environmentFactory));
    }

    /// <summary>普通标签共享 Environment 的 userDataFolder（随 R7 缓存策略走）。</summary>
    public string SharedUserDataFolder => Path.Combine(AppPaths.CacheDirectory, "WebView2");

    /// <summary>隐私标签隔离 Environment 的 userDataFolder（随 R7 缓存策略走；崩溃残留由 watchdog/启动扫描兜底清理，§5.2）。</summary>
    public string PrivateUserDataFolder => Path.Combine(AppPaths.CacheDirectory, "WebView2.Private");

    /// <summary>获取（或创建）普通标签共享的 Environment。单飞创建，并发调用共享同一实例。</summary>
    public async Task<object> GetSharedEnvironmentAsync()
    {
        ThrowIfDisposed();
        if (_sharedEnvironment != null)
            return _sharedEnvironment;

        await _environmentLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_sharedEnvironment == null)
            {
                AppPaths.EnsureCreated();
                _sharedEnvironment = await _environmentFactory(SharedUserDataFolder).ConfigureAwait(false);
            }
            return _sharedEnvironment;
        }
        finally
        {
            _environmentLock.Release();
        }
    }

    /// <summary>获取（或创建）隐私标签共享的隔离 Environment（独立 Profile）。</summary>
    public async Task<object> GetPrivateEnvironmentAsync()
    {
        ThrowIfDisposed();
        if (_privateEnvironment != null)
            return _privateEnvironment;

        await _environmentLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_privateEnvironment == null)
            {
                AppPaths.EnsureCreated();
                _privateEnvironment = await _environmentFactory(PrivateUserDataFolder).ConfigureAwait(false);
            }
            return _privateEnvironment;
        }
        finally
        {
            _environmentLock.Release();
        }
    }

    /// <summary>
    /// 释放隐私 Environment（最后一个隐私标签关闭时调用）。
    /// 目录残留清理由崩溃守护进程 / 下次启动扫描兜底（§5.2），此处不做文件删除。
    /// </summary>
    public void ReleasePrivateEnvironment()
    {
        if (_disposed || _privateEnvironment == null)
            return;

        _environmentLock.Wait();
        try
        {
            (_privateEnvironment as IDisposable)?.Dispose();
            _privateEnvironment = null;
        }
        finally
        {
            _environmentLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        (_sharedEnvironment as IDisposable)?.Dispose();
        (_privateEnvironment as IDisposable)?.Dispose();
        _sharedEnvironment = null;
        _privateEnvironment = null;
        _environmentLock.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(BrowserLifecycleService));
    }
}
