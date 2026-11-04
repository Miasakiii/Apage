#nullable enable

namespace Apage.Core.Data;

/// <summary>SQL LIKE 子串匹配的转义工具（% _ \ 按字面量处理）。</summary>
internal static class SqlLike
{
    public static string Contains(string keyword) => "%" + Escape(keyword) + "%";

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
