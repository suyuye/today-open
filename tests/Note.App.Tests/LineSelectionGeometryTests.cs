using System.Windows;

using NoteApp.Helpers;

using Xunit;

namespace NoteApp.Tests;

/// <summary>
/// <see cref="LineSelectionGeometry"/> 的回归测试。
///
/// 核心不变量：**每个视觉行的高亮右边缘，必须等于该视觉行最后一个字符的右边缘**。
/// 缺陷形态是「每个折行视觉行都少掉最后一个字」，界面上看不出是少了字，
/// 只表现为一条条长短不一、和文字对不齐的色带 —— 用户因此无法判断选中了什么。
/// </summary>
public class LineSelectionGeometryTests
{
    private const double LineHeight = 19.32;
    private const double CharWidth = 10;

    /// <summary>
    /// 造「每行 4 个字符 × 3 个视觉行」的插入点表，模拟自动换行。
    /// 前插入点 <c>leading[i]</c> 与后插入点 <c>trailing[i]</c> 都按等宽字推进，
    /// 但换行处 <c>trailing</c> 停留在本行行尾、<c>leading</c> 跳到下一行行首 ——
    /// 这正是 WPF 的真实行为，也是缺陷的触发条件。
    /// </summary>
    private static (List<Rect> Leading, List<Rect> Trailing) ThreeWrappedRows()
    {
        var leading = new List<Rect>();
        var trailing = new List<Rect>();
        const int perRow = 4;   // 每行 4 个字符
        const double originX = 10;

        for (var row = 0; row < 3; row++)
        {
            var top = 100 + row * LineHeight;
            for (var c = 0; c < perRow; c++)
            {
                var index = row * perRow + c;
                var left = originX + c * CharWidth;
                leading.Add(new Rect(left, top, 0, LineHeight));
                trailing.Add(new Rect(left + CharWidth, top, 0, LineHeight));
            }
        }

        // 行尾插入点：leading 与 trailing 都停在第 3 行末尾
        var lastTop = 100 + 2 * LineHeight;
        var endX = originX + perRow * CharWidth;
        leading.Add(new Rect(endX, lastTop, 0, LineHeight));
        trailing.Add(new Rect(endX, lastTop, 0, LineHeight));

        return (leading, trailing);
    }

    [Fact]
    public void BuildRowRects_FullSelectionAcrossWrappedRows_CoversEachRowsLastCharacter()
    {
        var (leading, trailing) = ThreeWrappedRows();

        // 12 个字符，全选
        var rows = LineSelectionGeometry.BuildRowRects(leading, trailing, 0, 12, LineHeight);

        Assert.Equal(3, rows.Count);
        // 每行都从行首 x=10 铺满 4 个字符到 x=50 —— 不是 40（少一个字）
        Assert.All(rows, r => Assert.Equal(10, r.X, 0.01));
        Assert.All(rows, r => Assert.Equal(40, r.Width, 0.01));
        Assert.Equal(100, rows[0].Y, 0.01);
        Assert.Equal(100 + LineHeight, rows[1].Y, 0.01);
        Assert.Equal(100 + 2 * LineHeight, rows[2].Y, 0.01);
    }

    [Fact]
    public void BuildRowRects_WrappedRowEndsAtTrailingEdge_NotNextRowStart()
    {
        // 这是缺陷的直接断言：上一行行尾字符的右边缘必须来自 trailing，
        // 而不是 leading（leading 在换行处会退回到下一行行首）。
        var (leading, trailing) = ThreeWrappedRows();

        var firstRow = LineSelectionGeometry.BuildRowRects(leading, trailing, 0, 4, LineHeight);

        Assert.Single(firstRow);
        // 字符 0..3，右边缘 = trailing[3].Left = 10 + 4*10 = 50
        Assert.Equal(50, firstRow[0].X + firstRow[0].Width, 0.01);
        // 绝不能被算成下一行行首（leading[4].Left = 10）
        Assert.NotEqual(leading[4].Left, firstRow[0].X + firstRow[0].Width, 0.01);
    }

    [Fact]
    public void BuildRowRects_SelectionStartingMidRow_StartsAtThatCharsLeftEdge()
    {
        var (leading, trailing) = ThreeWrappedRows();

        // 从字符 1 选到末尾
        var rows = LineSelectionGeometry.BuildRowRects(leading, trailing, 1, 12, LineHeight);

        Assert.Equal(3, rows.Count);
        Assert.Equal(20, rows[0].X, 0.01);
        Assert.Equal(30, rows[0].Width, 0.01);
        Assert.Equal(10, rows[1].X, 0.01);
        Assert.Equal(40, rows[1].Width, 0.01);
        Assert.Equal(10, rows[2].X, 0.01);
        Assert.Equal(40, rows[2].Width, 0.01);
    }

    [Fact]
    public void BuildRowRects_SelectionEndingAtRowStart_DoesNotEmitEmptyRow()
    {
        var (leading, trailing) = ThreeWrappedRows();

        // 选到视觉行 2 的行首（字符 4）为止：只应产出一行
        var rows = LineSelectionGeometry.BuildRowRects(leading, trailing, 0, 4, LineHeight);

        Assert.Single(rows);
        Assert.Equal(100, rows[0].Y, 0.01);
        Assert.Equal(50, rows[0].X + rows[0].Width, 0.01);
    }

    [Fact]
    public void BuildRowRects_SingleRowPartial_ProducesSingleRect()
    {
        var (leading, trailing) = ThreeWrappedRows();

        // 字符 1..2（不含 3）
        var rows = LineSelectionGeometry.BuildRowRects(leading, trailing, 1, 3, LineHeight);

        Assert.Single(rows);
        Assert.Equal(20, rows[0].X, 0.01);
        Assert.Equal(20, rows[0].Width, 0.01);
    }

    [Fact]
    public void BuildRowRects_LastRowPartial_EndsAtSelectedCharsRightEdge()
    {
        var (leading, trailing) = ThreeWrappedRows();

        // 最后一行只选到字符 9（该行第 2 个字符）
        var rows = LineSelectionGeometry.BuildRowRects(leading, trailing, 0, 10, LineHeight);

        Assert.Equal(3, rows.Count);
        Assert.Equal(10, rows[2].X, 0.01);
        Assert.Equal(20, rows[2].Width, 0.01);
    }

    [Fact]
    public void BuildRowRects_EmptyOrInvertedRange_ProducesNothing()
    {
        var (leading, trailing) = ThreeWrappedRows();

        Assert.Empty(LineSelectionGeometry.BuildRowRects(leading, trailing, 5, 5, LineHeight));
        Assert.Empty(LineSelectionGeometry.BuildRowRects(leading, trailing, 7, 3, LineHeight));
        Assert.Empty(LineSelectionGeometry.BuildRowRects(new List<Rect>(), new List<Rect>(), 0, 3, LineHeight));
    }

    [Fact]
    public void BuildRowRects_ZeroHeightCaretRects_UsesFallbackHeight()
    {
        var leading = new List<Rect> { new(10, 100, 0, 0), new(20, 100, 0, 0) };
        var trailing = new List<Rect> { new(20, 100, 0, 0), new(30, 100, 0, 0) };

        var rows = LineSelectionGeometry.BuildRowRects(leading, trailing, 0, 1, 24);

        Assert.Single(rows);
        Assert.Equal(24, rows[0].Height, 0.01);
        Assert.Equal(20, rows[0].X + rows[0].Width, 0.01);
    }

    [Fact]
    public void BuildRowRects_BlankCharsWithValidTop_StillSegmentsByTop()
    {
        // 空白字符（空格）的插入点矩形依然带 Top；分段必须靠 Top 而非宽高，
        // 否则「空格 + 换行」的选区会被错并成一行，表现为色带横跨多行。
        var leading = new List<Rect>
        {
            new(10, 100, 0, LineHeight),
            new(20, 100, 0, LineHeight),
            new(10, 100 + LineHeight, 0, LineHeight),
            new(20, 100 + LineHeight, 0, LineHeight),
        };
        var trailing = new List<Rect>
        {
            new(20, 100, 0, LineHeight),
            new(30, 100, 0, LineHeight),
            new(20, 100 + LineHeight, 0, LineHeight),
            new(30, 100 + LineHeight, 0, LineHeight),
        };

        var rows = LineSelectionGeometry.BuildRowRects(leading, trailing, 0, 3, LineHeight);

        Assert.Equal(2, rows.Count);
        Assert.Equal(100, rows[0].Y, 0.01);
        Assert.Equal(100 + LineHeight, rows[1].Y, 0.01);
        // 第一行覆盖字符 0、1，右边缘 = trailing[1].Left = 30
        Assert.Equal(30, rows[0].X + rows[0].Width, 0.01);
    }

    [Fact]
    public void BuildRowRects_DegenerateTrailingEdge_StillProducesVisibleRow()
    {
        // 防御：后插入点接口在某些字号/空行下会退化成零宽。
        // 此时必须仍给出可见宽度，否则整条高亮被丢掉、用户以为没选中。
        var leading = new List<Rect> { new(10, 100, 0, LineHeight), new(10, 100, 0, LineHeight) };
        var trailing = new List<Rect> { new(10, 100, 0, LineHeight), new(10, 100, 0, LineHeight) };

        var rows = LineSelectionGeometry.BuildRowRects(leading, trailing, 0, 1, LineHeight);

        Assert.Single(rows);
        Assert.True(rows[0].Width > 0);
    }
}
