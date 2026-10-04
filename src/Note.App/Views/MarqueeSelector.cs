using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

using NoteApp.Helpers;
using NoteApp.Native;
using NoteApp.Services;

namespace NoteApp.Views;

/// <summary>
/// 「桌面框选 + 多便签统一拖动」的协调器。
///
/// **要解决的问题**：桌面上摆了七八张便签，想把其中几张挪个地方，
/// 只能一张一张拖 —— 而且拖完还很难保持原来的相对位置。
/// 希望能像框选桌面图标那样：先框住几张，再把它们一起拖走。
///
/// **交互流程**（两个独立阶段，符合"先选中、再操作"的通用心智）：
/// <code>
///   阶段一 · 框选
///     空白桌面按下 ──▶ 拖出选框，实时高亮命中的便签 ──▶ 松手 → 选中集合确定
///        │
///        └─ 按在便签上？ → 让路给便签自己的拖动，框选不介入
///
///   阶段二 · 统一拖动
///     在「已选中的便签」上按下 ──▶ 整组一起移动（保持相对位置）──▶ 松手 → 落库
///        │
///        └─ 在未选中的地方按下 → 清空选择，回到阶段一
/// </code>
///
/// **为什么不用"松手即进入拖动等待"**：松手代表手势结束，
/// 此时还期待用户继续移动是不自然的。改为"选中保持、下次按在选中便签上才整组拖"，
/// 既符合直觉，也给了用户反悔的机会（在选择之外点一下即可取消）。
///
/// **三条关键约束**：
/// 1. 🔴 **钩子回调在系统输入线程**。回调里绝不能直接操作 WPF 窗口，
///    必须 <c>Dispatcher.BeginInvoke</c> 抛到 UI 线程 —— 否则会抛
///    "调用线程无法访问此对象"或更糟的静默错乱。
/// 2. 🔴 **回调必须极快返回**。所以回调里只做「读消息 + 转投 UI 线程」。
/// 3. 🔴 **绝不能抢便签自己的拖动**。在未选中便签上按下时必须立刻让路，
///    否则会和 <see cref="Window.DragMove"/> 打架（表现为便签拖不动或乱跳）。
/// </summary>
internal sealed class MarqueeSelector : IDisposable
{
    /// <summary>判定「点在便签上」时，窗口矩形额外外扩的容错（DIP）。</summary>
    private const double HitTolerance = 2;

    private readonly IWindowService _windowService;
    private readonly GlobalMouseHook _hook;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    private MarqueeOverlayWindow? _overlay;

    /// <summary>已按下但还没拖出足够距离（用于区分「单击」与「框选」）。</summary>
    private bool _pendingMarquee;

    /// <summary>正在框选。</summary>
    private bool _isMarqueeing;

    /// <summary>正在统一拖动已选中的便签组。</summary>
    private bool _isGroupDragging;

    /// <summary>框选起点（虚拟桌面 DIP 坐标）。</summary>
    private Point _startDip;

    /// <summary>统一拖动涉及的便签窗口（含起始位置，用于绝对位移与取消回滚）。</summary>
    private readonly List<(NoteWindow Window, double Left, double Top)> _dragTargets = new();

    /// <summary>
    /// 统一拖动的**起点指针位置（DIP）**。位移一律由「当前位置 - 起点」算出，
    /// 而不是逐帧累加增量 ——
    /// 🔴 关键：钩子回调是 <c>BeginInvoke</c> 到 UI 线程执行的，鼠标快速移动时
    /// 系统产生的 mousemove 远多于 UI 线程能及时处理的数量，消息会被**合并或丢弃**。
    /// 若用增量累加，丢掉的每一帧都会永久损失一段位移，表现为"拖了半天只走了一点点"
    /// （实测拖 150 逻辑像素只移动了 28）。
    /// 用「起点 + 当前绝对位置」则每次都以最新光标为准，天然免疫丢帧。
    /// </summary>
    private Point _dragStartDip;

    /// <summary>当前选中的便签集合（跨多次操作保持，直到被点空白处或其它便签清空）。</summary>
    private readonly List<NoteWindow> _selected = new();

    private bool _disposed;

    public MarqueeSelector(IWindowService windowService)
    {
        _windowService = windowService;
        _hook = new GlobalMouseHook();
        _hook.MouseEvent += OnGlobalMouseEvent;
    }

    /// <summary>安装全局钩子。失败只记日志，不影响其它功能。</summary>
    public void Start() => _hook.Install();

    // ---------- 钩子事件：立刻转投 UI 线程 ----------

    /// <summary>【验证用】钩子收到的消息总数（用于确认"钩子到底有没有被触发"）。</summary>
    internal int HookMessageCount => _hookMessageCount;

    private int _hookMessageCount;

    private void OnGlobalMouseEvent(int message, IntPtr lParam)
    {
        _hookMessageCount++;

        // ⚠️ 这里是系统输入线程，只做最小工作：解析点位 + 转投 UI 线程。
        var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        var physical = new Point(data.pt.X, data.pt.Y);

        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            // 心跳：把"钩子活着、UI 线程也在跑"这件事写进状态文件。
            // 验证脚本靠它判断"框选没反应"是钩子没触发，还是状态机走错了分支。
            WriteStatusFile($"hook={_hookMessageCount} pending={_pendingMarquee} marquee={_isMarqueeing} drag={_isGroupDragging} selected={_selected.Count}");

            switch (message)
            {
                case WindowNativeMethods.WM_LBUTTONDOWN:
                    OnButtonDown(physical);
                    break;
                case WindowNativeMethods.WM_MOUSEMOVE:
                    OnMouseMove(physical);
                    break;
                case WindowNativeMethods.WM_LBUTTONUP:
                    OnButtonUp();
                    break;
                case WindowNativeMethods.WM_RBUTTONDOWN:
                case WindowNativeMethods.WM_MBUTTONDOWN:
                    CancelAll();
                    break;
            }
        }));
    }

    // ---------- 状态机 ----------

    /// <summary>
    /// 把「窗口坐标 → 屏幕物理像素」的换算交给窗口自己做。
    ///
    /// **为什么要走这一层而不是自己用 <c>PointToScreen</c>**：
    /// 便签是 WPF 的 per-monitor-DPI 窗口，<c>Left/Top/Width/Height</c> 是**逻辑单位（DIP）**，
    /// 而钩子给的是**物理像素**。两者之间的换算系数取决于窗口当前所在显示器 ——
    /// 主屏 1.5、副屏可能是 1.0，用系统级系数去算副屏的窗口必然错位。
    /// 窗口自己的 <c>PointToScreen</c> 会带上正确的变换矩阵，是唯一可靠的做法。
    ///
    /// 🔴 这个字段也是**自动化验证的注入点**：生产环境是真实换算，
    /// 验证时可以换成"逻辑单位直通"的实现，从而绕开 DPI 与合成输入的双重不确定性，
    /// 专注验证状态机本身（选中集合、统一位移、对齐吸附）。
    /// </summary>
    internal Func<Point, Point>? ScreenPointConverter { get; set; }

    /// <summary>
    /// 把物理像素点换算成「与便签窗口同一坐标系」的 DIP。
    ///
    /// 注意这里用的是**该点所在显示器**的 DPI，而不是 <c>GetDpiForSystem()</c> ——
    /// 后者只反映主屏，便签在副屏上时会整体偏移。
    /// 注入了 <see cref="ScreenPointConverter"/> 时走注入实现（自动化验证用）。
    /// </summary>
    private Point ToWindowDip(Point physical)
    {
        if (ScreenPointConverter is not null)
        {
            return ScreenPointConverter(physical);
        }

        return MonitorHelper.WindowDipFromPhysical(physical);
    }

    private void OnButtonDown(Point physical)
    {
        if (_isGroupDragging)
        {
            return;
        }

        var dip = ToWindowDip(physical);

        // 按在已选中的便签上：进入统一拖动（整组一起走）
        var hitSelected = _selected.Find(w => VisibleRectOf(w).Contains(dip));
        if (hitSelected is not null)
        {
            BeginGroupDrag();
            return;
        }

        // 按在别的便签上：让路给便签自己的拖动，同时清空当前选择
        // （用户点别的便签 = 放弃当前这组选择，符合直觉）
        if (IsOverAnyNote(dip))
        {
            ClearSelection();
            return;
        }

        // 按在空白桌面：开始框选
        _pendingMarquee = true;
        _isMarqueeing = false;
        _startDip = dip;
        LogHelper.Debug($"候选框选：按下点={dip.X:F0},{dip.Y:F0}");
    }

    private void OnMouseMove(Point physical)
    {
        if (_isGroupDragging)
        {
            UpdateGroupDrag(ToWindowDip(physical));
            return;
        }

        if (_pendingMarquee && !_isMarqueeing)
        {
            var dip = ToWindowDip(physical);
            if (MarqueeGeometry.IsSignificantDrag(_startDip, dip))
            {
                BeginMarquee();
            }
        }

        if (_isMarqueeing)
        {
            UpdateMarquee(ToWindowDip(physical));
        }
    }

    private void OnButtonUp()
    {
        if (_isMarqueeing)
        {
            EndMarquee();
            return;
        }

        if (_isGroupDragging)
        {
            EndGroupDrag();
            return;
        }

        _pendingMarquee = false;
    }

    // ---------- 框选 ----------

    private void BeginMarquee()
    {
        _isMarqueeing = true;

        if (ShowOverlayForTest)
        {
            EnsureOverlay();
            _overlay?.Show();
        }

        UpdateMarquee(_startDip);
    }

    private void UpdateMarquee(Point currentDip)
    {
        var rect = MarqueeGeometry.ToRect(_startDip, currentDip);

        if (ShowOverlayForTest)
        {
            _overlay?.UpdateMarquee(rect);
        }

        // 实时更新命中集合与高亮
        foreach (var window in EnumerateNoteWindows())
        {
            var hit = MarqueeGeometry.HitTest(rect, VisibleRectOf(window));
            window.SetMarqueeHighlight(hit);
        }
    }

    /// <summary>
    /// 松开鼠标：框选结束，确定最终选中集合。
    ///
    /// 用「可见内容矩形」判定而不是含阴影的窗口外框 ——
    /// 阴影是透明的，用户看到的便签边界是内容边界，用外框会"明明没框到却选中"。
    /// </summary>
    private void EndMarquee()
    {
        _isMarqueeing = false;
        _pendingMarquee = false;

        if (ShowOverlayForTest)
        {
            _overlay?.ClearMarquee();
            HideOverlay();
        }

        // 以松手时的指针位置重算一次最终选框（不必依赖 MouseMove 的最后一帧）
        var endDip = ToWindowDip(GetCursorPhysical());
        var marquee = MarqueeGeometry.ToRect(_startDip, endDip);

        _selected.Clear();
        foreach (var window in EnumerateNoteWindows())
        {
            if (MarqueeGeometry.HitTest(marquee, VisibleRectOf(window)))
            {
                _selected.Add(window);
                window.SetMarqueeHighlight(true);
            }
            else
            {
                window.SetMarqueeHighlight(false);
            }
        }

        LogHelper.Debug(
            $"框选结束：起点=({_startDip.X:F0},{_startDip.Y:F0}) 终点=({endDip.X:F0},{endDip.Y:F0}) " +
            $"选框=[{marquee.Left:F0},{marquee.Top:F0} {marquee.Width:F0}x{marquee.Height:F0}] " +
            $"选中 {_selected.Count} 张");

        WriteStatusFile($"selected={_selected.Count}");
    }

    /// <summary>
    /// 【验证用】把内部状态写到一个文件，供验证脚本读取。
    ///
    /// 为什么需要它：自动化脚本和被测进程是两个进程，脚本无法直接读对方的内存。
    /// 日志可以读，但日志有缓冲、有等级、有轮转，做同步断言不可靠；
    /// 写一个只有一行的小文件是最简单可靠的跨进程状态通道。
    /// 只有设了 <c>TODAY_OPEN_AUTOMATION_STATUS</c> 环境变量时才写，正常启动不产生任何文件。
    /// </summary>
    private static void WriteStatusFile(string content)
    {
        var path = Environment.GetEnvironmentVariable("TODAY_OPEN_AUTOMATION_STATUS");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            File.WriteAllText(path, content);
        }
        catch (Exception ex)
        {
            LogHelper.Warning($"写验证状态文件失败：{ex.Message}");
        }
    }

    // ---------- 统一拖动 ----------

    private void BeginGroupDrag()
    {
        _dragTargets.Clear();
        foreach (var window in _selected)
        {
            var origin = window.ResolveGroupDragOrigin();
            _dragTargets.Add((window, origin.Left, origin.Top));
        }

        _isGroupDragging = _dragTargets.Count > 0;
        _dragStartDip = ToWindowDip(GetCursorPhysical());

        LogHelper.Debug($"开始统一拖动：{_dragTargets.Count} 张便签，起点=({_dragStartDip.X:F0},{_dragStartDip.Y:F0})");
    }

    /// <summary>
    /// 按「当前指针 - 起点」的绝对位移统一移动所有被选中的便签。
    ///
    /// ⚠️ 用绝对位移而不是增量累加：钩子的 mousemove 经 Dispatcher 转投时会丢帧，
    /// 增量累加会永久损失位移（详见 <see cref="_dragStartDip"/> 的说明）。
    /// 绝对位移的好处是"丢多少帧都不影响最终位置"，拖到哪就是哪。
    ///
    /// 所有便签按**同一位移**移动，保持相对位置 —— 与框选桌面图标一致的心智模型。
    ///
    /// 移动后再做一次**便签间对齐吸附**：拖动组里"最靠左的那张"负责与外部便签对齐，
    /// 其余成员保持与它的相对位置同步吸附。这样框选拖动也能享受对齐的便利。
    /// </summary>
    private void UpdateGroupDrag(Point currentDip)
    {
        if (!_isGroupDragging)
        {
            return;
        }

        var dx = currentDip.X - _dragStartDip.X;
        var dy = currentDip.Y - _dragStartDip.Y;

        if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5)
        {
            return;
        }

        // 以"起始位置 + 位移"设置绝对坐标，避免累计误差
        foreach (var (window, originLeft, originTop) in _dragTargets)
        {
            window.SetGroupDragPosition(originLeft + dx, originTop + dy);
        }

        ApplyGroupAlignSnap();
    }

    /// <summary>
    /// 批量拖动时的对齐吸附。
    ///
    /// 做法：只拿**组内第一张**便签去和组外的便签算对齐偏移，然后把整个偏移
    /// 同样施加给全组 —— 这样组内相对位置不变，整组像一个整体被吸过去。
    ///
    /// 为什么不在组内成员之间互相对齐：它们本来就保持着用户摆好的相对位置，
    /// 内部再互相吸会破坏这个布局。
    /// </summary>
    private void ApplyGroupAlignSnap()
    {
        if (_dragTargets.Count == 0)
        {
            return;
        }

        var lead = _dragTargets[0].Window;

        // 折叠/极简形态不参与对齐（与单张拖动路径的规则保持一致）
        var others = new List<Rect>();
        foreach (var window in EnumerateNoteWindows())
        {
            if (_dragTargets.Any(t => ReferenceEquals(t.Window, window)))
            {
                continue;
            }

            others.Add(VisibleRectOf(window));
        }

        if (others.Count == 0)
        {
            return;
        }

        var self = VisibleRectOf(lead);
        var result = NoteAlignGeometry.Compute(self, others);
        if (!result.AlignedX && !result.AlignedY)
        {
            return;
        }

        // 把吸附偏移同样施加给全组，保持组内相对位置。
        //
        // ⚠️ 这里**不要**把偏移写回 _dragStartDip ——
        // 每帧都是从「起始位置 + 指针位移」重新算出干净位置再吸附，
        // 这是个幂等过程：吸附到对齐位之后，下一帧再算出的偏移自然是 0，不会累积。
        // 若把偏移写回起点，反而会让整组随每帧漂移（拖得越久偏得越远）。
        foreach (var (window, _, _) in _dragTargets)
        {
            if (result.AlignedX)
            {
                window.NudgeGroupDragX(result.Dx);
            }

            if (result.AlignedY)
            {
                window.NudgeGroupDragY(result.Dy);
            }
        }
    }

    /// <summary>结束拖动：落库位置。保留选中状态，方便用户接着微调。</summary>
    private void EndGroupDrag()
    {
        foreach (var (window, _, _) in _dragTargets)
        {
            // 通知 VM 保存新位置（复用便签自身拖动的落库路径）
            window.CommitPositionAfterGroupDrag();
        }

        _dragTargets.Clear();
        _isGroupDragging = false;
    }

    // ---------- 选择管理 ----------

    private void ClearSelection()
    {
        foreach (var window in _selected)
        {
            window.SetMarqueeHighlight(false);
        }

        _selected.Clear();
        WriteStatusFile("selected=0");
    }

    /// <summary>取消全部状态（右键/中键按下、或功能关闭时调用）。</summary>
    public void CancelAll()
    {
        _pendingMarquee = false;
        _isMarqueeing = false;
        _isGroupDragging = false;

        ClearSelection();
        _dragTargets.Clear();
        HideOverlay();
    }

    // ---------- 辅助 ----------

    private IEnumerable<NoteWindow> EnumerateNoteWindows()
    {
        foreach (var id in _windowService.GetOpenWindowIds())
        {
            var window = _windowService.GetWindow(id);
            if (window is { IsVisible: true })
            {
                yield return window;
            }
        }
    }

    private bool IsOverAnyNote(Point dip)
    {
        foreach (var window in EnumerateNoteWindows())
        {
            var rect = VisibleRectOf(window);
            rect.Inflate(HitTolerance, HitTolerance);
            if (rect.Contains(dip))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>便签的「可见内容矩形」（去掉透明阴影边距）。</summary>
    private static Rect VisibleRectOf(NoteWindow window)
    {
        var margin = window.ShadowMarginValue;
        return new Rect(
            window.Left + margin,
            window.Top + margin,
            Math.Max(1, window.Width - margin * 2),
            Math.Max(1, window.Height - margin * 2));
    }

    /// <summary>物理像素点 → 虚拟桌面 DIP 坐标（仅在无法定位具体显示器时的兜底）。</summary>
    private static Point ToVirtualDip(Point physical)
    {
        var scale = MonitorHelper.SystemDpiScale();
        return new Point(physical.X / scale, physical.Y / scale);
    }

    private static Point GetCursorPhysical()
    {
        if (!WindowNativeMethods.GetCursorPos(out var p))
        {
            return new Point(0, 0);
        }

        return new Point(p.X, p.Y);
    }

    // ---------- 自动化验证接口 ----------
    //
    // 存在的唯一理由：本机没有真实鼠标可以驱动自动化脚本，而合成鼠标输入
    // （mouse_event/SendInput）在**按线程过滤**的低级钩子上收不到。
    // 直接调用状态机方法可以把「钩子 → 状态机」这一段替换掉，
    // 但状态机之后的全部逻辑（命中判定、统一位移、落库、对齐吸附）都是真实的。
    //
    // ⚠️ 生产代码**绝不**调用这些方法；它们只被 Temp/ 下的验证脚本通过反射调用。

    /// <summary>
    /// 🔴 **仅供自动化验证**：坐标缩放系数（= 系统 DPI 缩放，本机 1.5）。
    ///
    /// 验证脚本给的所有坐标都是「便签窗口坐标系下的值」（即等于窗口的 Left/Top），
    /// 而 <see cref="ScreenPointConverter"/> 收到的是"物理像素"。
    /// 两者差一个 DPI 缩放系数 —— 脚本用这个属性把系数告诉被测程序，
    /// 于是注入的换算实现 (p / scale) 与真实鼠标的物理像素→DIP 换算在语义上一致。
    ///
    /// **为什么不能"逻辑单位直通"**（把物理点原样当 DIP）：那会引入一个随坐标线性
    /// 放大的系统性偏移 —— 命中判定在被放大 1.5 倍的坐标系里做，
    /// 表现为"框选永远选不中，且越靠右下偏得越多"。
    /// </summary>
    internal double AutomationDipScale { get; set; } = 1.0;

    /// <summary>
    /// 【验证用】是否显示全屏框选覆盖层。默认 true（正常行为）。
    ///
    /// 验证时关掉的必要性：覆盖层是铺满整个虚拟桌面的置顶窗口，
    /// <c>EnumWindows</c> 会把它列出来，脚本按「窗口宽度 &gt; 50」筛选时它会被算进去，
    /// 也会遮挡 BitBlt 抓图。关掉它不影响状态机（选中逻辑完全不依赖覆盖层）。
    /// </summary>
    internal bool ShowOverlayForTest { get; set; } = true;

    /// <summary>【验证用】当前选中的便签数量。</summary>
    internal int SelectedCount => _selected.Count;

    /// <summary>【验证用】当前是否正在统一拖动。</summary>
    internal bool IsGroupDraggingForTest => _isGroupDragging;

    /// <summary>【验证用】已安装钩子的状态（确认自动化安装是否生效）。</summary>
    internal bool HookInstalled => _hook.IsInstalled;

    /// <summary>【验证用】把钩子换成"接受任何线程"的安装方式。</summary>
    internal void InstallHookForAutomation() => _hook.InstallForAutomation();

    /// <summary>
    /// 这张便签此刻是否在框选选中集合里。
    ///
    /// 给 <see cref="NoteWindow"/> 在决定要不要走自己的 <c>DragMove</c> 之前查询用 ——
    /// 已选中的便签被按下时应交给统一拖动，不能再走单张拖动（否则被两条路径同时移动）。
    /// </summary>
    internal bool IsSelected(NoteWindow window) => _selected.Contains(window);

    private void EnsureOverlay()
    {
        _overlay ??= new MarqueeOverlayWindow();
    }

    private void HideOverlay()
    {
        _overlay?.ClearMarquee();
        _overlay?.Hide();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hook.MouseEvent -= OnGlobalMouseEvent;
        _hook.Dispose();
        CancelAll();

        _overlay?.Close();
        _overlay = null;
    }
}
