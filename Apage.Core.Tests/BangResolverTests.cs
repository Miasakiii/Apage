using Apage.Core.Services;
using Xunit;

namespace Apage.Core.Tests;

/// <summary>
/// BangResolver：本地 bangs 快捷搜索（R8）。
/// 把守修正后的边界：① 表为本地静态（编译期内置）；② 纯模板展开（无动态生成）；
/// ③ 全部 https 目标、模板必含 {q}；④ 空查询/未知触发词/非 bang 一律 null（回落普通搜索）。
/// </summary>
public sealed class BangResolverTests
{
    [Theory]
    [InlineData("!gh wpf", true)]
    [InlineData("!gh", true)]
    [InlineData("! x", false)]   // 触发词为空
    [InlineData("!", false)]     // 只有感叹号
    [InlineData("hello", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsBangInput_DetectsShape(string? input, bool expected)
    {
        Assert.Equal(expected, BangResolver.IsBangInput(input));
    }

    [Theory]
    [InlineData("!gh wpf", "https://github.com/search?q=wpf")]
    [InlineData("!g a b", "https://www.google.com/search?q=a%20b")]
    [InlineData("!bd 便携浏览器", "https://www.baidu.com/s?wd=%E4%BE%BF%E6%90%BA%E6%B5%8F%E8%A7%88%E5%99%A8")]
    [InlineData("!GH WPF", "https://github.com/search?q=WPF")] // 触发词大小写不敏感
    public void TryResolve_KnownBang_ExpandsTemplate(string input, string expected)
    {
        Assert.Equal(expected, BangResolver.TryResolve(input));
    }

    [Theory]
    [InlineData("!gh")]              // 空查询：回落普通搜索，不抢导航
    [InlineData("!gh   ")]           // 只有空白
    [InlineData("!unknown x")]       // 未知触发词
    [InlineData("! x")]              // 触发词为空
    [InlineData("!")]
    [InlineData("hello world")]      // 非 bang
    [InlineData("")]
    [InlineData(null)]
    public void TryResolve_Unresolvable_ReturnsNull(string? input)
    {
        Assert.Null(BangResolver.TryResolve(input));
    }

    [Fact]
    public void TryResolve_EscapesQuerySpecials()
    {
        var url = BangResolver.TryResolve("!g a&b=c#d");

        Assert.NotNull(url);
        Assert.DoesNotContain("&b=c", url!, System.StringComparison.Ordinal); // & 已转义，不会拼成新参数
        Assert.Contains("a%26b%3Dc%23d", url!, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Table_IsNonEmpty()
    {
        Assert.NotEmpty(BangResolver.Table);
        Assert.True(BangResolver.Table.Count >= 10, "curated 表至少覆盖十余条常用指令");
    }

    [Fact]
    public void Table_EveryTemplateContainsPlaceholderAndIsHttps()
    {
        // 修正后约束：纯模板展开（必含 {q}）+ 全部 https 目标
        foreach (var (trigger, template) in BangResolver.Table)
        {
            Assert.Contains(BangResolver.QueryPlaceholder, template);
            Assert.StartsWith("https://", template, System.StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(trigger));
        }
    }

    [Fact]
    public void Table_HasNoDuplicateTemplateCollisions()
    {
        // 同一模板被多个触发词共享是允许的，但触发词本身必须唯一（字典已保证）；
        // 这里顺带确认大小写变体不会重复注册
        var seen = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var trigger in BangResolver.Table.Keys)
        {
            Assert.True(seen.Add(trigger), "duplicate trigger: " + trigger);
        }
    }

    [Fact]
    public void TryResolve_QueryWithBangInside_IsNotRecursivelyParsed()
    {
        // 查询里再出现 ! 不再触发二次解析（纯字符串替换，无递归）
        var url = BangResolver.TryResolve("!g !gh wpf");

        Assert.NotNull(url);
        Assert.StartsWith("https://www.google.com/search?q=", url!, System.StringComparison.Ordinal);
    }
}
