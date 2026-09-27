using NoteApp.Models.Enums;

namespace NoteApp.Services;

/// <summary>系统托盘服务。</summary>
public interface ITrayService
{
    // 生命周期
    void Initialize();
    void Show();
    void Hide();
    void Remove();

    // 状态
    bool IsVisible { get; }
    void SetTooltip(string text);
    void SetIcon(NoteColor color);

    // 菜单操作（通过事件绑定，不直接操作 UI）
    event EventHandler? NewNoteRequested;
    event EventHandler? ShowAllRequested;
    event EventHandler? HideAllRequested;
    event EventHandler? OpenListRequested;
    event EventHandler? OpenSettingsRequested;
    event EventHandler? ExitRequested;
}
