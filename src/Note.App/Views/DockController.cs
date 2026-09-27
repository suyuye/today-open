using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

using NoteApp.Helpers;
using NoteApp.Models.Enums;

namespace NoteApp.Views;

/// <summary>
/// 便签「贴边自动隐藏」的窗口行为控制器。
///
/// 单独成一个类而不是塞进 <see cref="NoteWindow.xaml.cs"/>（已近千行），
/// 原因是这块逻辑有自己完整的状态机与定时器，混进窗口代码里很难看清楚它管了哪些事。
///
/// **状态机**（3 个状态）：
/// <code>
///   Expanded  ──拖到边缘松手──▶ 收起计时中 ──到时──▶ Collapsed
///       ▲                                              │
///       └──────────鼠标移入唤醒带──滑出──── Awakened ◀──┘
/// </code>
///
/// **设计取舍**：
/// 1. **收起后一定留 5px 可见条**（<see cref="DockGeometry.VisibleStripWidth"/>）。
///    绝不把窗口完全推出屏幕 —— 那样鼠标再也碰不到它，用户会以为便签丢了。
/// 2. **贴边判定只在拖动结束时做**，不在拖动过程中做。
///    拖动途中就吸附会让手指"粘"在边缘，反而难放到想去的位置。
/// 3. **滑出/收起用动画而非直接赋值**，让位置变化可被眼睛跟上；
///    但动画结束后必须把值**落定**（`FillBehavior.Stop` + 手动赋终值），
///    否则后续任何依赖 `Window.Left` 的逻辑（保存位置、拖动）都会读到动画中间值。
/// 4. **关闭时立即停掉所有定时器与动画** —— 否则窗口已销毁而动画仍在跑，
///    会在已释放的 HWND 上设置位置。
/// </summary>
internal sealed class DockController
{
    /// <summary>松手后延迟多久自动收起。留一点缓冲，避免用户刚放好就被收走。</summary>
    private static readonly TimeSpan AutoHideDelay = TimeSpan.FromMilliseconds(700);

    /// <summary>鼠标离开后延迟多久回缩。太短会在鼠标划过时频繁开合，太长则显得迟钝。</summary>
    private static readonly TimeSpan RetractDelay = TimeSpan.FromMilliseconds(600);

    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(180);

    private readonly Window _window;
    private readonly Func<bool> _isEnabled;
    private readonly Func<bool> _isBusy;
    private readonly Func<Rect> _workArea;

    /// <summary>
    /// 取「本屏哪些边是整个桌面的真实外边界」。
    ///
    /// 用来把「接缝」（两块屏之间那条缝）排除出可贴边范围 ——
    /// 接缝处收起便签等于把它藏进看不见的地方，且视觉上像跳到了桌面中间。
    /// </summary>
    private readonly Func<DesktopSides> _outerSides;

    private readonly Action<DockEdge> _onEdgeChanged;

    /// <summary>
    /// 拖动过程中「即将吸附到哪条边」的回调（None = 不会吸附）。
    ///
    /// 这是本轮新增的手感修复核心：早期版本只在松手时判定，
    /// 用户拖到边缘时**界面毫无反应**，只能靠猜 —— 于是永远找不到"刚刚好的距离"。
    /// 现在拖动时就实时上报将要吸附的边，由窗口显示一条提示，用户能看到"吸住了"。
    /// </summary>
    private readonly Action<DockEdge>? _onSnapPreviewChanged;

    /// <summary>
    /// 用户把便签拖到边缘、但贴边开关尚未开启时调用：由外部把开关打开并落库。
    ///
    /// 目的是消除「功能没开 → 什么反应都没有 → 用户以为坏了」这个黑洞。
    /// </summary>
    private readonly Action? _onAutoEnable;

    /// <summary>
    /// 用户在菜单里**主动取消过**「贴边自动隐藏」的勾选，此时不再自动启用。
    ///
    /// 没有这个标志的话，用户关掉贴边后只要再往边缘拖一次就被强行打开，
    /// 变成"关不掉的功能"，比开不掉更让人恼火。
    /// </summary>
    private readonly Func<bool>? _suppressAutoEnable;

    /// <summary>
    /// 窗口外框比可见内容多出的透明阴影边距（DIP）。
    ///
    /// 这个值**必须与 <see cref="NoteWindow"/> 的 <c>ShadowMargin</c> 一致**，
    /// 否则贴边对齐会整体偏移这么多像素：左贴边时窗口会多缩进去一个边距，
    /// 看起来像「贴不到边」，而在只有 5px 可见条时这个偏差比可见条本身还大。
    /// </summary>
    private readonly double _edgeMargin;

    private readonly DispatcherTimer _autoHideTimer;
    private readonly DispatcherTimer _retractTimer;

    private bool _isCollapsed;
    private bool _isAnimating;
    private bool _disposed;

    public DockController(
        Window window,
        Func<bool> isEnabled,
        Func<bool> isBusy,
        Func<Rect> workArea,
        Action<DockEdge> onEdgeChanged,
        double edgeMargin = 0,
        Action<DockEdge>? onSnapPreviewChanged = null,
        Action? onAutoEnable = null,
        Func<bool>? suppressAutoEnable = null,
        Func<DesktopSides>? outerSides = null)
    {
        _window = window;
        _isEnabled = isEnabled;
        _isBusy = isBusy;
        _workArea = workArea;
        // 未提供时按"四条边都是真边界"处理 —— 与单屏行为完全一致，也便于单测。
        _outerSides = outerSides ?? (() => DesktopSides.All);
        _onEdgeChanged = onEdgeChanged;
        _edgeMargin = edgeMargin;
        _onSnapPreviewChanged = onSnapPreviewChanged;
        _onAutoEnable = onAutoEnable;
        _suppressAutoEnable = suppressAutoEnable;

        _autoHideTimer = new DispatcherTimer { Interval = AutoHideDelay };
        _autoHideTimer.Tick += (_, _) =>
        {
            _autoHideTimer.Stop();
            CollapseIfIdle();
        };

        _retractTimer = new DispatcherTimer { Interval = RetractDelay };
        _retractTimer.Tick += (_, _) =>
        {
            _retractTimer.Stop();
            RetractIfPointerLeft();
        };
    }

    /// <summary>当前贴靠的边。</summary>
    public DockEdge Edge { get; private set; } = DockEdge.None;

    /// <summary>是否处于「已收起（只露一条边）」状态。</summary>
    public bool IsCollapsed => _isCollapsed;

    /// <summary>拖动过程中实时上报的「将要吸附到哪条边」，None 表示不会吸附。</summary>
    public DockEdge SnapPreview { get; private set; } = DockEdge.None;

    /// <summary>
    /// 拖动过程中实时更新吸附预览。由 <see cref="NoteWindow"/> 的拖动钩子反复调用。
    ///
    /// 只更新预览提示，**不改窗口位置** —— 拖动途中就"拉"窗口会让手指感觉粘在边缘上，
    /// 反而更难放到想去的位置（这是早期就定下的取舍，保留）。
    /// </summary>
    public void UpdateSnapPreview()
    {
        if (_disposed)
        {
            return;
        }

        // 窗口折叠/极简态不预览（此时窗口本身只有标题栏，语义是"收纳"而非"贴边"）。
        // 注意**不能在开关未开启时就返回** —— 恰恰相反，未开启时才最需要这个提示：
        // 用户不知道自己没开功能，只看到"拖到边缘毫无反应"。
        if (_isBusy())
        {
            SetSnapPreview(DockEdge.None);
            return;
        }

        // 用户明确关掉过贴边：不再提示会吸附，避免"提示要吸、松手却不吸"的矛盾
        if (_suppressAutoEnable?.Invoke() == true && !_isEnabled())
        {
            SetSnapPreview(DockEdge.None);
            return;
        }

        var area = _workArea();
        var detected = DockGeometry.DetectEdge(
            _window.Left, _window.Width, area, _edgeMargin, DockGeometry.SnapPreviewDistance,
            _outerSides());

        SetSnapPreview(detected);
    }

    /// <summary>清除拖动预览（拖动结束或取消时调用）。</summary>
    public void ClearSnapPreview() => SetSnapPreview(DockEdge.None);

    private void SetSnapPreview(DockEdge edge)
    {
        if (SnapPreview == edge)
        {
            return;
        }

        SnapPreview = edge;
        _onSnapPreviewChanged?.Invoke(edge);
    }

    /// <summary>
    /// 启用贴边并恢复上次记录的贴边边。
    /// 注意：**不自动收起** —— 启动时便签应当是看得见的，
    /// 一开机就发现便签"不见了"会很吓人。
    /// </summary>
    public void Restore(DockEdge edge)
    {
        if (!_isEnabled())
        {
            Edge = DockEdge.None;
            return;
        }

        if (edge == DockEdge.None)
        {
            Edge = DockEdge.None;
            return;
        }

        // 记录但不对齐：用户上次关闭时可能把便签拖离了边缘，
        // 启动时按记录的边对齐会显得"位置自己变了"。
        Edge = edge;
        _isCollapsed = false;
    }

    /// <summary>
    /// 拖动结束：判断是否该贴边，并安排延迟收起。
    /// 由 <see cref="NoteWindow"/> 在 <c>DragMove</c> 返回后调用。
    ///
    /// ⚠️ **即使用户没有先在「更多」菜单里开启贴边，也会照常判定**。
    ///
    /// 早期实现的第一件事就是 <c>if (!_isEnabled()) return;</c>，
    /// 于是没开开关的便签**连吸附都不会发生** —— 用户把便签拖到边缘、
    /// 松手、什么都没发生，完全不知道是因为功能没开（菜单里只有一个勾选框，
    /// 而勾选状态要专门打开菜单才看得到）。这正是"有的便签能贴、有的怎么都不行"
    /// 的真正原因：差别只在开关。
    ///
    /// 现在的语义改成：**"把便签拖到屏幕边缘"本身就是一个明确的贴边意图**，
    /// 直接照做，并顺手把开关打开（由 <paramref name="onAutoEnable"/> 落库）。
    /// 想彻底关掉这个行为的用户，可以点菜单里的「贴边自动隐藏」取消勾选，
    /// 此时 <see cref="_suppressAutoEnable"/> 为真，就真的不再吸附。
    /// </summary>
    public void OnDragFinished()
    {
        if (_disposed || _isBusy())
        {
            return;
        }

        ClearSnapPreview();

        var area = _workArea();
        var detected = DockGeometry.DetectEdge(_window.Left, _window.Width, area, _edgeMargin,
            DockGeometry.SnapDistance, _outerSides());

        if (detected == DockEdge.None)
        {
            // 拖离边缘：解除贴边，并取消待执行的收起
            _autoHideTimer.Stop();
            if (_isCollapsed)
            {
                Expand(animate: true);
            }

            if (Edge != DockEdge.None)
            {
                Edge = DockEdge.None;
                _onEdgeChanged(DockEdge.None);
            }

            return;
        }

        // 用户明确关掉过贴边（菜单取消勾选且已落库）：尊重这个选择，不吸附
        if (_suppressAutoEnable?.Invoke() == true && !_isEnabled())
        {
            return;
        }

        // 拖到边缘 = 明确的贴边意图。开关没开就顺手打开（落库），否则用户永远不知道
        // 功能没生效，只会觉得"这个功能坏了"。
        if (!_isEnabled())
        {
            _onAutoEnable?.Invoke();
        }

        Edge = detected;
        _onEdgeChanged(detected);

        // 先把窗口对齐到贴边位置（左对齐左边 / 右对齐右边），再安排收起
        AlignToEdge();

        _autoHideTimer.Stop();
        _autoHideTimer.Start();
    }

    /// <summary>鼠标进入窗口：若已收起则滑出。</summary>
    public void OnPointerEnter()
    {
        if (_disposed || _isBusy())
        {
            return;
        }

        _retractTimer.Stop();

        if (_isCollapsed)
        {
            Expand(animate: true);
        }
    }

    /// <summary>鼠标离开窗口：若已展开且贴边，安排回缩。</summary>
    public void OnPointerLeave()
    {
        if (_disposed || _isBusy())
        {
            return;
        }

        if (_isCollapsed || Edge == DockEdge.None)
        {
            return;
        }

        _autoHideTimer.Stop();
        _retractTimer.Stop();
        _retractTimer.Start();
    }

    /// <summary>
    /// 全局指针位置复查：收起态下如果鼠标落在唤醒带内，滑出。
    ///
    /// 这一条是必需的：收起后窗口只剩下 5px 宽，而唤醒带比它宽得多（含容错区），
    /// 鼠标很可能**始终没有真正进入过窗口**，于是 `MouseEnter` 永远不会触发。
    /// 所以必须有这个基于全局指针坐标的兜底检查。
    /// </summary>
    public void PollAwakenZone()
    {
        if (_disposed || _isBusy() || !_isCollapsed || Edge == DockEdge.None)
        {
            return;
        }

        if (!DockGeometry.IsInAwakenZone(Edge, _workArea(), GetPointerDip().X))
        {
            return;
        }

        // 纵向也要求落在便签的高度范围内，否则屏幕边缘任意高度都会唤醒它
        var pointerY = GetPointerDip().Y;
        if (pointerY < _window.Top || pointerY > _window.Top + _window.Height)
        {
            return;
        }

        Expand(animate: true);
    }

    /// <summary>立即展开（例如用户从「更多」菜单关闭了贴边功能）。</summary>
    public void ExpandNow()
    {
        if (_isCollapsed)
        {
            Expand(animate: false);
        }
    }

    /// <summary>停掉定时器与动画。窗口关闭时必须调用。</summary>
    public void Dispose()
    {
        _disposed = true;
        _autoHideTimer.Stop();
        _retractTimer.Stop();

        // 动画可能仍在跑；把属性从动画中"摘出来"并落定到当前值，
        // 否则窗口销毁后动画回调仍会尝试在已失效的 HWND 上定位。
        _window.BeginAnimation(Window.LeftProperty, null);
    }

    // ---------- 内部实现 ----------

    /// <summary>把窗口左右对齐到贴边的边（不动纵向）。</summary>
    private void AlignToEdge()
    {
        var area = _workArea();
        var target = DockGeometry.ExpandedLeft(Edge, _window.Width, area, _edgeMargin);

        if (Math.Abs(_window.Left - target) > 0.5)
        {
            _window.Left = target;
        }
    }

    private void CollapseIfIdle()
    {
        if (_disposed || Edge == DockEdge.None || _isBusy() || _isCollapsed)
        {
            return;
        }

        // 鼠标还停在便签上就不收 —— 用户正要操作它
        if (IsPointerOverWindow())
        {
            _autoHideTimer.Start();
            return;
        }

        var target = DockGeometry.CollapsedLeft(Edge, _window.Width, _workArea(), _edgeMargin);
        SlideTo(target);
        _isCollapsed = true;
    }

    private void RetractIfPointerLeft()
    {
        if (_disposed || Edge == DockEdge.None || _isBusy() || _isCollapsed)
        {
            return;
        }

        if (IsPointerOverWindow())
        {
            return;
        }

        // 鼠标停在唤醒带里说明用户还在便签附近，不要收
        if (DockGeometry.IsInAwakenZone(Edge, _workArea(), GetPointerDip().X))
        {
            return;
        }

        SlideTo(DockGeometry.CollapsedLeft(Edge, _window.Width, _workArea(), _edgeMargin));
        _isCollapsed = true;
    }

    private void Expand(bool animate)
    {
        if (_disposed || _isAnimating)
        {
            return;
        }

        var target = DockGeometry.ExpandedLeft(Edge, _window.Width, _workArea(), _edgeMargin);

        if (animate)
        {
            SlideTo(target);
        }
        else
        {
            _window.BeginAnimation(Window.LeftProperty, null);
            _window.Left = target;
        }

        _isCollapsed = false;
    }

    /// <summary>
    /// 用动画把窗口横向滑到 <paramref name="targetLeft"/>。
    ///
    /// ⚠️ 动画结束后必须把值**落定**：`FillBehavior.Stop` 会让 `Left` 回到动画前的值，
    /// 所以紧接着要手动赋终值并清掉动画。否则窗口会"滑过去又弹回来"，
    /// 而且之后保存的位置是错的。
    /// </summary>
    private void SlideTo(double targetLeft)
    {
        // 动画进行中不叠加新动画：否则两个 DoubleAnimation 同时作用于 Left，
        // 后一个会顶掉前一个而**前一个的 Completed 不会触发** → _isAnimating 永久为 true。
        if (_isAnimating)
        {
            return;
        }

        _isAnimating = true;

        var animation = new DoubleAnimation(targetLeft, SlideDuration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };

        animation.Completed += (_, _) =>
        {
            _window.BeginAnimation(Window.LeftProperty, null);
            _window.Left = targetLeft;
            _isAnimating = false;
        };

        _window.BeginAnimation(Window.LeftProperty, animation);
    }

    private bool IsPointerOverWindow()
    {
        var (x, y) = GetPointerDip();
        return x >= _window.Left && x <= _window.Left + _window.Width
               && y >= _window.Top && y <= _window.Top + _window.Height;
    }

    /// <summary>
    /// 取全局指针坐标并换算成 DIP（与 <see cref="Window.Left"/> / <see cref="Window.Top"/> 同一坐标系）。
    ///
    /// ⚠️ 必须换算：<c>GetCursorPos</c> 返回的是**物理像素**，而本机 DPI 是 150%。
    /// 直接比较会让"鼠标是否在窗口上"的判定整体偏掉 1.5 倍 ——
    /// 表现是唤醒带位置完全不对，且只在非 100% 缩放的机器上复现，极难排查。
    /// </summary>
    private (double X, double Y) GetPointerDip()
    {
        if (!Native.WindowNativeMethods.GetCursorPos(out var point))
        {
            return (double.NaN, double.NaN);
        }

        var scale = GetDpiScale();
        return (point.X / scale, point.Y / scale);
    }

    /// <summary>物理像素 → DIP 的换算系数（150% 缩放时为 1.5）。</summary>
    private double GetDpiScale()
    {
        // 取**本窗口**所在的 DPI，而不是主窗口 —— 多显示器下各屏 DPI 可能不同，
        // 用错屏幕的系数会让唤醒带在副屏上整体偏移。
        var handle = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero)
        {
            return 1.0;
        }

        var dpi = Native.WindowNativeMethods.GetDpiForWindow(handle);
        return dpi <= 0 ? 1.0 : dpi / 96.0;
    }
}
