using Apage.Core.Services;
using Xunit;

namespace Apage.Core.Tests;

/// <summary>
/// BlockedResponse：被拦请求的空资源策略（R4）。
/// 两个不变量由本测试把守：① image/script/stylesheet/font 走空资源，其余 fail-closed 维持 403；
/// ② Content-Length 与 Payload 实际长度一致（错误长度会让 WebView2 报协议错误）。
/// </summary>
public sealed class BlockedResponseTests
{
    [Theory]
    [InlineData("Image")]
    [InlineData("image")]
    [InlineData(" IMAGE ")]
    public void For_Image_ReturnsTransparentGif(string context)
    {
        Assert.Equal(BlockedResponse.Kind.TransparentGif, BlockedResponse.For(context));
    }

    [Theory]
    [InlineData("Script")]
    [InlineData("Stylesheet")]
    [InlineData("Font")]
    [InlineData("script")]
    public void For_ScriptStyleFont_ReturnsEmptyText(string context)
    {
        Assert.Equal(BlockedResponse.Kind.EmptyText, BlockedResponse.For(context));
    }

    [Theory]
    [InlineData("Document")]
    [InlineData("Media")]
    [InlineData("XmlHttpRequest")]
    [InlineData("Fetch")]
    [InlineData("Other")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("something-unknown")]
    public void For_EverythingElse_Denies(string? context)
    {
        // fail-closed：拿不准就不改变既有拦截语义
        Assert.Equal(BlockedResponse.Kind.Deny, BlockedResponse.For(context));
    }

    [Fact]
    public void TransparentGif_IsValidGif89a()
    {
        var gif = BlockedResponse.TransparentGif;
        Assert.Equal(6, gif.Length >= 6 ? 6 : gif.Length);
        Assert.Equal((byte)'G', gif[0]);
        Assert.Equal((byte)'I', gif[1]);
        Assert.Equal((byte)'F', gif[2]);
        Assert.Equal((byte)'8', gif[3]);
        Assert.Equal((byte)'9', gif[4]);
        Assert.Equal((byte)'a', gif[5]);
    }

    [Fact]
    public void Payload_MatchesKind()
    {
        Assert.Same(BlockedResponse.TransparentGif, BlockedResponse.Payload(BlockedResponse.Kind.TransparentGif));
        Assert.Empty(BlockedResponse.Payload(BlockedResponse.Kind.EmptyText));
        Assert.Empty(BlockedResponse.Payload(BlockedResponse.Kind.Deny));
    }

    [Fact]
    public void Status_DenyIs403_OthersAre200()
    {
        var (denyCode, denyReason) = BlockedResponse.Status(BlockedResponse.Kind.Deny);
        Assert.Equal(403, denyCode);
        Assert.Equal("Blocked", denyReason);

        foreach (var kind in new[] { BlockedResponse.Kind.TransparentGif, BlockedResponse.Kind.EmptyText })
        {
            var (code, reason) = BlockedResponse.Status(kind);
            Assert.Equal(200, code);
            Assert.Equal("OK", reason);
        }
    }

    [Theory]
    [InlineData(BlockedResponse.Kind.TransparentGif)]
    [InlineData(BlockedResponse.Kind.EmptyText)]
    public void Headers_ContentLengthMatchesPayload(BlockedResponse.Kind kind)
    {
        var headers = BlockedResponse.Headers(kind);
        var expected = "Content-Length: " + BlockedResponse.Payload(kind).Length;
        Assert.Contains(expected, headers, System.StringComparison.Ordinal);
        Assert.Contains("Content-Type: ", headers, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Headers_DenyIsEmpty()
    {
        Assert.Equal(string.Empty, BlockedResponse.Headers(BlockedResponse.Kind.Deny));
    }
}
