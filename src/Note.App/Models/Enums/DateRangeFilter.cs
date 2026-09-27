namespace NoteApp.Models.Enums;

/// <summary>列表的时间范围筛选（按「最近修改时间」判断）。</summary>
public enum DateRangeFilter
{
    /// <summary>不限时间。</summary>
    All,

    /// <summary>今天修改过的。</summary>
    Today,

    /// <summary>最近 7 天修改过的。</summary>
    Last7Days,

    /// <summary>最近 30 天修改过的。</summary>
    Last30Days,
}
