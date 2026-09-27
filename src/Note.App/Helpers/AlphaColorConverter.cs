using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace NoteApp.Helpers;

/// <summary>
/// 颜色 → 同色相半透明画刷转换器。
/// 用途：便签上那些「比底色深一点」的小色块（如折叠态待办徽标的底），
/// 需要从便签自身的文字色派生出一个很淡的底。
/// 
/// 之所以做成转换器而不是在 Colors.xaml 里预置静态画刷：
/// 便签有 12 种颜色，为每种颜色各配一套「徽标底 / 徽标描边」会多出几十个资源键，
/// 而且一旦色板再扩展就要同步补色，很容易漏。用转换器从便签文字色现算，
/// 无论色板怎么扩，颜色永远跟当前这张便签自洽。
/// 
/// ConverterParameter 为透明度百分比（0-100），默认 12。
/// </summary>
public class AlphaColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not Color color)
        {
            return Brushes.Transparent;
        }

        var percent = 12.0;
        if (parameter is string text &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            percent = parsed;
        }

        var alpha = (byte)Math.Clamp(Math.Round(255 * percent / 100.0), 0, 255);
        return new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
