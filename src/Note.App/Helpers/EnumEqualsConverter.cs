using System.Globalization;
using System.Windows.Data;

namespace NoteApp.Helpers;

/// <summary>枚举相等比较转换器：判断绑定值（枚举）是否等于 ConverterParameter（枚举名字符串），用于菜单项勾选标记。</summary>
public class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null || parameter is not string name)
        {
            return false;
        }

        var type = value.GetType();
        return type.IsEnum && Enum.TryParse(type, name, ignoreCase: true, out var parsed) && value.Equals(parsed);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
