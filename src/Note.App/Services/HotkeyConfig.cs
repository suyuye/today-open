using System.Windows.Input;

namespace NoteApp.Services;

/// <summary>快捷键配置模型。</summary>
public class HotkeyConfig
{
    public string Name { get; set; } = "";
    public ModifierKeys Modifiers { get; set; }
    public Key Key { get; set; }
    public string Description { get; set; } = "";
}
