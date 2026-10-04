using System.Windows;

using NoteApp.Helpers;

using Xunit;

namespace NoteApp.Tests;

/// <summary>
/// 框选矩形与命中判定的几何算术。
///
/// 为什么值得写：框选判定算错的表现是「框到三张却拖走了两张」或
/// 「明明框住了却没选中」，这类问题在界面上很难复现和描述。
/// 把方向归一化、相交语义、最小拖动门槛全部钉死。
/// </summary>
public class MarqueeGeometryTests
{
    // ===== ToRect：四方向拖拽都要归一化 =====

    [Fact]
    public void ToRect_DragRightDown_Normalizes()
    {
        var r = MarqueeGeometry.ToRect(new Point(100, 100), new Point(200, 250));

        Assert.Equal(new Rect(100, 100, 100, 150), r);
    }

    [Fact]
    public void ToRect_DragLeftUp_ProducesSameRect()
    {
        // 从右下往左上拖，选框几何应与反向拖完全一致
        var r = MarqueeGeometry.ToRect(new Point(200, 250), new Point(100, 100));

        Assert.Equal(new Rect(100, 100, 100, 150), r);
    }

    [Fact]
    public void ToRect_DragLeftDown_ProducesPositiveSize()
    {
        // 水平反向、垂直正向：最容易写错的组合
        var r = MarqueeGeometry.ToRect(new Point(200, 100), new Point(100, 250));

        Assert.Equal(new Rect(100, 100, 100, 150), r);
        Assert.True(r.Width > 0);
        Assert.True(r.Height > 0);
    }

    [Fact]
    public void ToRect_DragRightUp_ProducesPositiveSize()
    {
        var r = MarqueeGeometry.ToRect(new Point(100, 250), new Point(200, 100));

        Assert.Equal(new Rect(100, 100, 100, 150), r);
    }

    [Fact]
    public void ToRect_ZeroDrag_ProducesEmptyRect()
    {
        var r = MarqueeGeometry.ToRect(new Point(50, 50), new Point(50, 50));

        Assert.Equal(0, r.Width);
        Assert.Equal(0, r.Height);
    }

    // ===== IsSignificantDrag =====

    [Fact]
    public void IsSignificantDrag_SmallMovement_IsFalse()
    {
        // 只移动 2px：视为普通单击，不启动框选
        Assert.False(MarqueeGeometry.IsSignificantDrag(new Point(100, 100), new Point(102, 101)));
    }

    [Fact]
    public void IsSignificantDrag_HorizontalMovement_IsTrue()
    {
        Assert.True(MarqueeGeometry.IsSignificantDrag(new Point(100, 100), new Point(105, 100)));
    }

    [Fact]
    public void IsSignificantDrag_VerticalMovement_IsTrue()
    {
        Assert.True(MarqueeGeometry.IsSignificantDrag(new Point(100, 100), new Point(100, 106)));
    }

    [Fact]
    public void IsSignificantDrag_DiagonalSmallEach_AboveThreshold_IsTrue()
    {
        // 单轴都不足 4，但另一轴刚好达到 → 应算显著
        Assert.True(MarqueeGeometry.IsSignificantDrag(new Point(100, 100), new Point(103, 104)));
    }

    [Fact]
    public void IsSignificantDrag_CustomThreshold_IsRespected()
    {
        Assert.False(MarqueeGeometry.IsSignificantDrag(new Point(0, 0), new Point(5, 0), minSize: 10));
        Assert.True(MarqueeGeometry.IsSignificantDrag(new Point(0, 0), new Point(10, 0), minSize: 10));
    }

    // ===== HitTest：相交即选中 =====

    [Fact]
    public void HitTest_FullyInside_IsTrue()
    {
        var marquee = new Rect(0, 0, 500, 500);
        var note = new Rect(100, 100, 200, 200);

        Assert.True(MarqueeGeometry.HitTest(marquee, note));
    }

    [Fact]
    public void HitTest_PartiallyOverlapping_IsTrue()
    {
        // 便签只有一角进框 —— 按"相交即选中"应当命中
        var marquee = new Rect(0, 0, 150, 150);
        var note = new Rect(100, 100, 200, 200);

        Assert.True(MarqueeGeometry.HitTest(marquee, note));
    }

    [Fact]
    public void HitTest_NoteLargerThanMarquee_IsTrue()
    {
        // 便签比选框还大：要求"完全包含"就永远选不中它
        var marquee = new Rect(150, 150, 50, 50);
        var note = new Rect(100, 100, 400, 400);

        Assert.True(MarqueeGeometry.HitTest(marquee, note));
    }

    [Fact]
    public void HitTest_EdgeTouching_IsTrue()
    {
        // 仅边界相切：Rect.IntersectsWith 视为相交，符合"碰到就算"的直觉
        var marquee = new Rect(0, 0, 100, 100);
        var note = new Rect(100, 20, 80, 80);

        Assert.True(MarqueeGeometry.HitTest(marquee, note));
    }

    [Fact]
    public void HitTest_CompletelySeparate_IsFalse()
    {
        var marquee = new Rect(0, 0, 100, 100);
        var note = new Rect(200, 200, 80, 80);

        Assert.False(MarqueeGeometry.HitTest(marquee, note));
    }

    [Fact]
    public void HitTest_AdjacentButNotTouching_IsFalse()
    {
        // 相隔 1px
        var marquee = new Rect(0, 0, 100, 100);
        var note = new Rect(101, 20, 80, 80);

        Assert.False(MarqueeGeometry.HitTest(marquee, note));
    }

    // ===== HitTestAll：批量命中，返回下标 =====

    [Fact]
    public void HitTestAll_ReturnsIndicesInOrder()
    {
        var marquee = new Rect(0, 0, 300, 300);
        var notes = new List<Rect>
        {
            new(10, 10, 50, 50),     // 0 命中
            new(1000, 1000, 50, 50), // 1 未命中
            new(200, 200, 50, 50),   // 2 命中
        };

        var hits = MarqueeGeometry.HitTestAll(marquee, notes);

        Assert.Equal(new List<int> { 0, 2 }, hits);
    }

    [Fact]
    public void HitTestAll_NoneHit_ReturnsEmpty()
    {
        var marquee = new Rect(0, 0, 50, 50);
        var notes = new List<Rect> { new(500, 500, 50, 50) };

        Assert.Empty(MarqueeGeometry.HitTestAll(marquee, notes));
    }

    [Fact]
    public void HitTestAll_EmptyList_ReturnsEmpty()
    {
        Assert.Empty(MarqueeGeometry.HitTestAll(new Rect(0, 0, 100, 100), new List<Rect>()));
    }

    [Fact]
    public void HitTestAll_NullList_ReturnsEmpty()
    {
        Assert.Empty(MarqueeGeometry.HitTestAll(new Rect(0, 0, 100, 100), null!));
    }
}
