using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace NoteApp.Helpers;

/// <summary>图标资源键（字符串）→ Geometry 转换器，用于侧边栏导航项图标。</summary>
public class IconKeyToGeometryConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string key ? Application.Current.TryFindResource(key) ?? Geometry.Empty : Geometry.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
