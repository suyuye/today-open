using System.Windows;

using NoteApp.Helpers;
using NoteApp.Models.Enums;

using Xunit;

namespace NoteApp.Tests;

/// <summary>
/// 贴边自动隐藏的坐标算术。
///
/// 为什么这块值得写一堆断言：算错的后果是「便签贴边后整条消失、再也点不到」，
/// 而界面上完全看不出是算术问题 —— 用户只会以为软件把便签弄丢了。
/// 这里把各个边界（超宽便签、窄屏、负坐标工作区、阴影边距）都钉死。
/// </summary>
public class DockGeometryTests
{
    /// <summary>1920×1080、无任务栏偏移的标准工作区。</summary>
    private static readonly Rect WorkArea = new(0, 0, 1920, 1080);

    /// <summary>带 12px 阴影边距的窗口宽度：可见内容 220 + 左右各 12。</summary>
    private const double WindowWidth = 244;
    private const double ShadowMargin = 12;

    // ===== DetectEdge =====

    [Fact]
    public void DetectEdge_LeftSiblingAttached_ReturnsLeft()
    {
        // 窗口左边界就在工作区左边界上
        var edge = DockGeometry.DetectEdge(0, WindowWidth, WorkArea, ShadowMargin);
        Assert.Equal(DockEdge.Left, edge);
    }

    [Fact]
    public void DetectEdge_WithinSnapDistanceOnLeft_ReturnsLeft()
    {
        // 可见左边界 = left + margin。要让它落在 SnapDistance 内，left 需 <= -12 + SnapDistance
        var left = DockGeometry.SnapDistance - ShadowMargin - 1;
        var edge = DockGeometry.DetectEdge(left, WindowWidth, WorkArea, ShadowMargin);
        Assert.Equal(DockEdge.Left, edge);
    }

    [Fact]
    public void DetectEdge_ShadowMarginPushedInside_ReturnsNone()
    {
        // 窗口外框已经贴住屏幕左边（left = 0），但可见内容还在 12px 处。
        // 12 <= SnapDistance 仍算贴边（这正是"轻微吸附"的来源：窗口本没贴边，
        // 但可见内容已经够近了）；用一个明确越过阈值的距离验证不贴。
        var left = DockGeometry.SnapDistance - ShadowMargin + 10;
        var edge = DockGeometry.DetectEdge(left, WindowWidth, WorkArea, ShadowMargin);
        Assert.Equal(DockEdge.None, edge);
    }

    [Fact]
    public void DetectEdge_JustOutsideSnapDistanceOnLeft_ReturnsNone()
    {
        var left = DockGeometry.SnapDistance - ShadowMargin + 1;
        var edge = DockGeometry.DetectEdge(left, WindowWidth, WorkArea, ShadowMargin);
        Assert.Equal(DockEdge.None, edge);
    }

    [Fact]
    public void DetectEdge_SnapDistance_IsGenerousEnoughForHandFeel()
    {
        // 手感回归护栏：阈值必须显著大于阴影边距。
        // 否则用户把便签拖到"看起来刚贴住屏幕边"（left ≈ -margin）之后，
        // 再往里拖一点窗口就跑出屏幕了，可用的吸附区间被压得太窄，
        // 主观感受就是"怎么都找不到那个刚刚好的距离"。
        Assert.True(DockGeometry.SnapDistance >= 24,
            $"SnapDistance={DockGeometry.SnapDistance} 太小，贴边会很难触发");
    }

    [Fact]
    public void DetectEdge_WithExplicitThreshold_RespectsIt()
    {
        // 拖动预览用的宽阈值：同一位置在"松手判定"下不贴，在"预览"下应当贴
        double left = DockGeometry.SnapDistance - ShadowMargin + 6;

        Assert.Equal(DockEdge.None,
            DockGeometry.DetectEdge(left, WindowWidth, WorkArea, ShadowMargin, DockGeometry.SnapDistance));

        Assert.Equal(DockEdge.Left,
            DockGeometry.DetectEdge(left, WindowWidth, WorkArea, ShadowMargin, DockGeometry.SnapPreviewDistance));
    }

    [Fact]
    public void DetectEdge_BothEdgesClose_PicksNearerOne()
    {
        // 窗口比屏幕还宽时，两侧都会落在阈值内。必须选**更近**的一侧，
        // 而不是固定偏左 —— 否则"想贴右边结果跳到左边"。
        // 工作区 0..1920，窗口 2100 宽、无阴影：
        //   left = -120 → 可见左界 -120（距左 -120），可见右界 1980（距右 -60）
        //   -120 < -60 → 左侧更近 → Left
        var edge = DockGeometry.DetectEdge(-120, 2100, WorkArea, 0);
        Assert.Equal(DockEdge.Left, edge);
    }

    [Fact]
    public void DetectEdge_BothEdgesClose_PicksRightWhenRightNearer()
    {
        // 同宽度，位置偏右：left = -60 → 左距 -60，右距 -120 → 右侧更近 → Right
        var edge = DockGeometry.DetectEdge(-60, 2100, WorkArea, 0);
        Assert.Equal(DockEdge.Right, edge);
    }

    [Fact]
    public void DetectEdge_RightSiblingAttached_ReturnsRight()
    {
        // 右边界落在工作区右边界：left = 1920 - 244 = 1676
        var edge = DockGeometry.DetectEdge(1676, WindowWidth, WorkArea, ShadowMargin);
        Assert.Equal(DockEdge.Right, edge);
    }

    [Fact]
    public void DetectEdge_WithinSnapDistanceOnRight_ReturnsRight()
    {
        // 可见右边界距屏幕右边缘 8 < 16 → 贴右边
        var edge = DockGeometry.DetectEdge(1920 - 244 + 4, WindowWidth, WorkArea, ShadowMargin);
        Assert.Equal(DockEdge.Right, edge);
    }

    [Fact]
    public void DetectEdge_InMiddle_ReturnsNone()
    {
        var edge = DockGeometry.DetectEdge(800, WindowWidth, WorkArea, ShadowMargin);
        Assert.Equal(DockEdge.None, edge);
    }

    [Fact]
    public void DetectEdge_WorkAreaOffsetByTaskbar_StillDetects()
    {
        // 工作区不在 (0,0)：左侧有 0px、底部有任务栏
        var shifted = new Rect(0, 0, 1920, 1040);
        var edge = DockGeometry.DetectEdge(4, WindowWidth, shifted, ShadowMargin);
        Assert.Equal(DockEdge.Left, edge);
    }

    [Fact]
    public void DetectEdge_DualMonitorNegativeWorkArea_DetectsLeft()
    {
        // 副屏在工作区左侧，坐标为负 —— 不能假设工作区 Left 是 0
        var leftMonitor = new Rect(-1920, 0, 1920, 1080);
        var edge = DockGeometry.DetectEdge(-1920, WindowWidth, leftMonitor, ShadowMargin);
        Assert.Equal(DockEdge.Left, edge);
    }

    // ===== ExpandedLeft =====

    [Fact]
    public void ExpandedLeft_Left_AlignsVisibleContentToScreenEdge()
    {
        // 阴影是透明的，所以窗口 Left 要再往左让掉一个 shadowMargin
        var left = DockGeometry.ExpandedLeft(DockEdge.Left, WindowWidth, WorkArea, ShadowMargin);
        Assert.Equal(-ShadowMargin, left, 3);
    }

    [Fact]
    public void ExpandedLeft_Right_AlignsVisibleContentToScreenRight()
    {
        var left = DockGeometry.ExpandedLeft(DockEdge.Right, WindowWidth, WorkArea, ShadowMargin);
        // 可见右边界 = left + width - margin 应当正好等于 1920
        Assert.Equal(1920, left + WindowWidth - ShadowMargin, 3);
    }

    [Fact]
    public void ExpandedLeft_None_FallsBackToWorkAreaLeft()
    {
        var left = DockGeometry.ExpandedLeft(DockEdge.None, WindowWidth, WorkArea, ShadowMargin);
        Assert.Equal(-ShadowMargin, left, 3);
    }

    // ===== CollapsedLeft —— 最关键的一组：绝不能让便签完全消失 =====

    [Fact]
    public void CollapsedLeft_Left_LeavesVisibleStripOnScreen()
    {
        var area = WorkArea;
        var left = DockGeometry.CollapsedLeft(DockEdge.Left, WindowWidth, area, ShadowMargin);

        // 可见右边界必须比屏幕左边缘**大** VisibleStripWidth，也就是能看见一条
        double visibleRight = left + WindowWidth - ShadowMargin;
        Assert.Equal(area.Left + DockGeometry.VisibleStripWidth, visibleRight, 3);
        Assert.True(visibleRight > area.Left, "收起后必须仍在屏幕内留下可见条");
    }

    [Fact]
    public void CollapsedLeft_Right_LeavesVisibleStripOnScreen()
    {
        var area = WorkArea;
        var left = DockGeometry.CollapsedLeft(DockEdge.Right, WindowWidth, area, ShadowMargin);

        // 可见左边界必须比屏幕右边缘**小** VisibleStripWidth
        double visibleLeft = left + ShadowMargin;
        Assert.Equal(area.Right - DockGeometry.VisibleStripWidth, visibleLeft, 3);
        Assert.True(visibleLeft < area.Right, "收起后必须仍在屏幕内留下可见条");
    }

    [Fact]
    public void CollapsedLeft_IsAlwaysFurtherOutThanExpanded()
    {
        // 收起态一定比展开态更靠外（更小的 Left 对左贴边 / 更大的 Left 对右贴边）
        foreach (var edge in new[] { DockEdge.Left, DockEdge.Right })
        {
            var expanded = DockGeometry.ExpandedLeft(edge, WindowWidth, WorkArea, ShadowMargin);
            var collapsed = DockGeometry.CollapsedLeft(edge, WindowWidth, WorkArea, ShadowMargin);

            if (edge == DockEdge.Left)
            {
                Assert.True(collapsed < expanded, "左贴边收起后应更靠左");
            }
            else
            {
                Assert.True(collapsed > expanded, "右贴边收起后应更靠右");
            }
        }
    }

    [Fact]
    public void CollapsedLeft_VeryWideWindow_StillLeavesStrip()
    {
        // 便签比屏幕还宽：这是最容易把窗口算出屏幕外的情形
        const double wide = 3000;
        var left = DockGeometry.CollapsedLeft(DockEdge.Left, wide, WorkArea, ShadowMargin);
        double visibleRight = left + wide - ShadowMargin;
        Assert.Equal(WorkArea.Left + DockGeometry.VisibleStripWidth, visibleRight, 3);
    }

    [Fact]
    public void CollapsedLeft_ZeroMargin_VisibleRightEdgeIsTheStrip()
    {
        // 无阴影边距时窗口外框 == 可见区域，所以可见右边界 = left + width
        var left = DockGeometry.CollapsedLeft(DockEdge.Left, 200, WorkArea, 0);
        Assert.Equal(DockGeometry.VisibleStripWidth, left + 200, 3);
    }

    // ===== IsInAwakenZone =====

    [Fact]
    public void IsInAwakenZone_Left_PointerAtScreenEdge_ReturnsTrue()
    {
        Assert.True(DockGeometry.IsInAwakenZone(DockEdge.Left, WorkArea, 0));
    }

    [Fact]
    public void IsInAwakenZone_Left_PointerInsideTolerance_ReturnsTrue()
    {
        // 唤醒带宽度 = VisibleStripWidth + AwakenTolerance，边界值本身算在内
        var edgeOfZone = DockGeometry.VisibleStripWidth + DockGeometry.AwakenTolerance;
        Assert.True(DockGeometry.IsInAwakenZone(DockEdge.Left, WorkArea, edgeOfZone));
    }

    [Fact]
    public void IsInAwakenZone_Left_PointerOutsideTolerance_ReturnsFalse()
    {
        var justOutside = DockGeometry.VisibleStripWidth + DockGeometry.AwakenTolerance + 1;
        Assert.False(DockGeometry.IsInAwakenZone(DockEdge.Left, WorkArea, justOutside));
    }

    [Fact]
    public void IsInAwakenZone_Tolerance_IsWideEnoughForHandFeel()
    {
        // 手感回归护栏：收起后只剩 5px 可见条，容错区太窄会让用户"要很精准地怼上去"。
        // 早期取 20（横向总容错 25px），实测在 150% 缩放下只有约 17 物理像素，反馈偏难。
        Assert.True(DockGeometry.AwakenTolerance >= 30,
            $"AwakenTolerance={DockGeometry.AwakenTolerance} 太小，收起后很难唤出");
    }

    [Fact]
    public void IsInAwakenZone_Left_PointerOffScreenToLeft_ReturnsTrue()
    {
        // 多显示器时指针可能在负坐标区，仍应视为「在左边缘附近」
        Assert.True(DockGeometry.IsInAwakenZone(DockEdge.Left, WorkArea, -500));
    }

    [Fact]
    public void IsInAwakenZone_Right_PointerAtScreenRight_ReturnsTrue()
    {
        Assert.True(DockGeometry.IsInAwakenZone(DockEdge.Right, WorkArea, 1920));
    }

    [Fact]
    public void IsInAwakenZone_Right_PointerJustInsideTolerance_ReturnsTrue()
    {
        var edgeOfZone = 1920 - DockGeometry.VisibleStripWidth - DockGeometry.AwakenTolerance;
        Assert.True(DockGeometry.IsInAwakenZone(DockEdge.Right, WorkArea, edgeOfZone));
    }

    [Fact]
    public void IsInAwakenZone_Right_PointerOutsideTolerance_ReturnsFalse()
    {
        var justOutside = 1920 - DockGeometry.VisibleStripWidth - DockGeometry.AwakenTolerance - 1;
        Assert.False(DockGeometry.IsInAwakenZone(DockEdge.Right, WorkArea, justOutside));
    }

    [Fact]
    public void IsInAwakenZone_None_AlwaysFalse()
    {
        // 没贴边就不该有任何唤醒带（否则屏边到处都能触发）
        Assert.False(DockGeometry.IsInAwakenZone(DockEdge.None, WorkArea, 0));
        Assert.False(DockGeometry.IsInAwakenZone(DockEdge.None, WorkArea, 1919));
    }

    // ===== ClampIntoWorkArea =====

    [Fact]
    public void Clamp_InsideWorkArea_Unchanged()
    {
        var (left, top) = DockGeometry.ClampIntoWorkArea(500, 300, WindowWidth, 200, WorkArea, ShadowMargin);
        Assert.Equal(500, left, 3);
        Assert.Equal(300, top, 3);
    }

    [Fact]
    public void Clamp_FullyOffLeft_PullsBackToLeaveVisibleStrip()
    {
        // 换显示器后旧坐标可能完全在工作区外
        var (left, _) = DockGeometry.ClampIntoWorkArea(-5000, 100, WindowWidth, 200, WorkArea, ShadowMargin);
        // 可见右边界至少露出 minVisible = 10px
        double visibleRight = left + WindowWidth - ShadowMargin;
        Assert.True(visibleRight >= WorkArea.Left + DockGeometry.VisibleStripWidth, "必须至少露出可见条");
    }

    [Fact]
    public void Clamp_FullyOffRight_PullsBackToLeaveVisibleStrip()
    {
        var (left, _) = DockGeometry.ClampIntoWorkArea(9000, 100, WindowWidth, 200, WorkArea, ShadowMargin);
        // 可见左边界（left + margin）至多到 Right - minVisible(10)
        double visibleLeft = left + ShadowMargin;
        Assert.True(visibleLeft <= WorkArea.Right - DockGeometry.VisibleStripWidth,
            $"可见左边界 {visibleLeft} 应 <= {WorkArea.Right - DockGeometry.VisibleStripWidth}");
    }

    [Fact]
    public void Clamp_FullyOffRight_ActuallyLandsAtTheBoundary()
    {
        var (left, _) = DockGeometry.ClampIntoWorkArea(9000, 100, WindowWidth, 200, WorkArea, ShadowMargin);
        // minVisible = min(10, 220) = 10；可见左边界应正好落在 1910
        Assert.Equal(WorkArea.Right - 10, left + ShadowMargin, 3);
    }

    [Fact]
    public void Clamp_AboveWorkArea_PullsDownToTop()
    {
        var (_, top) = DockGeometry.ClampIntoWorkArea(100, -400, WindowWidth, 200, WorkArea, ShadowMargin);
        Assert.Equal(WorkArea.Top - ShadowMargin, top, 3);
    }

    [Fact]
    public void Clamp_BelowWorkArea_PullsUpToBottom()
    {
        var (_, top) = DockGeometry.ClampIntoWorkArea(100, 5000, WindowWidth, 200, WorkArea, ShadowMargin);
        Assert.True(top < WorkArea.Bottom, "必须至少露出可见条");
    }

    [Fact]
    public void Clamp_CollapsedPosition_DoesNotShoveWindowBackInside()
    {
        // 关键：收起态的坐标本身就"在屏幕外一部分"，clamp 不能把它推回屏幕内，
        // 否则**每次关闭再打开便签都会往屏幕内挪 5px**，开关几次就自己滑出来。
        // 所以这里必须显式传 VisibleStripWidth 作为最小可见宽度。
        var collapsed = DockGeometry.CollapsedLeft(DockEdge.Left, WindowWidth, WorkArea, ShadowMargin);
        var (left, _) = DockGeometry.ClampIntoWorkArea(
            collapsed, 100, WindowWidth, 200, WorkArea, ShadowMargin, DockGeometry.VisibleStripWidth);
        Assert.Equal(collapsed, left, 3);
    }

    [Fact]
    public void Clamp_DefaultMinVisible_IsWiderThanDockStrip()
    {
        // 默认（非贴边）摆位要求露出 10px，比贴边可见条 5px 宽 ——
        // 这样"拖到边缘但没贴边"的便签不会被允许只露 5px
        var collapsed = DockGeometry.CollapsedLeft(DockEdge.Left, WindowWidth, WorkArea, ShadowMargin);
        var (left, _) = DockGeometry.ClampIntoWorkArea(collapsed, 100, WindowWidth, 200, WorkArea, ShadowMargin);
        double visibleRight = left + WindowWidth - ShadowMargin;
        Assert.Equal(WorkArea.Left + DockGeometry.VisibleStripWidth * 2, visibleRight, 3);
    }

    [Fact]
    public void Clamp_NarrowVisibleWidth_DoesNotThrow()
    {
        // 极窄窗口（宽度小于两倍阴影边距）不能让 visibleWidth 变成负数
        var (left, top) = DockGeometry.ClampIntoWorkArea(2000, 2000, 10, 10, WorkArea, ShadowMargin);
        Assert.False(double.IsNaN(left));
        Assert.False(double.IsNaN(top));
    }

    // ===== 多显示器 A：工作区必须取「窗口所在屏」的那一个 =====

    /// <summary>
    /// 右侧副屏的工作区（DIP）。桌面整体：主屏 [0,2560]，副屏 [3840,6400]。
    /// 副屏左边界 3840 ≠ 0，正是「主屏工作区不能代表副屏」的关键。
    /// </summary>
    private static readonly Rect SecondaryWorkArea = new(3840, 432, 2560, 1392);

    /// <summary>右侧副屏：左外侧连着主屏 → 只有右/上/下是真外边界。</summary>
    private const DesktopSides SecondaryOnRightOuter = DesktopSides.Right | DesktopSides.Top | DesktopSides.Bottom;

    /// <summary>主屏（左）：右外侧连着副屏 → 只有左/上/下是真外边界。</summary>
    private const DesktopSides PrimaryWithRightNeighborOuter = DesktopSides.Left | DesktopSides.Top | DesktopSides.Bottom;

    [Fact]
    public void RightDock_OnSecondaryMonitor_AlignsToSecondaryRightEdge()
    {
        // 上一轮用户上报的 bug：右侧接了副屏后，副屏上右贴边的便签跳到了主屏右缘
        // （桌面中央偏右）。根因是调用方传了主屏工作区而非窗口所在屏的工作区。
        double left = DockGeometry.ExpandedLeft(
            DockEdge.Right, WindowWidth, SecondaryWorkArea, ShadowMargin);

        double visibleRight = left + WindowWidth - ShadowMargin;
        Assert.Equal(SecondaryWorkArea.Right, visibleRight, 3);
        Assert.True(visibleRight > 2560, "必须落在主屏右边界之外——否则就是重现了那个 bug");
    }

    [Fact]
    public void RightDock_WrongWorkArea_LandsAtPrimaryEdge_Repro()
    {
        // 反证：若误用主屏工作区（Right=2560），右贴边会落在 2560 附近 ——
        // 即桌面中央偏右。把这个错误结果钉下来，避免将来又有人"顺手"改回主屏工作区。
        var primary = new Rect(0, 0, 2560, 1392);
        double left = DockGeometry.ExpandedLeft(DockEdge.Right, WindowWidth, primary, ShadowMargin);
        Assert.Equal(primary.Right, left + WindowWidth - ShadowMargin, 3);
        Assert.True(left < SecondaryWorkArea.Left, "误用主屏工作区时会落在副屏左边界左边（即桌面中部）");
    }

    [Fact]
    public void RightDockCollapsed_OnSecondaryMonitor_LeavesStripAtSecondaryEdge()
    {
        double left = DockGeometry.CollapsedLeft(
            DockEdge.Right, WindowWidth, SecondaryWorkArea, ShadowMargin);

        // 可见左边界 = left + edgeMargin，应贴在副屏右缘内侧 5px
        double visibleLeft = left + ShadowMargin;
        Assert.Equal(SecondaryWorkArea.Right - DockGeometry.VisibleStripWidth, visibleLeft, 3);
    }

    [Fact]
    public void DetectEdge_OnSecondaryRightSide_ReturnsRight_NotNone()
    {
        // 副屏右缘是真外边界 → 贴右
        double left = SecondaryWorkArea.Right - WindowWidth + ShadowMargin;
        var edge = DockGeometry.DetectEdge(left, WindowWidth, SecondaryWorkArea, ShadowMargin,
            DockGeometry.SnapDistance, SecondaryOnRightOuter);
        Assert.Equal(DockEdge.Right, edge);
    }

    [Fact]
    public void AwakenZone_OnSecondaryRightEdge_UsesSecondaryBounds()
    {
        // 收起在副屏右缘时，唤醒带应贴在 6400 附近，而不是主屏的 2560
        Assert.True(DockGeometry.IsInAwakenZone(DockEdge.Right, SecondaryWorkArea, 6390));
        Assert.False(DockGeometry.IsInAwakenZone(DockEdge.Right, SecondaryWorkArea, 2560));
    }

    // ===== 多显示器 B：接缝不是可贴边的边缘（本轮修复） =====

    [Fact]
    public void DetectEdge_PrimaryRightEdge_IsSeam_ReturnsNone()
    {
        // 用户本轮上报：主屏**右侧**（= 与右副屏的接缝）吸附时便签会跳走。
        // 用主屏工作区（Right=2560）判定，窗口贴其右缘；但主屏右侧有副屏 → 不是真外边界，
        // 必须返回 None，让便签停在原地，而不是被对齐/收进那条看不见的缝里。
        var primary = new Rect(0, 0, 2560, 1392);
        double left = primary.Right - WindowWidth + ShadowMargin;

        var edge = DockGeometry.DetectEdge(left, WindowWidth, primary, ShadowMargin,
            DockGeometry.SnapDistance, PrimaryWithRightNeighborOuter);

        Assert.Equal(DockEdge.None, edge);
    }

    [Fact]
    public void DetectEdge_SecondaryLeftEdge_IsSeam_ReturnsNone()
    {
        // 同一缝的另一侧：副屏左缘（= 与主屏的接缝），同样不是真外边界 → None
        double left = SecondaryWorkArea.Left - ShadowMargin;

        var edge = DockGeometry.DetectEdge(left, WindowWidth, SecondaryWorkArea, ShadowMargin,
            DockGeometry.SnapDistance, SecondaryOnRightOuter);

        Assert.Equal(DockEdge.None, edge);
    }

    [Fact]
    public void DetectEdge_PrimaryLeftEdge_IsRealBoundary_StillReturnsLeft()
    {
        // 对照：主屏左缘是真外边界（左侧无屏）→ 仍应正常贴左，
        // 不能因为「引入了接缝判定」而把真正的边缘也一起禁掉。
        var primary = new Rect(0, 0, 2560, 1392);
        double left = primary.Left - ShadowMargin;

        var edge = DockGeometry.DetectEdge(left, WindowWidth, primary, ShadowMargin,
            DockGeometry.SnapDistance, PrimaryWithRightNeighborOuter);

        Assert.Equal(DockEdge.Left, edge);
    }

    [Fact]
    public void DetectEdge_SecondaryRightEdge_IsRealBoundary_StillReturnsRight()
    {
        // 对照：副屏右缘是真外边界（右侧无屏）→ 仍应正常贴右
        double left = SecondaryWorkArea.Right - WindowWidth + ShadowMargin;

        var edge = DockGeometry.DetectEdge(left, WindowWidth, SecondaryWorkArea, ShadowMargin,
            DockGeometry.SnapDistance, SecondaryOnRightOuter);

        Assert.Equal(DockEdge.Right, edge);
    }

    [Fact]
    public void DetectEdge_SeamSideOnly_WhenNearSeam_ReturnsNone_EvenIfOtherSideReal()
    {
        // 便签在副屏上、贴近**左缘（接缝）**，同时并不靠近右缘。
        // 左侧因是接缝被禁、右侧太远 → None。确认"禁掉接缝"不会意外接到远处的另一边。
        double left = SecondaryWorkArea.Left - ShadowMargin + 2;

        var edge = DockGeometry.DetectEdge(left, WindowWidth, SecondaryWorkArea, ShadowMargin,
            DockGeometry.SnapDistance, SecondaryOnRightOuter);

        Assert.Equal(DockEdge.None, edge);
    }

    // ===== 多显示器 C：副屏在左 / 在上 / 在下（布局无关性） =====

    [Fact]
    public void FullScreen_WithLeftNeighbor_LeftEdgeIsSeam_RightEdgeIsReal()
    {
        // 副屏在主屏**左侧**（另一台机器常见布局）。
        // 主屏工作区 DIP [0,2560]；左副屏在 [-1920,0] → 桌面左边界是 -1920。
        var mainArea = new Rect(0, 0, 2560, 1392);
        const DesktopSides outer = DesktopSides.Right | DesktopSides.Top | DesktopSides.Bottom; // 左侧有屏

        // 主屏左缘 = 接缝 → None
        var leftAtSeam = DockGeometry.DetectEdge(
            mainArea.Left - ShadowMargin, WindowWidth, mainArea, ShadowMargin,
            DockGeometry.SnapDistance, outer);
        Assert.Equal(DockEdge.None, leftAtSeam);

        // 主屏右缘 = 真外边界 → Right
        var rightAtEdge = DockGeometry.DetectEdge(
            mainArea.Right - WindowWidth + ShadowMargin, WindowWidth, mainArea, ShadowMargin,
            DockGeometry.SnapDistance, outer);
        Assert.Equal(DockEdge.Right, rightAtEdge);
    }

    [Fact]
    public void FullScreen_WithTopNeighbor_BothVerticalEdgesStillUsableForHorizontalDock()
    {
        // 副屏在主屏**上方**：上下是接缝，但**左右仍是真外边界** ——
        // 当前贴边只做左右，所以左右必须照常可用（这正是"布局无关"的意义）。
        var area = new Rect(0, 432, 2560, 1000);
        const DesktopSides outer = DesktopSides.Left | DesktopSides.Right; // 上下有屏

        // 左缘：真边界 → Left
        Assert.Equal(DockEdge.Left, DockGeometry.DetectEdge(
            area.Left - ShadowMargin, WindowWidth, area, ShadowMargin,
            DockGeometry.SnapDistance, outer));

        // 右缘：真边界 → Right
        Assert.Equal(DockEdge.Right, DockGeometry.DetectEdge(
            area.Right - WindowWidth + ShadowMargin, WindowWidth, area, ShadowMargin,
            DockGeometry.SnapDistance, outer));
    }

    [Fact]
    public void MiddleMonitor_FourScreensAround_NoHorizontalEdgeUsable()
    {
        // 极端：屏幕被左右包夹（左右都是接缝）→ 左右都不能贴，返回 None。
        var area = new Rect(2560, 0, 2560, 1392);
        const DesktopSides outer = DesktopSides.Top | DesktopSides.Bottom; // 左右都有屏

        Assert.Equal(DockEdge.None, DockGeometry.DetectEdge(
            area.Left - ShadowMargin, WindowWidth, area, ShadowMargin,
            DockGeometry.SnapDistance, outer));
        Assert.Equal(DockEdge.None, DockGeometry.DetectEdge(
            area.Right - WindowWidth + ShadowMargin, WindowWidth, area, ShadowMargin,
            DockGeometry.SnapDistance, outer));
    }

    [Fact]
    public void SingleMonitor_AllSidesOuter_BehavesExactlyAsBefore()
    {
        // 单屏机器：DesktopSides.All → 行为必须与引入接缝判定之前**完全一致**。
        var leftAtLeft = DockGeometry.DetectEdge(0, WindowWidth, WorkArea, ShadowMargin,
            DockGeometry.SnapDistance, DesktopSides.All);
        Assert.Equal(DockEdge.Left, leftAtLeft);

        var rightAtRight = DockGeometry.DetectEdge(
            WorkArea.Right - WindowWidth + ShadowMargin, WindowWidth, WorkArea, ShadowMargin,
            DockGeometry.SnapDistance, DesktopSides.All);
        Assert.Equal(DockEdge.Right, rightAtRight);
    }

    [Fact]
    public void DefaultOverload_WithoutSides_KeepsLegacyBehavior()
    {
        // 不带 sides 的旧重载默认 DesktopSides.All，保证既有调用方/测试语义不变。
        var edge = DockGeometry.DetectEdge(0, WindowWidth, WorkArea, ShadowMargin);
        Assert.Equal(DockEdge.Left, edge);
    }
}
