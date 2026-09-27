using NoteApp.Views;

namespace NoteApp.Services;

/// <summary>便签窗口管理服务。</summary>
public interface IWindowService
{
    // 窗口管理
    void ShowNoteWindow(Guid noteId);
    void HideNoteWindow(Guid noteId);
    void CloseNoteWindow(Guid noteId);
    void FocusNoteWindow(Guid noteId);

    // 批量
    void ShowAllNoteWindows();
    void HideAllNoteWindows();
    void ToggleAllNoteWindows();
    void CloseAllNoteWindows();

    // 状态查询
    bool IsWindowOpen(Guid noteId);
    List<Guid> GetOpenWindowIds();

    /// <summary>获取窗口实例（未打开时返回 null）。</summary>
    NoteWindow? GetWindow(Guid noteId);

    // 窗口层级
    void BringToFront(Guid noteId);
    void UpdateZOrder();

    // 位置与尺寸
    void SaveWindowState(Guid noteId);
    void RestoreWindowState(Guid noteId);

    // 事件
    event EventHandler<Guid>? WindowOpened;
    event EventHandler<Guid>? WindowClosed;
}
