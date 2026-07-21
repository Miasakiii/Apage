#nullable enable
using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Apage.Core.Services;

/// <summary>WebView2 Runtime 检测结果（Core 层只做检测，不引用 WebView2 SDK）。</summary>
public sealed class WebView2RuntimeInfo
{
    public WebView2RuntimeInfo(bool isInstalled, string? version, string? source)
    {
        IsInstalled = isInstalled;
        Version = version;
        Source = source;
    }

    /// <summary>是否检测到可用的 WebView2 Runtime。</summary>
    public bool IsInstalled { get; }

    /// <summary>检测到的 Runtime 版本（pv 值），未安装时为 null。</summary>
    public string? Version { get; }

    /// <summary>命中来源（如 "HKCU/Registry32"），便于排查。</summary>
    public string? Source { get; }
}

/// <summary>
/// WebView2 Runtime 检测（风险 R1：缺失时引导用户手动下载，绝不静默联网下载）。
/// 读取注册表 EdgeUpdate Clients 键的 pv 值判定，与官方 GetAvailableCoreWebView2BrowserVersionString 等价。
/// 注：Core 目标为 netstandard2.0 且 csproj 不可改（无法引 Microsoft.Win32.Registry 包），
/// 故注册表访问走反射（运行时 net48 解析到 mscorlib，未来桌面版 .NET 解析到 Microsoft.Win32.Registry）。
/// </summary>
public static class WebView2RuntimeService
{
    // WebView2 Runtime 的 EdgeUpdate 客户端 GUID（固定值）
    private const string ClientKeyPath =
        @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    private static readonly string[] HiveNames = { "CurrentUser", "LocalMachine" };

    // 32/64 位视图都要查：64 位系统上 Runtime 常写在 WOW6432Node（Registry32 视图）
    private static readonly string[] ViewNames = { "Default", "Registry32", "Registry64" };

    /// <summary>检测当前机器是否安装 WebView2 Runtime，并返回版本信息。</summary>
    public static WebView2RuntimeInfo Detect()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new WebView2RuntimeInfo(false, null, null);

        foreach (var hive in HiveNames)
        foreach (var view in ViewNames)
        {
            var pv = TryReadPv(hive, view);
            if (IsUsableVersion(pv))
                return new WebView2RuntimeInfo(true, pv, hive + "/" + view);
        }

        return new WebView2RuntimeInfo(false, null, null);
    }

    private static bool IsUsableVersion(string? pv) =>
        !string.IsNullOrEmpty(pv) && pv != "0.0.0.0";

    private static string? TryReadPv(string hiveName, string viewName)
    {
        try
        {
            var registryKeyType = ResolveType("Microsoft.Win32.RegistryKey");
            var hiveType = ResolveType("Microsoft.Win32.RegistryHive");
            var viewType = ResolveType("Microsoft.Win32.RegistryView");
            if (registryKeyType == null || hiveType == null || viewType == null)
                return null;

            // RegistryKey.OpenBaseKey(RegistryHive, RegistryView)
            var openBaseKey = registryKeyType.GetMethod(
                "OpenBaseKey", BindingFlags.Public | BindingFlags.Static, null,
                new[] { hiveType, viewType }, null);
            if (openBaseKey == null)
                return null;

            var hive = Enum.Parse(hiveType, hiveName);
            var view = Enum.Parse(viewType, viewName);

            using var baseKey = openBaseKey.Invoke(null, new[] { hive, view }) as IDisposable;
            if (baseKey == null)
                return null;

            using var subKey = registryKeyType
                .GetMethod("OpenSubKey", new[] { typeof(string) })
                ?.Invoke(baseKey, new object[] { ClientKeyPath }) as IDisposable;
            if (subKey == null)
                return null;

            return registryKeyType
                .GetMethod("GetValue", new[] { typeof(string) })
                ?.Invoke(subKey, new object[] { "pv" }) as string;
        }
        catch
        {
            // 视图不可用（如 32 位系统请求 Registry64）或权限不足，均视为未命中
            return null;
        }
    }

    private static Type? ResolveType(string fullName) =>
        Type.GetType(fullName + ", mscorlib") ??
        Type.GetType(fullName + ", Microsoft.Win32.Registry");
}
