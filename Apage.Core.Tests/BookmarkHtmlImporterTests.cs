using System;
using System.Linq;
using Apage.Core.Data;
using Xunit;

namespace Apage.Core.Tests;

/// <summary>
/// B2 书签 HTML 导入（Netscape 格式）：Chrome/Edge 导出文件、属性变体、实体、截断与非法链接。
/// </summary>
public sealed class BookmarkHtmlImporterTests
{
    private const string ChromeExport = @"<!DOCTYPE NETSCAPE-Bookmark-file-1>
<META HTTP-EQUIV=""Content-Type"" CONTENT=""text/html; charset=UTF-8"">
<TITLE>Bookmarks</TITLE>
<H1>Bookmarks</H1>
<DL><p>
    <DT><H3 ADD_DATE=""1700000000"" LAST_MODIFIED=""1700000001"">书签栏</H3>
    <DL><p>
        <DT><A HREF=""https://example.com/a"" ADD_DATE=""1700000002"">示例 A</A>
        <DT><A HREF=""https://example.com/b?x=1&amp;y=2"" ADD_DATE=""1700000003"">查询 &amp; 参数</A>
        <DT><H3>子文件夹</H3>
        <DL><p>
            <DT><A HREF=""https://sub.example.com/"">子页面</A>
        </DL><p>
    </DL><p>
</DL><p>";

    [Fact]
    public void Parse_ChromeExport_ReadsAllAnchorsAcrossNesting()
    {
        var result = BookmarkHtmlImporter.Parse(ChromeExport);

        Assert.Equal(3, result.AnchorCount);
        Assert.Equal(3, result.Items.Count);
        Assert.Equal(0, result.SkippedInvalid);
        Assert.Equal(0, result.SkippedDuplicate);
        Assert.Contains(result.Items, i => i.Url == "https://example.com/a" && i.Title == "示例 A");
        Assert.Contains(result.Items, i => i.Url == "https://sub.example.com/" && i.Title == "子页面");
    }

    [Fact]
    public void Parse_DecodesEntitiesInUrlAndTitle()
    {
        var result = BookmarkHtmlImporter.Parse(ChromeExport);
        var item = result.Items.Single(i => i.Title.StartsWith("查询", StringComparison.Ordinal));

        Assert.Equal("https://example.com/b?x=1&y=2", item.Url);
        Assert.Equal("查询 & 参数", item.Title);
    }

    [Theory]
    [InlineData("<DT><A HREF='https://example.com/single'>单引号</A>", "https://example.com/single", "单引号")]
    [InlineData("<dt><a href=\"https://example.com/lower\">小写标签</a>", "https://example.com/lower", "小写标签")]
    [InlineData("<DT><A TITLE=\"t\" HREF=\"https://example.com/order\">顺序</A>", "https://example.com/order", "顺序")]
    [InlineData("<DT><A HREF=https://example.com/bare>无引号</A>", "https://example.com/bare", "无引号")]
    public void Parse_TolerantToAttributeAndTagVariants(string html, string url, string title)
    {
        var result = BookmarkHtmlImporter.Parse(html);

        var item = Assert.Single(result.Items);
        Assert.Equal(url, item.Url);
        Assert.Equal(title, item.Title);
    }

    [Fact]
    public void Parse_SkipsScriptAndLocalLinks()
    {
        var html = @"
<DT><A HREF=""javascript:alert(1)"">脚本</A>
<DT><A HREF=""place:sort=8"">地点</A>
<DT><A HREF=""file:///C:/Windows/notepad.exe"">本地</A>
<DT><A HREF=""https://example.com/ok"">正常</A>";

        var result = BookmarkHtmlImporter.Parse(html);

        var item = Assert.Single(result.Items);
        Assert.Equal("https://example.com/ok", item.Url);
        Assert.Equal(3, result.SkippedInvalid);
        Assert.Equal(4, result.AnchorCount);
    }

    [Fact]
    public void Parse_DuplicateUrls_AreCountedAndDropped()
    {
        var html = @"
<DT><A HREF=""https://example.com/x"">一</A>
<DT><A HREF=""https://EXAMPLE.com/x#top"">二</A>";

        var result = BookmarkHtmlImporter.Parse(html);

        Assert.Single(result.Items);
        Assert.Equal(1, result.SkippedDuplicate);
    }

    [Fact]
    public void Parse_EmptyOrNull_ReturnsEmptyResult()
    {
        Assert.Empty(BookmarkHtmlImporter.Parse((string?)null).Items);
        Assert.Empty(BookmarkHtmlImporter.Parse(string.Empty).Items);
        Assert.Empty(BookmarkHtmlImporter.Parse("   ").Items);
    }

    [Fact]
    public void Parse_TruncatedFile_KeepsParsedPrefixWithoutThrowing()
    {
        var truncated = @"<DL><p>
<DT><A HREF=""https://example.com/one"">一</A>
<DT><A HREF=""https://example.com/two"">二";

        var result = BookmarkHtmlImporter.Parse(truncated);

        var item = Assert.Single(result.Items);
        Assert.Equal("https://example.com/one", item.Url);
    }

    [Fact]
    public void Parse_MissingTitle_FallsBackToHost()
    {
        var result = BookmarkHtmlImporter.Parse("<DT><A HREF=\"https://fallback.example.com/path\"></A>");

        var item = Assert.Single(result.Items);
        Assert.Equal("fallback.example.com", item.Title);
    }

    [Fact]
    public void Parse_StripsNestedMarkupAndCollapsesWhitespace()
    {
        var result = BookmarkHtmlImporter.Parse(
            "<DT><A HREF=\"https://example.com/n\"><b>粗体</b>\n   标题</A>");

        var item = Assert.Single(result.Items);
        Assert.Equal("粗体 标题", item.Title);
    }

    [Fact]
    public void Parse_LongTitle_IsTruncated()
    {
        var longTitle = new string('标', 900);
        var result = BookmarkHtmlImporter.Parse($"<DT><A HREF=\"https://example.com/l\">{longTitle}</A>");

        var item = Assert.Single(result.Items);
        Assert.Equal(512, item.Title.Length);
    }
}
