using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using NoteApp.Models.Enums;

namespace NoteApp.Helpers;

/// <summary>
/// 便签颜色枚举 → 颜色画刷转换器。
/// ConverterParameter 指定色块：Bg / Title / Border / Text / Shadow。
/// 找不到资源时返回透明画刷（WPF 绑定不应因缺资源崩溃）；
/// 色板完整性由 NoteColorHelper 保证，任何新增颜色都必须补齐五个键。
/// </summary>
public class NoteColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // 兼容字符串入参：设置项里存的可能是枚举名字符串
        var color = value switch
        {
            NoteColor c => c,
            string s => NoteColorHelper.Parse(s),
            _ => NoteColorHelper.Default,
        };

        var part = parameter is string p ? p : "Bg";
        var key = $"Note{color}_{part}";
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
