using NoteApp.Helpers;
using NoteApp.Models.Enums;

using Xunit;

namespace NoteApp.Tests;

/// <summary>
/// 便签颜色解析测试。
///
/// 数据库里存的是**枚举名的小写字符串**，而色板从 6 色扩到 12 色时枚举名改过
/// （<c>Yellow</c> → <c>YellowDeep</c>）。若某处直接 <c>Enum.Parse</c> 而没有走
/// <see cref="NoteColorHelper.Parse"/>，旧便签会**整条读不出来**（抛异常，
/// 不是静默降级）—— 表现为所有便签窗口都恢复不出来。这组测试就是守住这条兼容路径。
/// </summary>
public class NoteColorHelperTests
{
    [Fact]
    public void 色板应为12色且无重复()
    {
        Assert.Equal(12, NoteColorHelper.All.Count);
        Assert.Equal(12, NoteColorHelper.All.Distinct().Count());
    }

    [Fact]
    public void 每个颜色都应能从存库字符串还原()
    {
        foreach (var color in NoteColorHelper.All)
        {
            Assert.Equal(color, NoteColorHelper.Parse(NoteColorHelper.ToStorage(color)));
        }
    }

    [Fact]
    public void 存库字符串应为小写枚举名()
    {
        Assert.Equal("yellowdeep", NoteColorHelper.ToStorage(NoteColor.YellowDeep));
        Assert.Equal("purplelight", NoteColorHelper.ToStorage(NoteColor.PurpleLight));
    }

    [Theory]
    [InlineData("yellow", NoteColor.YellowDeep)]
    [InlineData("green", NoteColor.GreenDeep)]
    [InlineData("blue", NoteColor.BlueDeep)]
    [InlineData("pink", NoteColor.PinkDeep)]
    [InlineData("purple", NoteColor.PurpleDeep)]
    [InlineData("gray", NoteColor.GrayDeep)]
    public void 旧版6色枚举名_应映射到对应的深档(string legacy, NoteColor expected)
    {
        Assert.Equal(expected, NoteColorHelper.Parse(legacy));
    }

    [Theory]
    [InlineData("Yellow")]
    [InlineData("YELLOWDEEP")]
    [InlineData("yellowDeep")]
    [InlineData("YellowDeep")]
    public void 解析应忽略大小写(string raw)
    {
        Assert.Equal(NoteColor.YellowDeep, NoteColorHelper.Parse(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("不存在的颜色")]
    [InlineData("123")]
    public void 无法识别的输入_应兜底为默认色而绝不抛异常(string? raw)
    {
        // 单条便签的颜色字段异常，不该让整个列表加载失败
        Assert.Equal(NoteColorHelper.Default, NoteColorHelper.Parse(raw));
    }

    [Fact]
    public void 默认色应为米黄深档()
    {
        Assert.Equal(NoteColor.YellowDeep, NoteColorHelper.Default);
        Assert.Contains(NoteColorHelper.Default, NoteColorHelper.All);
    }

    [Fact]
    public void 每个颜色都应有非空且互不重复的显示名()
    {
        var names = NoteColorHelper.All.Select(NoteColorHelper.GetDisplayName).ToList();

        Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void 深浅两档都应在色板里()
    {
        Assert.Contains(NoteColor.YellowLight, NoteColorHelper.All);
        Assert.Contains(NoteColor.GrayLight, NoteColorHelper.All);
    }
}
