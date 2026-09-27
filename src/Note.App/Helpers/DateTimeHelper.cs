using System.Globalization;

namespace NoteApp.Helpers;

/// <summary>时间与数据库 TEXT 的转换（统一 ISO 8601 本地时间格式）。</summary>
public static class DateTimeHelper
{
    private const string Iso8601Format = "yyyy-MM-ddTHH:mm:ss";

    /// <summary>DateTime → 数据库存储字符串（ISO 8601，本地时间）。</summary>
    public static string ToStorage(DateTime value) =>
        value.ToString(Iso8601Format, CultureInfo.InvariantCulture);

    /// <summary>数据库存储字符串 → DateTime（本地时间）。</summary>
    public static DateTime Parse(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture);
}
