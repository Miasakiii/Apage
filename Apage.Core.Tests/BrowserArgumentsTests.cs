using System;
using System.Linq;
using Apage.Core.Models;
using Apage.Core.Services;
using Xunit;

namespace Apage.Core.Tests;

/// <summary>
/// BrowserArguments：内核命令行参数收口（R1）。
/// 两个不变量由本测试把守：① 输出只含 stock 开关，禁止传入清单一项不出现；
/// ② WebRTC 防护严格跟随设置，不写回任何隐私默认值。
/// </summary>
public sealed class BrowserArgumentsTests
{
    private static AppSettings NewSettings() => new AppSettings();

    [Fact]
    public void Build_ContainsAllAlwaysOnStockSwitches()
    {
        var args = BrowserArguments.Build(NewSettings());

        Assert.Contains("--disable-background-networking", args);
        Assert.Contains("--disable-component-update", args);
        Assert.Contains("--disable-domain-reliability", args);
        Assert.Contains("--disable-sync", args);
        Assert.Contains("--no-default-browser-check", args);
        Assert.Contains("--disable-client-side-phishing-detection", args);
        Assert.Contains("--no-pings", args);
    }

    [Fact]
    public void Build_ContainsEdgeFeatureDisableListAndAcceptLanguage()
    {
        var args = BrowserArguments.Build(NewSettings());

        Assert.Contains(
            "--disable-features=msWebOOUI,msPdfOOUI,msSmartScreenProtection,OptimizationGuideModelDownloading,MediaRouter",
            args);
        Assert.Contains("--accept-lang=zh-CN", args);
    }

    [Fact]
    public void Build_WebRtcProtectionOn_IncludesPolicySwitch()
    {
        var settings = NewSettings();
        settings.WebRTCIPProtection = true;

        var args = BrowserArguments.Build(settings);

        Assert.Contains("--force-webrtc-ip-handling-policy=disable_non_proxied_udp", args);
    }

    [Fact]
    public void Build_WebRtcProtectionOff_OmitsPolicySwitch()
    {
        var settings = NewSettings();
        settings.WebRTCIPProtection = false;

        var args = BrowserArguments.Build(settings);

        Assert.DoesNotContain("--force-webrtc-ip-handling-policy", args);
    }

    [Fact]
    public void Build_NeverContainsForbiddenNonStockSwitches()
    {
        // 非 stock 开关会被内核静默忽略：传入即制造「以为加固了」的虚假安全感，必须一项不出现
        var args = BrowserArguments.Build(NewSettings());

        foreach (var forbidden in BrowserArguments.ForbiddenSwitches)
        {
            Assert.DoesNotContain(forbidden, args, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ForbiddenSwitches_AreNonEmptyDashDashEntries()
    {
        Assert.NotEmpty(BrowserArguments.ForbiddenSwitches);
        Assert.All(BrowserArguments.ForbiddenSwitches, s => Assert.StartsWith("--", s, StringComparison.Ordinal));
    }

    [Fact]
    public void Build_DoesNotMutateSettings()
    {
        var settings = NewSettings();
        settings.WebRTCIPProtection = true;
        settings.SendDoNotTrack = true;
        settings.SendGPC = true;
        settings.ClearOnExit = false;

        BrowserArguments.Build(settings);

        // 隐私默认值红线：拼装参数不得写回任何设置
        Assert.True(settings.WebRTCIPProtection);
        Assert.True(settings.SendDoNotTrack);
        Assert.True(settings.SendGPC);
        Assert.False(settings.ClearOnExit);
    }

    [Fact]
    public void Build_NullSettings_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => BrowserArguments.Build(null!));
    }

    [Theory]
    [InlineData("--edge-webview-switches=--foo")]
    [InlineData("Apage.Portable.exe", "--edge-webview-switches", "--bar")]
    public void HasExternalOverride_DetectsInjectedSwitch(params string[] args)
    {
        Assert.True(BrowserArguments.HasExternalOverride(args));
    }

    [Theory]
    [InlineData("--smoke")]
    [InlineData("Apage.Portable.exe", "--force-webrtc-ip-handling-policy=disable_non_proxied_udp")]
    public void HasExternalOverride_Absent_ReturnsFalse(params string[] args)
    {
        Assert.False(BrowserArguments.HasExternalOverride(args));
    }

    [Fact]
    public void HasExternalOverride_Null_ReturnsFalse()
    {
        Assert.False(BrowserArguments.HasExternalOverride(null));
    }

    [Fact]
    public void Build_IsDeterministic()
    {
        // 同一设置两次拼装结果一致（Environment 创建只做一次，但收口点必须可单测、可复现）
        Assert.Equal(BrowserArguments.Build(NewSettings()), BrowserArguments.Build(NewSettings()));
    }
}
