using System.Windows.Input;

namespace NoteApp.Services;

/// <summary>全局快捷键服务。</summary>
public interface IHotkeyService
{
    // 注册与注销
    bool RegisterHotkey(string name, ModifierKeys modifiers, Key key);
    void UnregisterHotkey(string name);
    void UnregisterAll();

    // 查询
    bool IsRegistered(string name);
    HotkeyConfig? GetHotkey(string name);
    List<HotkeyConfig> GetAllHotkeys();

    // 预设快捷键
    void RegisterDefaultHotkeys();

    // 事件（快捷键触发时）
    event EventHandler<HotkeyTriggeredEventArgs>? HotkeyTriggered;
}
