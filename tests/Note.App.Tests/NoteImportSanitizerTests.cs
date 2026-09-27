using System.Windows;

using NoteApp.Helpers;
using NoteApp.Models;
using NoteApp.Models.Enums;

using Xunit;

namespace NoteApp.Tests;

/// <summary>
/// 导入数据净化测试。
///
/// 这组测试守的是一个很容易被忽略、但排查代价很高的场景：
/// 导入的便签坐标落在屏幕之外时，用户**既看不到也拖不回来**，
/// 表象和「导入失败」一模一样。所以「越界坐标必须被重新摆放到屏幕内」
/// 是导入功能里最值得写死的一条约定。
/// </summary>
public class NoteImportSanitizerTests
{
    /// <summary>单屏 1920×1080，从原点开始。</summary>
    private static readonly Rect Screen = new(0, 0, 1920, 1080);

    private static Note Note(int x, int y, int w = 280, int h = 240) => new()
    {
        Title = "标题",
        Content = "内容",
        PositionX = x,
        PositionY = y,
        Width = w,
        Height = h,
    };

    [Fact]
    public void 屏幕内的坐标应原样保留()
    {
        var note = Note(300, 400);

        NoteImportSanitizer.Sanitize(note, Screen, 0);

        Assert.Equal(300, note.PositionX);
        Assert.Equal(400, note.PositionY);
    }

    [Theory]
    [InlineData(-5000, 300)]      // 远在屏幕左侧之外
    [InlineData(9999, 300)]       // 远在屏幕右侧之外
    [InlineData(300, -8000)]      // 远在屏幕上方之外
    [InlineData(300, 99999)]      // 远在屏幕下方之外
    public void 越界坐标应被重新摆放到屏幕内(int x, int y)
    {
        var note = Note(x, y);

        NoteImportSanitizer.Sanitize(note, Screen, 3);

        Assert.True(NoteImportSanitizer.IsReachable(note, Screen),
            $"整理后仍不可达：({note.PositionX},{note.PositionY}) {note.Width}x{note.Height}");
        Assert.InRange(note.PositionX, 0, (int)Screen.Width);
        Assert.InRange(note.PositionY, 0, (int)Screen.Height);
    }

    [Fact]
    public void 只有一条像素露在屏幕里也算不可达()
    {
        // 只露 1px 的便签等于找不到，必须重新摆放
        var note = Note(1919, 300);

        Assert.False(NoteImportSanitizer.IsReachable(note, Screen));

        NoteImportSanitizer.Sanitize(note, Screen, 0);
        Assert.True(NoteImportSanitizer.IsReachable(note, Screen));
    }

    [Fact]
    public void 尺寸过小应抬到可编辑下限()
    {
        var note = Note(100, 100, w: 10, h: 5);

        NoteImportSanitizer.Sanitize(note, Screen, 0);

        Assert.Equal(NoteImportSanitizer.MinWidth, note.Width);
        Assert.Equal(NoteImportSanitizer.MinHeight, note.Height);
    }

    [Fact]
    public void 尺寸超过屏幕应压到屏幕以内()
    {
        // 比屏幕还宽的便签拖不动也用不了
        var note = Note(0, 0, w: 9999, h: 9999);

        NoteImportSanitizer.Sanitize(note, Screen, 0);

        Assert.True(note.Width <= (int)Screen.Width);
        Assert.True(note.Height <= (int)Screen.Height);
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(5, 30)]
    [InlineData(-20, 30)]
    [InlineData(100, 100)]
    [InlineData(999, 100)]
    [InlineData(60, 60)]
    public void 不透明度应被夹到30至100(int input, int expected)
    {
        var note = Note(100, 100);
        note.Opacity = input;

        NoteImportSanitizer.Sanitize(note, Screen, 0);

        Assert.Equal(expected, note.Opacity);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空白标题应归一为null以便由正文推导(string? title)
    {
        var note = Note(100, 100);
        note.Title = title;

        NoteImportSanitizer.Sanitize(note, Screen, 0);

        Assert.Null(note.Title);
    }

    [Fact]
    public void 标题两端空白应被去掉()
    {
        var note = Note(100, 100);
        note.Title = "  会议纪要  ";

        NoteImportSanitizer.Sanitize(note, Screen, 0);

        Assert.Equal("会议纪要", note.Title);
    }

    [Fact]
    public void 缺失的时间戳应补上而不是留成默认值()
    {
        var note = Note(100, 100);
        note.CreatedAt = default;
        note.UpdatedAt = default;

        NoteImportSanitizer.Sanitize(note, Screen, 0);

        // 0001-01-01 写进库会让相对时间显示成荒谬的值
        Assert.NotEqual(default, note.UpdatedAt);
        Assert.Equal(note.UpdatedAt, note.CreatedAt);
    }

    [Fact]
    public void 已有时间戳不应被改动()
    {
        var note = Note(100, 100);
        var created = new DateTime(2024, 3, 5, 8, 0, 0);
        var updated = new DateTime(2025, 6, 7, 9, 30, 0);
        note.CreatedAt = created;
        note.UpdatedAt = updated;

        NoteImportSanitizer.Sanitize(note, Screen, 0);

        Assert.Equal(created, note.CreatedAt);
        Assert.Equal(updated, note.UpdatedAt);
    }

    [Fact]
    public void 内容为null应归一为空串()
    {
        var note = Note(100, 100);
        note.Content = null!;

        NoteImportSanitizer.Sanitize(note, Screen, 0);

        Assert.Equal(string.Empty, note.Content);
    }

    [Fact]
    public void 层叠摆放的多条便签都应落在屏幕内()
    {
        for (var i = 0; i < 60; i++)
        {
            var note = Note(-9999, -9999);
            NoteImportSanitizer.PlaceCascade(note, Screen, i);
            NoteImportSanitizer.Sanitize(note, Screen, i);

            Assert.True(NoteImportSanitizer.IsReachable(note, Screen),
                $"第 {i} 条不可达：({note.PositionX},{note.PositionY})");
        }
    }

    [Fact]
    public void 层叠摆放的相邻便签不应完全重合()
    {
        var a = Note(0, 0);
        var b = Note(0, 0);

        NoteImportSanitizer.PlaceCascade(a, Screen, 0);
        NoteImportSanitizer.PlaceCascade(b, Screen, 1);

        Assert.NotEqual((a.PositionX, a.PositionY), (b.PositionX, b.PositionY));
    }

    [Fact]
    public void 负的层叠序号应按0处理()
    {
        var note = Note(0, 0);

        NoteImportSanitizer.PlaceCascade(note, Screen, -5);

        Assert.True(NoteImportSanitizer.IsReachable(note, Screen));
    }

    // ===== 文本文件 → 便签 =====

    [Fact]
    public void 首行是一级标题时应作为标题且不留在正文()
    {
        var note = NoteImportSanitizer.FromTextFile(
            @"D:\随便\购物清单.md", "# 周末采购\n- [ ] 牛奶\n- [x] 面包", NoteColor.GreenDeep);

        Assert.Equal("周末采购", note.Title);
        Assert.Equal("- [ ] 牛奶\n- [x] 面包", note.Content);
        Assert.Equal(NoteColor.GreenDeep, note.Color);
    }

    [Fact]
    public void 没有标题行时应使用文件名作标题()
    {
        var note = NoteImportSanitizer.FromTextFile(
            @"D:\随便\会议记录.md", "第一行就是正文\n第二行", NoteColor.BlueDeep);

        Assert.Equal("会议记录", note.Title);
        Assert.Equal("第一行就是正文\n第二行", note.Content);
    }

    [Fact]
    public void 正文应保留待办语法()
    {
        // 待办语法是纯文本存的，导入时必须原样保留，否则复选框会全部退化成普通文字
        var note = NoteImportSanitizer.FromTextFile("x.txt", "- [ ] 未完成\n- [x] 已完成", NoteColor.YellowDeep);

        Assert.Contains("- [ ] 未完成", note.Content);
        Assert.Contains("- [x] 已完成", note.Content);
    }

    [Fact]
    public void 换行符应统一为LF()
    {
        var note = NoteImportSanitizer.FromTextFile("x.txt", "第一行\r\n第二行\r第三行", NoteColor.YellowDeep);

        Assert.DoesNotContain('\r', note.Content);
        Assert.Equal("第一行\n第二行\n第三行", note.Content);
    }

    [Fact]
    public void 空文件应产生空正文而不是抛异常()
    {
        var note = NoteImportSanitizer.FromTextFile("空.md", "", NoteColor.YellowDeep);

        Assert.Equal("空", note.Title);
        Assert.Equal(string.Empty, note.Content);
    }

    [Fact]
    public void 只有井号而无标题文字时应退回文件名()
    {
        var note = NoteImportSanitizer.FromTextFile(@"D:\x\备忘.md", "# \n正文", NoteColor.YellowDeep);

        Assert.Equal("备忘", note.Title);
    }

    [Fact]
    public void 文本文件导入的便签也应落在屏幕内()
    {
        var note = NoteImportSanitizer.FromTextFile("x.txt", "内容", NoteColor.YellowDeep);

        NoteImportSanitizer.PlaceCascade(note, Screen, 0);
        NoteImportSanitizer.Sanitize(note, Screen, 0);

        Assert.True(NoteImportSanitizer.IsReachable(note, Screen));
    }
}
