using CommunityToolkit.Mvvm.ComponentModel;

namespace NoteApp.ViewModels;

/// <summary>侧边栏分类类型。</summary>
public enum CategoryType
{
    All,
    Pinned,
    Active,
    PendingReminder,
    TagGroup,
    Tag,
    Archived,
    Trash,
}

/// <summary>侧边栏分类项：固定分类 + 动态标签分类 + 「标签」分组标题。</summary>
public partial class CategoryItem : ObservableObject
{
    public CategoryItem(string name, string iconKey, CategoryType categoryType, int? tagId = null)
    {
        Name = name;
        IconKey = iconKey;
        CategoryType = categoryType;
        TagId = tagId;
    }

    /// <summary>侧边栏显示名。</summary>
    public string Name { get; }

    /// <summary>图标资源键（Icons.xaml 中的 Geometry）。分组标题为空字符串。</summary>
    public string IconKey { get; }

    public CategoryType CategoryType { get; }

    /// <summary>标签分类时的标签 ID；非标签分类为 null。</summary>
    public int? TagId { get; }

    /// <summary>便签数量徽标。</summary>
    [ObservableProperty]
    private int _count;

    /// <summary>是否为「标签」分组标题（不可点击）。</summary>
    public bool IsGroupHeader => CategoryType == CategoryType.TagGroup;

    /// <summary>标题栏显示名：「全部」显示为「全部便签」，其余为 Name。</summary>
    public string Title => CategoryType == CategoryType.All ? "全部便签" : Name;
}
