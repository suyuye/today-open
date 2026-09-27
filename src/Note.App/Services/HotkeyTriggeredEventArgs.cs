namespace NoteApp.Services;

/// <summary>快捷键触发事件参数。</summary>
public class HotkeyTriggeredEventArgs : EventArgs
{
    public HotkeyTriggeredEventArgs(string name)
    {
        Name = name;
    }

    public string Name { get; }
}
