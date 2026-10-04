using System.Windows;

using NoteApp.Helpers;

using Xunit;

namespace NoteApp.Tests;

/// <summary>
/// 便签之间边缘对齐吸附的坐标算术。
///
/// 为什么值得写断言：吸附算错的表现是「便签拖着拖着突然跳到奇怪的位置」，
/// 界面上完全看不出是算术问题，用户只会觉得软件"发疯了"。
/// 这里把四条边的对齐、阈值边界、多便签竞争、空列表都钉死。
/// </summary>
public class NoteAlignGeometryTests
{
    /// <summary>被拖动的便签：100×80，左上角在 (500, 400)。</summary>
    private static readonly Rect Dragged = new(500, 400, 100, 80);

    // ===== 左边对左边 =====

    [Fact]
    public void Compute_LeftEdgeAlmostAligned_SnapsHorizontally()
    {
        // 对方左边界在 503，被拖左边界 500 → 差 3，在阈值内
        var other = new Rect(503, 900, 100, 80);

        var result = NoteAlignGeometry.Compute(Dragged, new[] { other });

        Assert.True(result.AlignedX);
        Assert.Equal(3, result.Dx);          // 被拖便签要往右挪 3
        Assert.Equal(503, result.GuideLeft); // 提示线画在 503
        Assert.False(result.AlignedY);       // 纵向差很远，不吸
    }

    [Fact]
    public void Compute_LeftEdgeBeyondThreshold_DoesNotSnap()
    {
        // 差 9，超过默认阈值 8
        var other = new Rect(509, 900, 100, 80);

        var result = NoteAlignGeometry.Compute(Dragged, new[] { other });

        Assert.False(result.AlignedX);
        Assert.Equal(0, result.Dx);
    }

    [Fact]
    public void Compute_LeftEdgeExactlyAtThreshold_Snaps()
    {
        // 恰好等于阈值：应吸附（<= 判定）
        var other = new Rect(500 + NoteAlignGeometry.AlignSnapDistance, 900, 100, 80);

        var result = NoteAlignGeometry.Compute(Dragged, new[] { other });

        Assert.True(result.AlignedX);
    }

    // ===== 右边对右边 =====

    [Fact]
    public void Compute_RightEdgeAlmostAligned_SnapsHorizontally()
    {
        // 被拖右边界 = 600。对方右边界在 596 → 差 4，左边界 496 与 500 差 4 也是候选，
        // 两个候选同为 4，取先算到的「左对左」——这里对方左边界放到 200 避免干扰。
        var other = new Rect(396, 900, 200, 80); // 右边界 596

        var result = NoteAlignGeometry.Compute(Dragged, new[] { other });

        Assert.True(result.AlignedX);
        Assert.Equal(-4, result.Dx);   // 被拖便签要往左挪 4
        Assert.Equal(596, result.GuideLeft);
    }

    [Fact]
    public void Compute_PicksNearestAmongLeftAndRightCandidates()
    {
        // 对方左边界 502（差 2）、右边界 607（被拖右 600，差 7）→ 应选差 2 的那个
        var other = new Rect(502, 900, 105, 80); // 右边界 607

        var result = NoteAlignGeometry.Compute(Dragged, new[] { other });

        Assert.True(result.AlignedX);
        Assert.Equal(2, result.Dx);
        Assert.Equal(502, result.GuideLeft);
    }

    // ===== 上对下 =====

    [Fact]
    public void Compute_TopEdgeAlmostAligned_SnapsVertically()
    {
        var other = new Rect(1500, 397, 100, 80); // 上边界 397，与 400 差 3

        var result = NoteAlignGeometry.Compute(Dragged, new[] { other });

        Assert.True(result.AlignedY);
        Assert.Equal(-3, result.Dy);
        Assert.Equal(397, result.GuideTop);
        Assert.False(result.AlignedX);
    }

    [Fact]
    public void Compute_BottomEdgeAlmostAligned_SnapsVertically()
    {
        // 被拖下边界 = 480。对方下边界 485 → 差 5；
        // 对方上边界刻意放到 430（差 30）以避免与「上对上」候选并列。
        var other = new Rect(1500, 430, 100, 55); // 上 430 下 485

        var result = NoteAlignGeometry.Compute(Dragged, new[] { other });

        Assert.True(result.AlignedY);
        Assert.Equal(5, result.Dy);
        Assert.Equal(485, result.GuideTop);
    }

    [Fact]
    public void Compute_TopAndBottomTie_PrefersTop()
    {
        // 对方上边界 405（差 5）、下边界 485（差 5）——两个候选并列。
        // 实现按「先上后下」的顺序、用严格小于比较，因此并列时保留先算到的「上对上」，
        // 于是 dy = +5（把被拖便签下移 5 使上边界对齐到 405）、提示线画在 405。
        // 这是确定性行为，钉死它，避免将来有人调换比较顺序却以为无害。
        var other = new Rect(1500, 405, 100, 80); // 上 405 下 485

        var result = NoteAlignGeometry.Compute(Dragged, new[] { other });

        Assert.True(result.AlignedY);
        Assert.Equal(5, result.Dy);
        Assert.Equal(405, result.GuideTop);
    }

    // ===== 两轴同时吸附 =====

    [Fact]
    public void Compute_BothAxesWithinThreshold_SnapsBoth()
    {
        // 左边界差 2、上边界差 3
        var other = new Rect(502, 397, 100, 80);

        var result = NoteAlignGeometry.Compute(Dragged, new[] { other });

        Assert.True(result.AlignedX);
        Assert.True(result.AlignedY);
        Assert.Equal(2, result.Dx);
        Assert.Equal(-3, result.Dy);
    }

    // ===== 多便签竞争：只取最近 =====

    [Fact]
    public void Compute_MultipleCandidates_PicksClosest()
    {
        var far = new Rect(506, 900, 100, 80);   // 差 6
        var near = new Rect(501, 900, 100, 80);  // 差 1 ← 应选它

        var result = NoteAlignGeometry.Compute(Dragged, new[] { far, near });

        Assert.Equal(1, result.Dx);
        Assert.Equal(501, result.GuideLeft);
    }

    [Fact]
    public void Compute_CandidatesAcrossAxes_AreIndependent()
    {
        // X 轴由 A 提供最近候选（差 1），Y 轴由 B 提供最近候选（差 2）
        var a = new Rect(501, 9000, 100, 80);
        var b = new Rect(9000, 402, 100, 80);

        var result = NoteAlignGeometry.Compute(Dragged, new[] { a, b });

        Assert.True(result.AlignedX);
        Assert.True(result.AlignedY);
        Assert.Equal(1, result.Dx);
        Assert.Equal(2, result.Dy);
    }

    // ===== 无对齐 =====

    [Fact]
    public void Compute_NoNearbyNotes_ReturnsNone()
    {
        var other = new Rect(2000, 2000, 100, 80);

        var result = NoteAlignGeometry.Compute(Dragged, new[] { other });

        Assert.False(result.AlignedX);
        Assert.False(result.AlignedY);
        Assert.Equal(0, result.Dx);
        Assert.Equal(0, result.Dy);
    }

    [Fact]
    public void Compute_EmptyOthers_ReturnsNone()
    {
        var result = NoteAlignGeometry.Compute(Dragged, Array.Empty<Rect>());

        Assert.False(result.AlignedX);
        Assert.False(result.AlignedY);
        Assert.Equal(0, result.Dx);
    }

    [Fact]
    public void Compute_NullOthers_ReturnsNone()
    {
        var result = NoteAlignGeometry.Compute(Dragged, null!);

        Assert.False(result.AlignedX);
        Assert.False(result.AlignedY);
    }

    // ===== 自定义阈值 =====

    [Fact]
    public void Compute_CustomThreshold_IsRespected()
    {
        var other = new Rect(520, 900, 100, 80); // 差 20

        Assert.False(NoteAlignGeometry.Compute(Dragged, new[] { other }, 8).AlignedX);
        Assert.True(NoteAlignGeometry.Compute(Dragged, new[] { other }, 20).AlignedX);
    }

    // ===== 完全重合也要能吸住（差 0）=====

    [Fact]
    public void Compute_ExactlyAligned_SnapsWithZeroDelta()
    {
        var other = new Rect(500, 400, 100, 80);

        var result = NoteAlignGeometry.Compute(Dragged, new[] { other });

        Assert.True(result.AlignedX);
        Assert.True(result.AlignedY);
        Assert.Equal(0, result.Dx);
        Assert.Equal(0, result.Dy);
    }

    // ===== 折叠胶囊形态（矮矩形）=====
    //
    // 对齐本身与形态无关，只看矩形四条边。但胶囊是**极扁**的矩形
    // （约 200×32），是四条边里"上下边离得最近"的极端情况 ——
    // 上边与下边只隔 32，很容易出现上边差 5、下边差 27 这种
    // 「两个候选都进了阈值视野」的局面，必须确认挑的是更近的那个。

    /// <summary>折叠胶囊：200 宽、32 高，左上角在 (300, 200)。</summary>
    private static readonly Rect Capsule = new(300, 200, 200, 32);

    [Fact]
    public void Compute_Capsule_TopEdgeAlignsToOtherCapsule()
    {
        // 同样尺寸的两个胶囊（高都是 32），上边差 4、下边也差 4（平手）
        // → 取先遍历到的"上对上"，GuideTop 落在对方上边 204
        var other = new Rect(900, 204, 200, 32);

        var result = NoteAlignGeometry.Compute(Capsule, new[] { other });

        Assert.True(result.AlignedY);
        Assert.Equal(4, result.Dy);        // 往下挪 4 让上边齐平
        Assert.Equal(204, result.GuideTop);
    }

    [Fact]
    public void Compute_Capsule_PrefersNearerEdgeWhenTopAndBottomCompete()
    {
        // 对方是常规便签（高 80，上边 202、下边 282）：
        // 胶囊上边差 2、下边差 50 —— 胶囊上下边只隔 32，两个候选都会进循环，
        // 必须挑差 2 的那个（而不是因为"下边更靠下"就选它）
        var other = new Rect(900, 202, 100, 80);

        var result = NoteAlignGeometry.Compute(Capsule, new[] { other });

        Assert.True(result.AlignedY);
        Assert.Equal(2, result.Dy);        // 近的是上边，不能挑下边那个 50
        Assert.Equal(202, result.GuideTop);
    }

    [Fact]
    public void Compute_Capsule_LeftEdgeAlignsAgainstNormalNote()
    {
        // 胶囊左边界 300，常规便签左边界 303 → 差 3
        var other = new Rect(303, 800, 100, 80);

        var result = NoteAlignGeometry.Compute(Capsule, new[] { other });

        Assert.True(result.AlignedX);
        Assert.Equal(3, result.Dx);
        Assert.Equal(303, result.GuideLeft);
    }

    [Fact]
    public void Compute_CapsuleAgainstCapsule_AlignedByLeftEdgeOnly()
    {
        // 两个胶囊左边界差 5；纵向差 200（远超阈值）→ 只吸横向
        var other = new Rect(305, 700, 200, 32);

        var result = NoteAlignGeometry.Compute(Capsule, new[] { other });

        Assert.True(result.AlignedX);
        Assert.False(result.AlignedY);
        Assert.Equal(5, result.Dx);
    }
}
