using System.Windows.Media;

using NoteApp.Helpers;
using NoteApp.Models;

using Xunit;

namespace NoteApp.Tests;

/// <summary>
/// 便签「卷角」相关测试：模型默认值 + 配色推导纯函数。
///
/// <para>
/// 卷角是纯装饰，视觉本身（三角形怎么摆）由 XAML 负责、无法单测；
/// 但配色推导是纯函数、必须锁住性质 —— 尤其是"色相不变"和"永不隐形"两条，
/// 它们正是本项目历史上踩过的坑（把全透明当成有效色传下去，导致派生元素消失）。
/// </para>
/// </summary>
public class DogEarTests
{
    // ===== 模型默认值 =====

    [Fact]
    public void 新便签默认不开启卷角()
    {
        Assert.False(new Note().IsDogEar);
    }

    [Fact]
    public void 卷角可从模型赋值()
    {
        var note = new Note { IsDogEar = true };

        Assert.True(note.IsDogEar);
    }

    // ===== 明度偏移：方向与边界 =====

    [Fact]
    public void 正向偏移应提亮_朝白靠拢()
    {
        var color = Color.FromRgb(0x80, 0x40, 0x20);
        var lighter = DogEarPalette.ShiftBrightness(color, 0.30);

        Assert.True(lighter.R > color.R);
        Assert.True(lighter.G > color.G);
        Assert.True(lighter.B > color.B);
    }

    [Fact]
    public void 负向偏移应压暗_朝黑靠拢()
    {
        var color = Color.FromRgb(0x80, 0x40, 0x20);
        var darker = DogEarPalette.ShiftBrightness(color, -0.22);

        Assert.True(darker.R < color.R);
        Assert.True(darker.G < color.G);
        Assert.True(darker.B < color.B);
    }

    [Fact]
    public void 零偏移应保持原色()
    {
        var color = Color.FromRgb(0x33, 0x66, 0x99);

        Assert.Equal(color, DogEarPalette.ShiftBrightness(color, 0));
    }

    [Fact]
    public void 偏移量应被夹到合法区间()
    {
        var color = Color.FromRgb(0x40, 0x80, 0xC0);

        // 超过 1 的提亮 == 全白；低于 -1 的压暗 == 全黑
        Assert.Equal(Colors.White, DogEarPalette.ShiftBrightness(color, 5.0));
        Assert.Equal(Colors.Black, DogEarPalette.ShiftBrightness(color, -5.0));
    }

    [Fact]
    public void 全黑提亮仍是有效色_不会越界()
    {
        var lighter = DogEarPalette.ShiftBrightness(Colors.Black, 0.30);

        // 全黑朝白插值 30% → 每个分量都是 (byte)(0 + 255*0.3) = 76
        Assert.Equal(76, lighter.R);
        Assert.Equal(76, lighter.G);
        Assert.Equal(76, lighter.B);
    }

    [Fact]
    public void 全白压暗仍是有效色()
    {
        var darker = DogEarPalette.ShiftBrightness(Colors.White, -0.22);

        // 255 * 0.78 = 198.9 → 198
        Assert.Equal(198, darker.R);
    }

    // ===== 色相保持（RGB 三通道同比例变化，相对排序不变） =====

    [Fact]
    public void 明度偏移应保持通道相对关系_即色相不翻转()
    {
        // R > G > B 的暖色，偏移后仍应 R > G > B
        var color = Color.FromRgb(0xC0, 0x90, 0x60);

        var face = DogEarPalette.FaceColor(color);
        var shadow = DogEarPalette.ShadowColor(color);

        Assert.True(face.R > face.G && face.G > face.B);
        Assert.True(shadow.R > shadow.G && shadow.G > shadow.B);
    }

    // ===== 亮面比原色亮、阴影比原色暗（卷角的两面可区分） =====

    [Fact]
    public void 亮面必须比便签底色亮_阴影必须比底色暗()
    {
        foreach (var bg in new[]
                 {
                     Color.FromRgb(0xFF, 0xF4, 0xC8),   // 浅黄
                     Color.FromRgb(0xC8, 0xE6, 0xC9),   // 浅绿
                     Color.FromRgb(0x3A, 0x3A, 0x3A),   // 深灰
                 })
        {
            var face = DogEarPalette.FaceColor(bg);
            var shadow = DogEarPalette.ShadowColor(bg);

            Assert.True(Luminance(face) > Luminance(bg), "折角亮面应比底色亮");
            Assert.True(Luminance(shadow) < Luminance(bg), "折痕阴影应比底色暗");
        }
    }

    [Fact]
    public void 亮面不应等于底色_否则卷角不可见()
    {
        var bg = Color.FromRgb(0x64, 0x96, 0xC8);

        Assert.NotEqual(bg, DogEarPalette.FaceColor(bg));
    }

    /// <summary>常量护栏：偏移量必须落在"看得见"的区间，不能细到没差别。</summary>
    [Fact]
    public void 偏移常量应落在可感知区间()
    {
        Assert.InRange(DogEarPalette.FaceLightenAmount, 0.15, 0.55);
        Assert.InRange(DogEarPalette.ShadowDarkenAmount, -0.45, -0.10);
    }

    private static double Luminance(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
}
