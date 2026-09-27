using System.Globalization;
using System.Windows.Data;

namespace NoteApp.Helpers;

/// <summary>修改时间 → 相对时间显示（刚刚 / X分钟前 / 今天 HH:mm / MM-dd HH:mm）。</summary>
public class RelativeTimeConverter : IValueConverter
{
    /// <summary>
    /// 「现在」的来源。默认就是 <see cref="DateTime.Now"/>，运行时行为完全不变。
    ///
    /// 之所以做成可替换：这个转换器的输出**取决于当前时刻**，而"当前时刻"在测试里
    /// 是不可控的。早先的测试直接写 `DateTime.Today.AddHours(1)` 并断言得到 "01:00"，
    /// 注释还写着「一定不会跨天（也不会是未来）」—— 这个假设只在**上午**成立；
    /// 到了凌晨，那个时间点落在未来，`now - time` 变负数，被当成"刚刚"，
    /// 测试便在午夜后无故变红（表象完全看不出是测试自己的问题）。
    /// 现在测试可以换成固定时钟，断言不再依赖运行时刻。
    /// </summary>
    private readonly Func<DateTime> _nowProvider;

    public RelativeTimeConverter() : this(() => DateTime.Now)
    {
    }

    /// <summary>用指定的"现在"来源构造（供测试注入固定时钟；XAML 走无参构造）。</summary>
    internal RelativeTimeConverter(Func<DateTime> nowProvider)
    {
        _nowProvider = nowProvider;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DateTime time)
        {
            return string.Empty;
        }

        var now = _nowProvider();
        var span = now - time;

        // 负跨度 = 未来时间：只可能来自时钟回拨或外部写入的未来时间戳，
        // 按"刚刚"处理（显示"负 X 分钟前"只会更费解）。
        if (span < TimeSpan.FromMinutes(1))
        {
            return "刚刚";
        }

        if (span < TimeSpan.FromHours(1))
        {
            return $"{(int)span.TotalMinutes}分钟前";
        }

        if (time.Date == now.Date)
        {
            return time.ToString("HH:mm");
        }

        return time.ToString("MM-dd HH:mm");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
