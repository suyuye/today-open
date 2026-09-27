using System.Windows;

using NoteApp.Helpers;

using Xunit;
namespace NoteApp.Tests;

/// <summary>
/// 多显示器工作区解析。
///
/// 这里断言的是「结果自洽」而不是写死某个数字 —— 因为跑测试的机器显示器配置未知
/// （CI 可能单屏、开发机可能多屏）。要点是：
/// 1. 返回的矩形必须是**有效**的（非空、宽高为正、DIP 量级合理）；
/// 2. 传给「点」的方法必须返回**包含该点**的那块屏的工作区；
/// 3. 主屏兜底与「点落主屏」结果一致。
/// </summary>
public class MonitorHelperTests
{
    [Fact]
    public void PrimaryWorkArea_IsValidRect()
    {
        var area = MonitorHelper.PrimaryWorkArea();
        Assert.False(area.IsEmpty, "主屏工作区不应为空");
        Assert.True(area.Width > 100, $"主屏工作区宽度异常：{area.Width}");
        Assert.True(area.Height > 100, $"主屏工作区高度异常：{area.Height}");
    }

    [Fact]
    public void WorkArea_DipPointInsidePrimary_ContainsThatPoint()
    {
        var primary = MonitorHelper.PrimaryWorkArea();

        // 取主屏工作区中心作为查询点：结果必须包含它
        double cx = primary.Left + primary.Width / 2;
        double cy = primary.Top + primary.Height / 2;
        var area = MonitorHelper.WorkAreaForDipPoint(cx, cy);

        Assert.False(area.IsEmpty);
        Assert.True(cx >= area.Left && cx <= area.Right,
            $"点 x={cx} 应落在返回工作区 [{area.Left},{area.Right}] 内");
        Assert.True(cy >= area.Top && cy <= area.Bottom,
            $"点 y={cy} 应落在返回工作区 [{area.Top},{area.Bottom}] 内");
    }

    [Fact]
    public void WorkAreaForDipPoint_SameMonitorTwice_IsStable()
    {
        var primary = MonitorHelper.PrimaryWorkArea();
        double cx = primary.Left + primary.Width / 2;
        double cy = primary.Top + primary.Height / 2;

        var a = MonitorHelper.WorkAreaForDipPoint(cx, cy);
        var b = MonitorHelper.WorkAreaForDipPoint(cx, cy);
        Assert.Equal(a, b);
    }

    [Fact]
    public void WorkAreaPhysicalForPoint_PrimaryPoint_MatchesDipVariant()
    {
        // 物理点 (0,0) 与 DIP 点 (0,0) 都落在主屏。前者返回物理、后者返回 DIP，
        // 若两屏缩放为 1.0 则相等；无论如何都应是同一块屏 → 左/上边界一致。
        var phys = MonitorHelper.WorkAreaPhysicalForPoint(0, 0);
        var dip = MonitorHelper.WorkAreaForDipPoint(0, 0);
        Assert.Equal(phys.Left, dip.Left, 3);
        Assert.Equal(phys.Top, dip.Top, 3);
    }

    [Fact]
    public void WorkAreaPhysicalForPoint_FarPoint_StillReturnsSomeMonitor()
    {
        // 任意点都应命中某块屏（nearest 语义），不应返回空
        var area = MonitorHelper.WorkAreaPhysicalForPoint(100000, 100000);
        Assert.False(area.IsEmpty);
        Assert.True(area.Width > 100);
    }

    // ===== 真实外边界判定（GetOuterSides）=====

    // 说明：GetOuterSides 内部会去查「所有显示器并集」（GetSystemMetrics 虚拟屏边界），
    // 所以这些其实是**集成断言** —— 结果取决于跑测试的机器有几块屏、怎么摆。
    // 因此只断言「自洽性」与「与单/多屏无关的硬性质」，不写死具体边。

    [Fact]
    public void GetOuterSides_ResultOnlyUsesLegalBits()
    {
        // 返回值只能由四个合法位组成，不能出现未定义位。
        var bounds = MonitorHelper.GetMonitorBoundsPhysical(IntPtr.Zero);
        var sides = MonitorHelper.GetOuterSides(bounds);
        Assert.True((sides & DesktopSides.All) == sides, "返回值只能由四个合法位组成");
    }

    [Fact]
    public void GetOuterSides_LeftEdgeOnVirtualLeft_ImpliesLeftUsable()
    {
        // 硬性质：如果某屏显示区左边界恰好等于虚拟桌面左边界，
        // 那么它左侧一定没有别的屏 → 必须包含 Left 位。
        var bounds = MonitorHelper.GetMonitorBoundsPhysical(IntPtr.Zero);
        var virtualBounds = MonitorHelper.VirtualScreenBoundsPhysical();
        if (!virtualBounds.IsEmpty && Math.Abs(bounds.Left - virtualBounds.Left) <= 1.0)
        {
            var sides = MonitorHelper.GetOuterSides(bounds);
            Assert.True((sides & DesktopSides.Left) != 0,
                "左边界与虚拟屏左边界重合时，Left 必须被判定为真外边界");
        }
    }

    [Fact]
    public void VirtualScreenBounds_ContainsPrimaryMonitorBounds()
    {
        // 虚拟桌面并集必须**包住**主屏显示区（这是判定接缝的地基）。
        var virtualBounds = MonitorHelper.VirtualScreenBoundsPhysical();
        Assert.False(virtualBounds.IsEmpty, "虚拟屏边界不应为空");

        var primaryBounds = MonitorHelper.GetMonitorBoundsPhysical(IntPtr.Zero);
        Assert.True(primaryBounds.Left >= virtualBounds.Left - 1, "并集应包住主屏左边界");
        Assert.True(primaryBounds.Right <= virtualBounds.Right + 1, "并集应包住主屏右边界");
        Assert.True(primaryBounds.Top >= virtualBounds.Top - 1, "并集应包住主屏上边界");
        Assert.True(primaryBounds.Bottom <= virtualBounds.Bottom + 1, "并集应包住主屏下边界");
    }

    [Fact]
    public void GetOuterSides_SyntheticSandwich_GatesSeamSides()
    {
        // 纯几何验证（不依赖本机屏数）：用真实的并集边界构造两个合成矩形来断言门控逻辑。
        var virtualBounds = MonitorHelper.VirtualScreenBoundsPhysical();
        if (virtualBounds.IsEmpty || virtualBounds.Width < 100)
        {
            return; // 拿不到并集信息时跳过（环境异常）
        }

        // 「左右都内缩 10px」→ 左右是接缝，不应被判为真外边界
        var sandwiched = new Rect(
            virtualBounds.Left + 10, virtualBounds.Top,
            Math.Max(1, virtualBounds.Width - 20), virtualBounds.Height);
        var sides = MonitorHelper.GetOuterSides(sandwiched);
        Assert.True((sides & DesktopSides.Left) == 0, "左边界内缩 10px 时不应被判定为真外边界");
        Assert.True((sides & DesktopSides.Right) == 0, "右边界内缩 10px 时不应被判定为真外边界");

        // 「完全贴住并集边界」→ 四边都是真外边界
        var full = new Rect(virtualBounds.Left, virtualBounds.Top, virtualBounds.Width, virtualBounds.Height);
        Assert.Equal(DesktopSides.All, MonitorHelper.GetOuterSides(full));
    }
}
