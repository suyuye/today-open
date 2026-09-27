namespace NoteApp.ViewModels;

/// <summary>
/// 下拉筛选项：给人看的名字 + 给代码用的值。
///
/// 用它而不是直接拿枚举/Id 绑 ComboBox，是因为需要「全部颜色 / 全部标签 / 全部时间」
/// 这类「不过滤」选项 —— 用一个可空的 Value 表示「不筛选」，逻辑比给每个枚举
/// 硬塞一个 All 成员干净。
/// </summary>
public sealed class FilterOption<T>
{
    public FilterOption(string name, T value)
    {
        Name = name;
        Value = value;
    }

    /// <summary>下拉里显示的文字。</summary>
    public string Name { get; }

    /// <summary>实际用于筛选的值（null 表示该项不参与筛选）。</summary>
    public T Value { get; }

    /// <summary>ComboBox 未设置 DisplayMemberPath 时的兜底显示。</summary>
    public override string ToString() => Name;
}
