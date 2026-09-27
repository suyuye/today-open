namespace NoteApp.ViewModels;

/// <summary>列表排序方式。</summary>
public enum SortBy
{
    /// <summary>按修改时间，最新在前。</summary>
    ModifiedTimeDesc,

    /// <summary>按创建时间，最新在前。</summary>
    CreatedTimeDesc,

    /// <summary>按标题 A-Z 升序，空标题排最后。</summary>
    TitleAsc,

    /// <summary>按颜色分组，同色在一起，组内按修改时间降序。</summary>
    ByColor
}
