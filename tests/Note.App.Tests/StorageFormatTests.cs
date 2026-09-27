using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Media;

using NoteApp.Helpers;
using NoteApp.Models.Enums;

using Xunit;

namespace NoteApp.Tests;

/// <summary>时间戳与 GUID 的存库格式测试。</summary>
public class StorageFormatTests
{
    /// <summary>
    /// 时间戳必须是 ISO 文本。
    /// 曾经把时间戳写成毫秒整数写进数据库，导致 <c>GetAllNotes</c> 抛
    /// <c>FormatException</c>、**所有便签窗口都恢复不出来**，
    /// 而表象只是「软件启动后空白」，完全看不出是格式问题。
    /// </summary>
    [Fact]
    public void 时间戳应存为ISO文本而非毫秒整数()
    {
        var text = DateTimeHelper.ToStorage(new DateTime(2026, 9, 25, 20, 37, 5));

        Assert.Equal("2026-09-25T20:37:05", text);
        Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}$"), text);
    }

    [Fact]
    public void 时间戳应能逐秒往返()
    {
        var original = new DateTime(2026, 9, 25, 20, 37, 5);

        Assert.Equal(original, DateTimeHelper.Parse(DateTimeHelper.ToStorage(original)));
    }

    [Fact]
    public void 解析ISO文本应得到正确时间()
    {
        var parsed = DateTimeHelper.Parse("2026-09-11T23:21:46");

        Assert.Equal(2026, parsed.Year);
        Assert.Equal(9, parsed.Month);
        Assert.Equal(11, parsed.Day);
        Assert.Equal(23, parsed.Hour);
        Assert.Equal(21, parsed.Minute);
        Assert.Equal(46, parsed.Second);
    }

    [Fact]
    public void 时间戳格式不应受当前区域性影响()
    {
        // 用固定区域性格式化，避免在非公历区域下产出别的格式
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            Assert.Equal("2026-09-25T20:37:05", DateTimeHelper.ToStorage(new DateTime(2026, 9, 25, 20, 37, 5)));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>
    /// GUID 必须存成大写带连字符格式：Notes.Id 与 NoteTags/Reminders 的 NoteId
    /// 靠字符串相等做 JOIN，大小写不一致会匹配不上。
    /// </summary>
    [Fact]
    public void GUID存库应为大写带连字符()
    {
        var id = Guid.Parse("0a5ee0f9-8ed7-4553-9674-287e2255fc08");

        var text = GuidHelper.ToStorage(id);

        Assert.Equal("0A5EE0F9-8ED7-4553-9674-287E2255FC08", text);
        Assert.Equal(36, text.Length);
        Assert.Equal(id, Guid.Parse(text));
    }

    [Fact]
    public void GUID存库应始终大写()
    {
        foreach (var id in new[] { Guid.NewGuid(), Guid.NewGuid() })
        {
            Assert.Equal(GuidHelper.ToStorage(id), GuidHelper.ToStorage(id).ToUpperInvariant());
        }
    }
}

/// <summary>值转换器测试。</summary>
public class ConverterTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1048576L, "1 MB")]
    [InlineData(2147483648L, "2 GB")]
    public void 字节数应转换为可读大小(long bytes, string expected)
    {
        Assert.Equal(expected, new ByteSizeConverter().Convert(bytes, typeof(string), null!, Culture));
    }

    [Fact]
    public void 字节数转换_非长整型输入应返回空串()
    {
        var converter = new ByteSizeConverter();

        Assert.Equal(string.Empty, converter.Convert("不是数字", typeof(string), null!, Culture));
        Assert.Equal(string.Empty, converter.Convert(null!, typeof(string), null!, Culture));
    }

    [Theory]
    [InlineData("YellowDeep", true)]
    [InlineData("yellowdeep", true)]
    [InlineData("YELLOWDEEP", true)]
    [InlineData("GreenDeep", false)]
    [InlineData("不存在", false)]
    public void 枚举相等比较(string parameter, bool expected)
    {
        Assert.Equal(expected, new EnumEqualsConverter().Convert(NoteColor.YellowDeep, typeof(bool), parameter, Culture));
    }

    [Fact]
    public void 枚举相等比较_非法入参应为false()
    {
        var converter = new EnumEqualsConverter();

        Assert.Equal(false, converter.Convert(null!, typeof(bool), "YellowDeep", Culture));
        Assert.Equal(false, converter.Convert(NoteColor.YellowDeep, typeof(bool), 42, Culture));
    }

    [Fact]
    public void 颜色转半透明画刷_应保留RGB并套用透明度()
    {
        var brush = (SolidColorBrush)new AlphaColorConverter()
            .Convert(Color.FromRgb(0x10, 0x20, 0x30), typeof(Brush), "50", Culture);

        Assert.Equal(0x10, brush.Color.R);
        Assert.Equal(0x20, brush.Color.G);
        Assert.Equal(0x30, brush.Color.B);
        Assert.Equal(128, brush.Color.A);   // 255 * 50% = 127.5 → 128
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("100", 255)]
    [InlineData(null, 31)]   // 未指定时默认 12% → 30.6 → 31
    public void 颜色转半透明画刷_透明度边界(string? parameter, byte expectedAlpha)
    {
        var brush = (SolidColorBrush)new AlphaColorConverter()
            .Convert(Color.FromRgb(1, 2, 3), typeof(Brush), parameter!, Culture);

        Assert.Equal(expectedAlpha, brush.Color.A);
    }

    [Fact]
    public void 颜色转半透明画刷_非颜色输入应返回透明画刷()
    {
        var result = new AlphaColorConverter().Convert("不是颜色", typeof(Brush), null!, Culture);

        Assert.Same(Brushes.Transparent, result);
    }

    // ===== 相对时间 =====
    //
    // 🔴 这个转换器的输出取决于「当前时刻」，所以这几个测试**必须注入固定时钟**。
    // 早先它们直接用 DateTime.Now / DateTime.Today，其中「同一天较早时刻」那条
    // 取的是今天 01:00 并断言 "01:00"，注释还写着「一定不会跨天（也不会是未来）」——
    // 该假设只在上午成立：凌晨跑测试时 01:00 变成未来，跨度为负被当成"刚刚"，
    // 测试就在午夜后无故变红，而且从报错完全看不出是测试本身的问题。

    /// <summary>固定"现在"：2026-09-27 14:30，用它构造所有相对时间场景。</summary>
    private static readonly DateTime FixedNow = new(2026, 9, 27, 14, 30, 0);

    private static RelativeTimeConverter ConverterAt(DateTime now) => new(() => now);

    [Fact]
    public void 相对时间_一分钟内显示刚刚()
    {
        var result = ConverterAt(FixedNow).Convert(FixedNow.AddSeconds(-30), typeof(string), null!, Culture);

        Assert.Equal("刚刚", result);
    }

    [Fact]
    public void 相对时间_一小时内显示分钟前()
    {
        var result = ConverterAt(FixedNow).Convert(FixedNow.AddMinutes(-5), typeof(string), null!, Culture);

        Assert.Equal("5分钟前", result);
    }

    /// <summary>同一天但超过一小时 → 显示 HH:mm。固定时钟保证 01:00 一定在"过去"。</summary>
    [Fact]
    public void 相对时间_同一天较早时刻显示时分()
    {
        var time = FixedNow.Date.AddHours(1);   // 2026-09-27 01:00，早于 14:30

        var result = ConverterAt(FixedNow).Convert(time, typeof(string), null!, Culture);

        Assert.Equal("01:00", result);
    }

    [Fact]
    public void 相对时间_更早的日期显示月日时分()
    {
        var time = FixedNow.Date.AddDays(-1).AddHours(12);

        var result = ConverterAt(FixedNow).Convert(time, typeof(string), null!, Culture);

        Assert.Equal(time.ToString("MM-dd HH:mm"), result);
    }

    /// <summary>未来时间（时钟回拨等）不应产出"负X分钟前"，按"刚刚"处理。</summary>
    [Fact]
    public void 相对时间_未来时间应显示刚刚()
    {
        var result = ConverterAt(FixedNow).Convert(FixedNow.AddMinutes(10), typeof(string), null!, Culture);

        Assert.Equal("刚刚", result);
    }

    [Fact]
    public void 相对时间_非时间输入应返回空串()
    {
        Assert.Equal(string.Empty, ConverterAt(FixedNow).Convert("不是时间", typeof(string), null!, Culture));
    }
}
