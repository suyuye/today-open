using System.Windows.Input;

namespace NoteApp.Helpers;

/// <summary>全局快捷键字符串的格式化与解析。存储格式 "Ctrl+Alt+N"（无空格，与 ISettingsService 一致），显示格式 "Ctrl + Alt + N"（空格分隔）。</summary>
public static class HotkeyHelper
{
    /// <summary>修饰键显示顺序：Ctrl → Alt → Shift → Win。</summary>
    private static readonly (ModifierKeys Flag, string Name)[] ModifierOrder =
    {
        (ModifierKeys.Control, "Ctrl"),
        (ModifierKeys.Alt, "Alt"),
        (ModifierKeys.Shift, "Shift"),
        (ModifierKeys.Windows, "Win"),
    };

    /// <summary>是否是修饰键（单独按下时不作为主键完成组合）。</summary>
    public static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin;

    /// <summary>将修饰键 + 主键格式化为存储格式，如 "Ctrl+Alt+N"。</summary>
    public static string FormatStorage(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>(5);
        foreach (var (flag, name) in ModifierOrder)
        {
            if (modifiers.HasFlag(flag))
            {
                parts.Add(name);
            }
        }

        parts.Add(GetKeyName(key));
        return string.Join("+", parts);
    }

    /// <summary>存储格式 → 显示格式，如 "Ctrl+Alt+N" → "Ctrl + Alt + N"。</summary>
    public static string ToDisplay(string storage) =>
        string.Join(" + ", storage.Split('+', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>解析存储格式为修饰键 + 主键；格式非法时返回 false。</summary>
    public static bool TryParse(string storage, out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;

        if (string.IsNullOrWhiteSpace(storage))
        {
            return false;
        }

        var parts = storage.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i])
            {
                case "Ctrl": modifiers |= ModifierKeys.Control; break;
                case "Alt": modifiers |= ModifierKeys.Alt; break;
                case "Shift": modifiers |= ModifierKeys.Shift; break;
                case "Win": modifiers |= ModifierKeys.Windows; break;
                default: return false;
            }
        }

        return TryGetKey(parts[^1], out key);
    }

    private static bool TryGetKey(string name, out Key key)
    {
        // 主键盘数字 0-9：Key 枚举成员是 "D0"~"D9"，存储格式用 "0"~"9"
        if (name.Length == 1 && name[0] is >= '0' and <= '9')
        {
            key = (Key)(Key.D0 + (name[0] - '0'));
            return true;
        }

        // 其余（A-Z、F1-F24、Space、NumPad0 等）与 Key 枚举成员同名
        return Enum.TryParse(name, out key);
    }

    private static string GetKeyName(Key key)
    {
        if (key is >= Key.D0 and <= Key.D9)
        {
            return ((char)('0' + (key - Key.D0))).ToString();
        }

        return key.ToString();
    }
}
