using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using NoteApp.Helpers;
using NoteApp.Models.Enums;
using NoteApp.Views;

using Xunit;

namespace NoteApp.Tests;

/// <summary>
/// <see cref="DockController"/> 的进程内行为测试。
///
/// 为什么需要它：贴边功能真正的风险点在**窗口坐标与动画**上，
/// 而本机沙箱里合成鼠标事件进不了 WPF 的输入管线（实测 <c>SendInput</c> 的
/// 左键按下完全触发不到 TitleBar 的处理器，只 MouseEnter/Leave 能进），
/// 所以无法从外部驱动拖动来完成端到端验证。
///
/// 这里改为**直接实例化真实的 Window + DockController**，用真实的 Dispatcher
/// 跑真实的定时器与动画，只把「拖动」这一步替换成手动调用 OnDragFinished()。
/// 这样除数构造出来的鼠标输入之外，其余路径全是真实的。
///
/// 注意：这些用例需要 STA 线程与 Dispatcher。xUnit 默认在 MTA 上跑测试，
/// 所以每条用例内部自己起一个 STA 线程 + Dispatcher 循环。
/// </summary>
public class DockControllerTests
{
    /// <summary>1920×1080 工作区（DIP）。</summary>
    private static readonly Rect WorkArea = new(0, 0, 1920, 1080);

    /// <summary>在 STA 线程 + 已启动的 Dispatcher 上跑一段逻辑，返回其结果。</summary>
    private static T RunOnDispatcher<T>(Func<T> body, TimeSpan timeout)
    {
        T result = default!;
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = body();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        if (!thread.Join(timeout))
        {
            throw new TimeoutException($"测试体在 {timeout.TotalSeconds}s 内未完成（dispatcher 可能被卡住）");
        }

        if (error is not null)
        {
            throw error;
        }

        return result;
    }

    /// <summary>
    /// 建一个真实显示的 Window。
    ///
    /// **必须真的 Show()**：`DoubleAnimation` 由渲染管线（独立于 UI 线程的媒体线程）
    /// 驱动时钟，未显示的窗口没有 HwndSource 与合成目标，动画永远不推进 ——
    /// 表现是 `IsCollapsed` 变成 true 而 `window.Left` 一点没动，很容易被误判成
    /// 「收起逻辑没生效」。用 `ShowActivated=false` + 全透明 + 定位到工作区右上角，
    /// 尽量不干扰真实桌面。
    /// </summary>
    private static (Window Window, DockController Controller, List<DockEdge> Edges) MakeController(
        double left, double width, bool enabled = true, double edgeMargin = 0,
        Rect? workArea = null, DesktopSides outerSides = DesktopSides.All)
    {
        var area = workArea ?? WorkArea;
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            Opacity = 0.01,
            Width = width,
            Height = 120,
            Left = left,
            Top = area.Top,
            Content = new Border(),
        };
        window.Show();

        var edges = new List<DockEdge>();
        var controller = new DockController(
            window,
            isEnabled: () => enabled,
            isBusy: () => false,
            workArea: () => area,
            onEdgeChanged: e => edges.Add(e),
            edgeMargin: edgeMargin,
            outerSides: () => outerSides);

        return (window, controller, edges);
    }

    /// <summary>
    /// 推进 Dispatcher 与渲染管线 <paramref name="ms"/> 毫秒。
    ///
    /// 用 `DispatcherPriority.Render` 的定时器来退帧：动画时钟挂在渲染优先级上，
    /// 若只在 Background 优先级退帧，动画可能一帧都推不动。
    /// </summary>
    private static void Pump(Dispatcher dispatcher, int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Render, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(ms),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    // ===== 拖动结束后的贴边判定 =====

    [Fact]
    public void OnDragFinished_NearLeftEdge_SnapsAndReportsLeft()
    {
        var result = RunOnDispatcher(() =>
        {
            var (window, controller, edges) = MakeController(left: 4, width: 200);
            controller.OnDragFinished();
            var edge = controller.Edge;
            var alignedLeft = window.Left;
            var recorded = edges.ToList();
            controller.Dispose();
            window.Close();
            return (Edge: edge, AlignedLeft: alignedLeft, Edges: recorded);
        }, TimeSpan.FromSeconds(10));

        Assert.Equal(DockEdge.Left, result.Edge);
        // 可见左边界对齐工作区左侧（无阴影边距时左边界即 0）
        Assert.Equal(WorkArea.Left, result.AlignedLeft, 1);
        Assert.Contains(DockEdge.Left, result.Edges);
    }

    [Fact]
    public void OnDragFinished_NearRightEdge_ReportsRight()
    {
        var result = RunOnDispatcher(() =>
        {
            var (window, controller, edges) = MakeController(left: WorkArea.Right - 200 + 3, width: 200);
            controller.OnDragFinished();
            var edge = controller.Edge;
            var recorded = edges.ToList();
            controller.Dispose();
            window.Close();
            return (Edge: edge, Edges: recorded);
        }, TimeSpan.FromSeconds(10));

        Assert.Equal(DockEdge.Right, result.Edge);
        Assert.Contains(DockEdge.Right, result.Edges);
    }

    [Fact]
    public void OnDragFinished_InMiddle_ReportsNone()
    {
        var result = RunOnDispatcher(() =>
        {
            var (window, controller, _) = MakeController(left: 800, width: 200);
            controller.OnDragFinished();
            var edge = controller.Edge;
            controller.Dispose();
            window.Close();
            return edge;
        }, TimeSpan.FromSeconds(10));

        Assert.Equal(DockEdge.None, result);
    }

    [Fact]
    public void OnDragFinished_WhenNotEnabledYet_AutoEnablesAndSnaps()
    {
        // 🔴 这是本轮修的**核心缺陷**：早期实现在开关未开启时直接 return，
        // 于是「最近阅览」那条没开开关的便签拖到边缘**完全没反应**，
        // 用户却不知道是因为功能没开（勾选框要打开菜单才看得到）。
        // 现在的契约是：拖到边缘 = 明确的贴边意图 → 自动开启 + 吸附。
        var result = RunOnDispatcher(() =>
        {
            var autoEnabled = false;
            var edges = new List<DockEdge>();

            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                Opacity = 0.01,
                Width = 200,
                Height = 120,
                Left = 4,
                Top = WorkArea.Top,
                Content = new Border(),
            };
            window.Show();

            var controller = new DockController(
                window,
                isEnabled: () => autoEnabled,          // 初始关闭
                isBusy: () => false,
                workArea: () => WorkArea,
                onEdgeChanged: e => edges.Add(e),
                onAutoEnable: () => autoEnabled = true);

            controller.OnDragFinished();
            var edge = controller.Edge;
            var enabled = autoEnabled;
            var left = window.Left;
            controller.Dispose();
            window.Close();
            return (Edge: edge, AutoEnabled: enabled, Left: left);
        }, TimeSpan.FromSeconds(10));

        Assert.True(result.AutoEnabled, "拖到边缘应当自动开启贴边开关");
        Assert.Equal(DockEdge.Left, result.Edge);
        Assert.Equal(WorkArea.Left, result.Left, 1);
    }

    [Fact]
    public void OnDragFinished_WhenUserExplicitlyDisabled_DoesNotSnap()
    {
        // 用户主动在菜单里取消勾选过：不能被"拖到边缘"强行重新打开，
        // 否则就成了关不掉的功能（比开不掉更让人恼火）。
        var result = RunOnDispatcher(() =>
        {
            var autoEnabled = false;
            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                Opacity = 0.01,
                Width = 200,
                Height = 120,
                Left = 4,
                Top = WorkArea.Top,
                Content = new Border(),
            };
            window.Show();

            var controller = new DockController(
                window,
                isEnabled: () => false,               // 保持关闭
                isBusy: () => false,
                workArea: () => WorkArea,
                onEdgeChanged: _ => { },
                onAutoEnable: () => autoEnabled = true,
                suppressAutoEnable: () => true);      // 用户明确关过

            controller.OnDragFinished();
            var edge = controller.Edge;
            var left = window.Left;
            controller.Dispose();
            window.Close();
            return (Edge: edge, AutoEnabled: autoEnabled, Left: left);
        }, TimeSpan.FromSeconds(10));

        Assert.False(result.AutoEnabled, "用户明确关掉过就不该自动开启");
        Assert.Equal(DockEdge.None, result.Edge);
        Assert.Equal(4, result.Left, 1);
    }

    [Fact]
    public void OnDragFinished_WhenDockDisabledAfterBeingDocked_ClearsEdge()
    {
        var result = RunOnDispatcher(() =>
        {
            var (window, controller, edges) = MakeController(left: 4, width: 200, enabled: true);
            controller.OnDragFinished();
            var first = controller.Edge;
            controller.Dispose();
            window.Close();
            return (first, edges.ToList());
        }, TimeSpan.FromSeconds(10));

        Assert.Equal(DockEdge.Left, result.first);
    }

    // ===== 拖动中的吸附预览（本轮新增的手感修复）=====

    [Fact]
    public void UpdateSnapPreview_OutsideSnapButInsidePreview_ReportsEdge()
    {
        // 手感修复的核心：早期版本拖动全程界面毫无反馈，用户"找不到刚刚好的距离"。
        // 现在无论开关是否开启，只要进入预览带就立刻提示"松手会吸"。
        var result = RunOnDispatcher(() =>
        {
            var previews = new List<DockEdge>();

            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                Opacity = 0.01,
                Width = 200,
                Height = 120,
                // 落在预览带内但在松手判定带外
                Left = DockGeometry.SnapDistance + 6,
                Top = WorkArea.Top,
                Content = new Border(),
            };
            window.Show();

            var controller = new DockController(
                window,
                isEnabled: () => false,               // 特意关闭 —— 预览依然要提示
                isBusy: () => false,
                workArea: () => WorkArea,
                onEdgeChanged: _ => { },
                onSnapPreviewChanged: e => previews.Add(e));

            controller.UpdateSnapPreview();
            var preview = controller.SnapPreview;

            controller.ClearSnapPreview();
            var cleared = controller.SnapPreview;

            controller.Dispose();
            window.Close();
            return (Preview: preview, Cleared: cleared, Events: previews.ToList());
        }, TimeSpan.FromSeconds(10));

        Assert.Equal(DockEdge.Left, result.Preview);
        Assert.Contains(DockEdge.Left, result.Events);
        Assert.Equal(DockEdge.None, result.Cleared);
    }

    [Fact]
    public void UpdateSnapPreview_WhenUserExplicitlyDisabled_StaysSilent()
    {
        // 用户明确关过贴边：不能提示"松手会吸"，否则松手却没吸，自相矛盾
        var result = RunOnDispatcher(() =>
        {
            var previews = new List<DockEdge>();
            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                Opacity = 0.01,
                Width = 200,
                Height = 120,
                Left = 2,
                Top = WorkArea.Top,
                Content = new Border(),
            };
            window.Show();

            var controller = new DockController(
                window,
                isEnabled: () => false,
                isBusy: () => false,
                workArea: () => WorkArea,
                onEdgeChanged: _ => { },
                onSnapPreviewChanged: e => previews.Add(e),
                suppressAutoEnable: () => true);

            controller.UpdateSnapPreview();
            var preview = controller.SnapPreview;
            controller.Dispose();
            window.Close();
            return (Preview: preview, Events: previews.Count);
        }, TimeSpan.FromSeconds(10));

        Assert.Equal(DockEdge.None, result.Preview);
        Assert.Equal(0, result.Events);
    }

    [Fact]
    public void OnDragFinished_ClearsSnapPreview()
    {
        // 松手后必须清掉预览，否则"松手贴到左边"的提示会残留在屏幕上
        var result = RunOnDispatcher(() =>
        {
            var (window, controller, _) = MakeController(left: 80, width: 200);
            controller.UpdateSnapPreview();
            controller.OnDragFinished();
            var preview = controller.SnapPreview;
            controller.Dispose();
            window.Close();
            return preview;
        }, TimeSpan.FromSeconds(10));

        Assert.Equal(DockEdge.None, result);
    }

    // ===== 自动收起 =====

    [Fact]
    public void Collapse_HappensAfterDelay_AndLeavesVisibleStrip()
    {
        var result = RunOnDispatcher(() =>
        {
            var (window, controller, _) = MakeController(left: 4, width: 200);
            controller.OnDragFinished();

            // 拖动阶段不应立刻收起（有 700ms 缓冲）
            var collapsedImmediately = controller.IsCollapsed;

            var dispatcher = Dispatcher.CurrentDispatcher;
            Pump(dispatcher, 1600);   // 覆盖 700ms 延迟 + 180ms 动画

            var collapsed = controller.IsCollapsed;
            var finalLeft = window.Left;
            controller.Dispose();
            window.Close();
            return (collapsedImmediately, collapsed, finalLeft, window.Width);
        }, TimeSpan.FromSeconds(15));

        Assert.False(result.collapsedImmediately, "松手后不应立即收起，需留缓冲期");
        Assert.True(result.collapsed, "延迟过后应已收起");

        // 关键断言：可见右边界 = left + width 必须正好留下 VisibleStripWidth。
        // 容差放宽到 1px：动画按 DPI 换算后可能落在小数上（实测 4.67 / 5.33），
        // 逻辑正确即可，不必苛求像素级精确。
        double visibleRight = result.finalLeft + result.Width;
        Assert.True(Math.Abs(visibleRight - DockGeometry.VisibleStripWidth) < 1.0,
            $"可见右边界={visibleRight:F2}，应≈{DockGeometry.VisibleStripWidth}");
    }

    [Fact]
    public void Collapse_RightEdge_LeavesVisibleStripOnRight()
    {
        var result = RunOnDispatcher(() =>
        {
            var (window, controller, _) = MakeController(left: WorkArea.Right - 200 + 3, width: 200);
            controller.OnDragFinished();
            Pump(Dispatcher.CurrentDispatcher, 1600);
            var collapsed = controller.IsCollapsed;
            var left = window.Left;
            controller.Dispose();
            window.Close();
            return (collapsed, left);
        }, TimeSpan.FromSeconds(15));

        Assert.True(result.collapsed);
        // 可见左边界 = left，应距屏幕右边缘正好一个可见条（容差 1px，见上）
        double gap = WorkArea.Right - result.left;
        Assert.True(Math.Abs(gap - DockGeometry.VisibleStripWidth) < 1.0,
            $"距右边缘={gap:F2}，应≈{DockGeometry.VisibleStripWidth}");
    }

    // ===== 唤出 / 回缩 =====

    [Fact]
    public void ExpandNow_AfterCollapse_RestoresExpandedPositionImmediately()
    {
        var result = RunOnDispatcher(() =>
        {
            var (window, controller, _) = MakeController(left: 4, width: 200);
            controller.OnDragFinished();
            Pump(Dispatcher.CurrentDispatcher, 1600);

            var collapsedLeft = window.Left;
            controller.ExpandNow();
            var expandedLeft = window.Left;
            var collapsed = controller.IsCollapsed;
            controller.Dispose();
            window.Close();
            return (collapsedLeft, expandedLeft, collapsed);
        }, TimeSpan.FromSeconds(15));

        Assert.NotEqual(result.collapsedLeft, result.expandedLeft);
        Assert.False(result.collapsed);
        // 展开后左边界回到工作区左侧
        Assert.Equal(WorkArea.Left, result.expandedLeft, 1);
    }

    [Fact]
    public void OnPointerEnter_WhenCollapsed_ExpandsBack()
    {
        var result = RunOnDispatcher(() =>
        {
            var (window, controller, _) = MakeController(left: 4, width: 200);
            controller.OnDragFinished();
            Pump(Dispatcher.CurrentDispatcher, 1600);
            var collapsedLeft = window.Left;

            controller.OnPointerEnter();
            Pump(Dispatcher.CurrentDispatcher, 600);   // 等滑出动画
            var expandedLeft = window.Left;
            var collapsed = controller.IsCollapsed;
            controller.Dispose();
            window.Close();
            return (collapsedLeft, expandedLeft, collapsed);
        }, TimeSpan.FromSeconds(15));

        Assert.True(result.expandedLeft > result.collapsedLeft + 100,
            "唤醒后窗口应大幅滑出，而不是只动几像素");
        Assert.False(result.collapsed);
    }

    // ===== Dispose 安全性 =====

    [Fact]
    public void Dispose_StopsTimersAndIsIdempotent()
    {
        var result = RunOnDispatcher(() =>
        {
            var (window, controller, _) = MakeController(left: 4, width: 200);
            controller.OnDragFinished();
            controller.Dispose();
            window.Close();
            controller.Dispose();
            window.Close();   // 二次调用不应抛

            // Dispose 后即使定时器还排着，方法调用也必须安全返回
            controller.OnDragFinished();
            controller.OnPointerEnter();
            controller.OnPointerLeave();
            controller.PollAwakenZone();
            controller.ExpandNow();
            return true;
        }, TimeSpan.FromSeconds(10));

        Assert.True(result);
    }

    [Fact]
    public void Dispose_DuringAnimation_DoesNotThrowWhenAnimationCompletes()
    {
        var result = RunOnDispatcher(() =>
        {
            var (window, controller, _) = MakeController(left: 4, width: 200);
            controller.OnDragFinished();
            // 只推一点点，让收起动画还在进行中
            Pump(Dispatcher.CurrentDispatcher, 750);
            controller.Dispose();
            window.Close();
            // 继续把动画跑完 —— 不应在已释放的窗口上崩
            Pump(Dispatcher.CurrentDispatcher, 600);
            return true;
        }, TimeSpan.FromSeconds(15));

        Assert.True(result);
    }

    [Fact]
    public void Restore_DoesNotCollapseAtStartup()
    {
        // 启动时必须看得见便签：即使记录着贴边边，也不能一开机就收起
        var result = RunOnDispatcher(() =>
        {
            var (window, controller, _) = MakeController(left: 4, width: 200);
            controller.Restore(DockEdge.Left);
            var collapsed = controller.IsCollapsed;
            var edge = controller.Edge;
            controller.Dispose();
            window.Close();
            return (collapsed, edge);
        }, TimeSpan.FromSeconds(10));

        Assert.False(result.collapsed, "启动恢复时不应处于收起态");
        Assert.Equal(DockEdge.Left, result.edge);
    }

    [Fact]
    public void Restore_WhenDisabled_IgnoresRecordedEdge()
    {
        var result = RunOnDispatcher(() =>
        {
            var (window, controller, _) = MakeController(left: 4, width: 200, enabled: false);
            controller.Restore(DockEdge.Right);
            var edge = controller.Edge;
            controller.Dispose();
            window.Close();
            return edge;
        }, TimeSpan.FromSeconds(10));

        Assert.Equal(DockEdge.None, result);
    }

    // ===== 反复开合不漂移 =====

    [Fact]
    public void RepeatedCollapseExpand_DoesNotDrift()
    {
        var result = RunOnDispatcher(() =>
        {
            var (window, controller, _) = MakeController(left: 4, width: 200);
            controller.OnDragFinished();

            double firstExpanded = window.Left;
            var lefts = new List<double>();

            for (int i = 0; i < 3; i++)
            {
                Pump(Dispatcher.CurrentDispatcher, 1600);   // 收起
                lefts.Add(window.Left);
                controller.ExpandNow();                     // 展开
                lefts.Add(window.Left);
            }

            var lastExpanded = window.Left;
            controller.Dispose();
            window.Close();
            return (firstExpanded, lastExpanded, lefts);
        }, TimeSpan.FromSeconds(30));

        // 展开位置必须逐次一致 —— 若 clamp 或对齐算错，会每轮往里挪几像素
        Assert.Equal(result.firstExpanded, result.lastExpanded, 2);
    }

    // ===== 多显示器：贴边必须对齐「窗口所在屏」的边 =====

    /// <summary>
    /// 右侧副屏工作区（DIP）。主屏 [0,1920]，副屏 [2000, ...]。
    /// 用 2000 起点模拟主副屏之间的真实间隙（用户机器上主屏右 2560 / 副屏左 3840）。
    /// </summary>
    private static readonly Rect SecondaryWorkArea = new(2000, 100, 1920, 1080);

    /// <summary>右侧副屏：左外侧连着主屏 → 左=接缝；右/上/下=真外边界。</summary>
    private const DesktopSides SecondaryOnRightOuter = DesktopSides.Right | DesktopSides.Top | DesktopSides.Bottom;

    /// <summary>主屏（左）：右外侧连着副屏 → 右=接缝；左/上/下=真外边界。</summary>
    private const DesktopSides PrimaryWithRightNeighborOuter = DesktopSides.Left | DesktopSides.Top | DesktopSides.Bottom;

    [Fact]
    public void OnDragFinished_OnSecondaryMonitor_AlignsToSecondaryRightEdge()
    {
        // 🔴 上一轮用户上报的 bug：右侧接副屏后，副屏上右贴边的便签跳到了**主屏右缘**
        // （桌面中央偏右）。根因是窗口拿了 SystemParameters.WorkArea（永远给主屏）。
        // 本用例把 workArea 显式设为副屏，验证会正确对齐到副屏右缘。
        var result = RunOnDispatcher(() =>
        {
            double rightEdge = SecondaryWorkArea.Right;
            var (window, controller, edges) = MakeController(
                left: rightEdge - 200 + 5, width: 200, workArea: SecondaryWorkArea,
                outerSides: SecondaryOnRightOuter);

            controller.OnDragFinished();
            var edge = controller.Edge;
            var alignedLeft = window.Left;
            controller.Dispose();
            window.Close();
            return (Edge: edge, AlignedLeft: alignedLeft, PrimaryRight: WorkArea.Right, SecondaryRight: rightEdge);
        }, TimeSpan.FromSeconds(10));

        Assert.Equal(DockEdge.Right, result.Edge);
        // 可见右边界（无阴影边距时即 left+width）应对齐副屏右缘
        double visibleRight = result.AlignedLeft + 200;
        Assert.Equal(result.SecondaryRight, visibleRight, 1);
        Assert.True(visibleRight > result.PrimaryRight,
            $"必须落在主屏右缘({result.PrimaryRight})之外，否则就是重现了那个 bug");
    }

    [Fact]
    public void Collapse_OnSecondaryMonitor_LeavesStripAtSecondaryRightEdge()
    {
        var result = RunOnDispatcher(() =>
        {
            var (window, controller, _) = MakeController(
                left: SecondaryWorkArea.Right - 200 + 5, width: 200, workArea: SecondaryWorkArea,
                outerSides: SecondaryOnRightOuter);
            controller.OnDragFinished();
            Pump(Dispatcher.CurrentDispatcher, 1600);
            var collapsed = controller.IsCollapsed;
            var left = window.Left;
            controller.Dispose();
            window.Close();
            return (collapsed, left);
        }, TimeSpan.FromSeconds(15));

        Assert.True(result.collapsed);
        // 收起后可见条应贴在**副屏**右缘，而不是主屏右缘
        double gap = SecondaryWorkArea.Right - result.left;
        Assert.True(Math.Abs(gap - DockGeometry.VisibleStripWidth) < 1.0,
            $"距副屏右边缘={gap:F2}，应≈{DockGeometry.VisibleStripWidth}");
    }

    // ===== 多显示器：接缝不可贴边（本轮修复） =====

    [Fact]
    public void OnDragFinished_AtSeam_DoesNotDock_AndKeepsPosition()
    {
        // 🔴 本轮用户上报：主屏**右侧**（= 与右副屏的接缝）吸附时便签跳走。
        // 修正后：接缝处不贴边（Edge 保持 None、不触发 onEdgeChanged、位置不被搬走）。
        var result = RunOnDispatcher(() =>
        {
            // 便签贴在主屏右缘（工作区右边界 1920）。主屏右侧有副屏 → 是接缝。
            var (window, controller, edges) = MakeController(
                left: WorkArea.Right - 200 + 4, width: 200,
                outerSides: PrimaryWithRightNeighborOuter);

            double before = window.Left;
            controller.OnDragFinished();
            Pump(Dispatcher.CurrentDispatcher, 100); // 给可能发生的对齐/动画一点时间
            var after = window.Left;
            var edge = controller.Edge;
            var recorded = edges.ToList();
            controller.Dispose();
            window.Close();
            return (Edge: edge, Before: before, After: after, Edges: recorded);
        }, TimeSpan.FromSeconds(10));

        Assert.Equal(DockEdge.None, result.Edge);
        Assert.DoesNotContain(DockEdge.Right, result.Edges);
        // 位置不应被"对齐"到缝上而跳动
        Assert.True(Math.Abs(result.After - result.Before) < 1.0,
            $"接缝处不应移动便签：before={result.Before:F1} after={result.After:F1}");
    }

    [Fact]
    public void OnDragFinished_SecondaryLeftEdge_IsSeam_DoesNotDock()
    {
        // 接缝的另一侧：副屏左缘（与主屏相邻）。历史上这里贴左会跳到缝上；
        // 现判定为接缝 → 不贴边。
        var result = RunOnDispatcher(() =>
        {
            var (window, controller, _) = MakeController(
                left: SecondaryWorkArea.Left + 3, width: 200, workArea: SecondaryWorkArea,
                outerSides: SecondaryOnRightOuter);
            controller.OnDragFinished();
            var edge = controller.Edge;
            controller.Dispose();
            window.Close();
            return edge;
        }, TimeSpan.FromSeconds(10));

        Assert.Equal(DockEdge.None, result);
    }

    [Fact]
    public void OnDragFinished_PrimaryLeftEdge_StillDocksLeft()
    {
        // 对照：主屏左缘是真外边界 → 即便右侧有副屏，也必须照常贴左。
        var result = RunOnDispatcher(() =>
        {
            var (window, controller, _) = MakeController(
                left: 4, width: 200,
                outerSides: PrimaryWithRightNeighborOuter);
            controller.OnDragFinished();
            var edge = controller.Edge;
            var alignedLeft = window.Left;
            controller.Dispose();
            window.Close();
            return (Edge: edge, AlignedLeft: alignedLeft);
        }, TimeSpan.FromSeconds(10));

        Assert.Equal(DockEdge.Left, result.Edge);
        Assert.Equal(WorkArea.Left, result.AlignedLeft, 1);
    }
}
