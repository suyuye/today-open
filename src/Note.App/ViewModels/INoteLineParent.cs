namespace NoteApp.ViewModels;

/// <summary>便签内容行的父视图模型接口：NoteWindowViewModel 和 NotePreviewViewModel 都实现此接口。</summary>
public interface INoteLineParent
{
    /// <summary>行内容变化时通知父视图模型合并保存。</summary>
    void NotifyLineContentChanged();

    /// <summary>
    /// 待办完成状态变化时通知父视图模型。
    /// 与 <see cref="NotifyLineContentChanged"/> 分开的原因：勾选待办需要刷新
    /// 折叠态的未完成计数徽标，但那个计数是纯 UI 状态，不应触发内容保存逻辑。
    /// </summary>
    void NotifyTodoStateChanged();
}
