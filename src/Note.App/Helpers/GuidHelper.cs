namespace NoteApp.Helpers;

/// <summary>GUID 与数据库 TEXT 的转换（统一大写、带连字符格式）。</summary>
public static class GuidHelper
{
    /// <summary>GUID → 数据库存储字符串（大写 D 格式）。</summary>
    /// <remarks>Notes.Id 与外键（NoteTags/Reminders 的 NoteId）依赖大小写一致做 JOIN 匹配。</remarks>
    public static string ToStorage(Guid id) => id.ToString("D").ToUpperInvariant();
}
