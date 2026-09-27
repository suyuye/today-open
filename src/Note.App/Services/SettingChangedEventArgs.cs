namespace NoteApp.Services;

/// <summary>设置变更事件参数。</summary>
public class SettingChangedEventArgs : EventArgs
{
    public SettingChangedEventArgs(string key, string? value)
    {
        Key = key;
        Value = value;
    }

    public string Key { get; }
    public string? Value { get; }
}
