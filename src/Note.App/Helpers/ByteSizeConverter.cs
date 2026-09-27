using System.Globalization;
using System.Windows.Data;

namespace NoteApp.Helpers;

/// <summary>字节数 → 人类可读大小字符串（B / KB / MB / GB），用于备份列表显示文件大小。</summary>
public class ByteSizeConverter : IValueConverter
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB" };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not long bytes)
        {
            return string.Empty;
        }

        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{size:0.##} {Units[unit]}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
