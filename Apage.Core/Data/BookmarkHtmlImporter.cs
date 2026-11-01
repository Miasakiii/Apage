#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace Apage.Core.Data;

/// <summary>导入器产出的一条书签（尚未入库）。</summary>
public sealed class ImportedBookmark
{
    public ImportedBookmark(string url, string title)
    {
        Url = url;
        Title = title;
    }

    public string Url { get; }

    public string Title { get; }
}

/// <summary>书签文件解析结果与跳过原因。</summary>
public sealed class BookmarkImportResult
{
    public BookmarkImportResult(
        IReadOnlyList<ImportedBookmark> items,
        int anchorCount,
        int skippedInvalid,
        int skippedDuplicate)
    {
        Items = items;
        AnchorCount = anchorCount;
        SkippedInvalid = skippedInvalid;
        SkippedDuplicate = skippedDuplicate;
    }

    public IReadOnlyList<ImportedBookmark> Items { get; }

    /// <summary>文件中识别到的 &lt;A&gt; 标签总数（含被跳过的）。</summary>
    public int AnchorCount { get; }

    public int SkippedInvalid { get; }

    public int SkippedDuplicate { get; }
}

/// <summary>
/// Chrome / Edge 导出的 Netscape Bookmark 文件解析器（B2）。
/// 只保留 http/https 链接：`javascript:`、`place:`、`file:` 等一律跳过，避免把脚本地址带进书签。
/// 解析容错优先：属性顺序、单双引号、大小写、实体、未闭合标签都不得抛异常；
/// 文件被截断时返回已解析部分，绝不因一处坏标签丢掉整份文件。
/// 当前 schema 无文件夹表，故只取 URL 与标题（文件夹层级信息被忽略）。
/// </summary>
public static class BookmarkHtmlImporter
{
    /// <summary>单文件大小上限：超出视为异常输入，避免把超大文件整读进内存。</summary>
    public const long MaxFileBytes = 16L * 1024 * 1024;

    private const int MaxTitleLength = 512;

    public static BookmarkImportResult ParseFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("书签文件路径不能为空。", nameof(path));

        var info = new System.IO.FileInfo(path);
        if (!info.Exists)
            throw new System.IO.FileNotFoundException("书签文件不存在。", path);
        if (info.Length > MaxFileBytes)
            throw new InvalidOperationException($"书签文件超过 {MaxFileBytes / 1024 / 1024}MB 上限，已拒绝解析。");

        using var reader = new System.IO.StreamReader(
            path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Parse(reader.ReadToEnd());
    }

    public static BookmarkImportResult Parse(System.IO.TextReader reader)
    {
        if (reader == null)
            throw new ArgumentNullException(nameof(reader));
        return Parse(reader.ReadToEnd());
    }

    public static BookmarkImportResult Parse(string? html)
    {
        var items = new List<ImportedBookmark>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var anchors = 0;
        var invalid = 0;
        var duplicate = 0;

        if (string.IsNullOrEmpty(html))
            return new BookmarkImportResult(items, 0, 0, 0);

        var index = 0;
        while (index < html!.Length)
        {
            var open = IndexOfIgnoreCase(html, "<a", index);
            if (open < 0)
                break;

            var after = open + 2;
            if (after < html.Length && !IsSpace(html[after]) && html[after] != '>')
            {
                index = after; // <abbr>/<article> 等，非锚点
                continue;
            }

            var tagEnd = FindTagEnd(html, after);
            if (tagEnd < 0)
                break; // 标签未闭合（文件截断）：保留已解析部分

            var close = IndexOfIgnoreCase(html, "</a>", tagEnd + 1);
            if (close < 0)
            {
                index = tagEnd + 1; // 没有闭合标签，跳过这个锚点继续找下一个
                continue;
            }

            anchors++;
            index = close + 4;

            var href = ReadAttribute(html, open + 2, tagEnd, "href");
            if (string.IsNullOrWhiteSpace(href))
            {
                invalid++;
                continue;
            }

            if (!UrlKey.TryNormalize(href, out var normalized))
            {
                invalid++;
                continue;
            }

            var title = ExtractText(html.Substring(tagEnd + 1, close - tagEnd - 1));
            if (title.Length == 0)
                title = UrlKey.GetHost(normalized);

            if (!seen.Add(normalized))
            {
                duplicate++;
                continue;
            }

            items.Add(new ImportedBookmark(normalized, Truncate(title)));
        }

        return new BookmarkImportResult(items, anchors, invalid, duplicate);
    }

    // 解析 <a ... > 起止范围内的某个属性值；支持 name="v" / name='v' / name=v，属性名大小写不敏感。
    private static string? ReadAttribute(string html, int start, int end, string name)
    {
        var i = start;
        while (i < end)
        {
            // 跳过空白与标签名/斜杠
            while (i < end && (IsSpace(html[i]) || html[i] == '/' || html[i] == '<'))
                i++;
            if (i >= end)
                return null;

            var nameStart = i;
            while (i < end && IsNameChar(html[i]))
                i++;
            if (i == nameStart)
            {
                i++; // 无法识别的字符，前进避免死循环
                continue;
            }

            var attrName = html.Substring(nameStart, i - nameStart);
            while (i < end && IsSpace(html[i]))
                i++;

            if (i >= end || html[i] != '=')
                continue; // 无值属性（如 NOWRAP）

            i++; // '='
            while (i < end && IsSpace(html[i]))
                i++;
            if (i >= end)
                return null;

            string value;
            var quote = html[i];
            if (quote == '"' || quote == '\'')
            {
                i++;
                var valueStart = i;
                while (i < end && html[i] != quote)
                    i++;
                value = html.Substring(valueStart, i - valueStart);
                if (i < end)
                    i++; // 跳过收尾引号
            }
            else
            {
                var valueStart = i;
                while (i < end && !IsSpace(html[i]))
                    i++;
                value = html.Substring(valueStart, i - valueStart);
            }

            if (string.Equals(attrName, name, StringComparison.OrdinalIgnoreCase))
                return System.Net.WebUtility.HtmlDecode(value);
        }

        return null;
    }

    // 取标签之间的纯文本：去嵌套标签、解码实体、压缩空白。
    private static string ExtractText(string fragment)
    {
        if (fragment.Length == 0)
            return string.Empty;

        var builder = new StringBuilder(fragment.Length);
        var inTag = false;
        foreach (var c in fragment)
        {
            if (c == '<')
            {
                inTag = true;
                continue;
            }
            if (c == '>')
            {
                inTag = false;
                continue;
            }
            if (!inTag)
                builder.Append(c);
        }

        var text = System.Net.WebUtility.HtmlDecode(builder.ToString());
        var normalized = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace && normalized.Length > 0)
                    normalized.Append(' ');
                lastWasSpace = true;
                continue;
            }
            normalized.Append(c);
            lastWasSpace = false;
        }

        return normalized.ToString().Trim();
    }

    private static bool IsNameChar(char c) =>
        !IsSpace(c) && c != '=' && c != '>' && c != '/' && c != '<';

    private static bool IsSpace(char c) => c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\f';

    private static int FindTagEnd(string html, int start)
    {
        var quote = '\0';
        for (var i = start; i < html.Length; i++)
        {
            var c = html[i];
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                continue;
            }

            if (c == '"' || c == '\'')
            {
                quote = c;
                continue;
            }

            if (c == '>')
                return i;
        }

        return -1;
    }

    private static int IndexOfIgnoreCase(string html, string value, int start)
    {
        if (start >= html.Length)
            return -1;
        return html.IndexOf(value, start, StringComparison.OrdinalIgnoreCase);
    }

    private static string Truncate(string value) =>
        value.Length <= MaxTitleLength ? value : value.Substring(0, MaxTitleLength);
}
