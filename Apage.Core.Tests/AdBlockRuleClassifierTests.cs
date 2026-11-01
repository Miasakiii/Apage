using Apage.Core.Services;
using Xunit;

namespace Apage.Core.Tests;

/// <summary>
/// 快照生成与运行时共用分类（A2）：不可执行语法必须在入库前被识别，不能退化为宽泛匹配。
/// </summary>
public sealed class AdBlockRuleClassifierTests
{
    [Theory]
    [InlineData(null, AdBlockRuleKind.Empty)]
    [InlineData("   ", AdBlockRuleKind.Empty)]
    [InlineData("! comment", AdBlockRuleKind.Comment)]
    [InlineData("[Adblock Plus 2.0]", AdBlockRuleKind.Comment)]
    [InlineData("||ads.example^", AdBlockRuleKind.Block)]
    [InlineData("@@||ads.example^", AdBlockRuleKind.Exception)]
    [InlineData("||ads.example^$script", AdBlockRuleKind.UnsupportedModifier)]
    [InlineData("$popup", AdBlockRuleKind.UnsupportedModifier)]
    [InlineData("/ad[0-9]+\\.js/", AdBlockRuleKind.UnsupportedRegex)]
    [InlineData("example.com##.ad", AdBlockRuleKind.Cosmetic)]
    [InlineData("example.com#@#.ad", AdBlockRuleKind.CosmeticUnsupported)]
    [InlineData("example.com#?#.ad", AdBlockRuleKind.CosmeticUnsupported)]
    [InlineData("@@", AdBlockRuleKind.Invalid)]
    public void Classify_ReturnsExecutableBoundary(string? rule, AdBlockRuleKind expected)
    {
        Assert.Equal(expected, AdBlockRuleEngine.Classify(rule));
    }

    [Fact]
    public void AddRule_RegexAndAdvancedCosmetic_AreRejected()
    {
        var engine = new AdBlockRuleEngine();

        Assert.False(engine.AddRule("/ad[0-9]+\\.js/"));
        Assert.False(engine.AddRule("example.com#@#.ad"));
        Assert.Equal(0, engine.BlockCount + engine.ExceptionCount + engine.CosmeticCount);
    }
}
