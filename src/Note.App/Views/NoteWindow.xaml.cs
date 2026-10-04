using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

using NoteApp.Data.Repositories;
using NoteApp.Helpers;
using NoteApp.Models;
using NoteApp.Models.Enums;
using NoteApp.Native;
using NoteApp.Services;
using NoteApp.ViewModels;

namespace NoteApp.Views;

/// <summary>便签窗口：无边框、可拖拽移动、八向缩放。业务逻辑全部在 NoteWindowViewModel；此处仅保留窗口交互与 P/Invoke。</summary>
public partial class NoteWindow : Window, NoteLineEditor.IHost
{
    /// <summary>外层 Border 的阴影边距（给 DropShadowEffect 留出渲染空间）。</summary>
    private const double ShadowMargin = 12;

    /// <summary>便签最小逻辑尺寸（不含阴影边距）。</summary>
    private const double MinLogicalWidth = 180;
    private const double MinLogicalHeight = 120;

    /// <summary>折叠态逻辑高度（仅标题栏）。</summary>
    private const double CollapsedLogicalHeight = 32;

    private readonly NoteWindowViewModel _viewModel;
    private readonly NoteLineEditor _lineEditor;
    private readonly DockController _dockController;
    private readonly bool _noFocusSetting;
    private readonly IReminderService _reminderService;
    private HwndSource? _hWndSource;
    private bool _isLoaded;

    /// <summary>贴边唤起带的轮询定时器。</summary>
    private readonly DispatcherTimer _awakenTimer;

    /// <summary>
    /// 拖动期间的吸附预览采样定时器。
    ///
    /// 为什么必须轮询而不是用事件：<see cref="Window.DragMove"/> 是同步阻塞调用，
    /// 拖动过程中对外没有任何回调可达 —— 只能并行取样窗口位置。
    /// 40ms（约 25fps）足够跟手，又不会在拖拽时抢占太多 UI 线程时间。
    /// </summary>
    private readonly DispatcherTimer _snapPreviewTimer;

    /// <summary>是否正在拖动窗口（拖动期间不自动收起，避免东西在手里跑掉）。</summary>
    private bool _isDragging;

    /// <summary>折叠前的展开逻辑高度（用于展开恢复）。</summary>
    private double _expandedLogicalHeight = MinLogicalHeight;

    public NoteWindow(Guid noteId)
    {
        InitializeComponent();

        var noteService = ServiceLocator.GetRequiredService<INoteService>();
        var tagRepository = ServiceLocator.GetRequiredService<ITagRepository>();
        var editorService = ServiceLocator.GetRequiredService<INoteEditorService>();
        _reminderService = ServiceLocator.GetRequiredService<IReminderService>();
        var settingsService = ServiceLocator.GetRequiredService<ISettingsService>();
        var attachmentService = ServiceLocator.GetRequiredService<IAttachmentService>();
        _viewModel = new NoteWindowViewModel(noteService, tagRepository, editorService, _reminderService, settingsService, attachmentService, noteId);
        DataContext = _viewModel;
        _lineEditor = new NoteLineEditor(this, ContentItemsControl, ContentBox);
        // 剪贴板里是图片时贴成附件，而不是尝试当文本粘贴（那会什么都没发生）
        _lineEditor.ImagePasteHandler = () => _viewModel.Attachments.TryAddFromClipboard();
        _noFocusSetting = ServiceLocator.GetRequiredService<ISettingsService>().GetBool("general.pinned_no_focus", true);

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.LinesRebuilt += OnLinesRebuilt;
        _viewModel.CloseRequested += (_, _) => Close();
        _viewModel.NewNoteRequested += (_, e) => new NoteWindow(e.Note.Id).Show();
        _reminderService.ReminderTriggered += OnReminderTriggered;

        // 极简模式下内容区任意位置（含行文本框内）右键弹出完整菜单。
        //
        // 🔴 必须走 AddHandler(handledEventsToo: true)，不能用 `ContextMenuOpening += ...`：
        // 行 TextBox 自己有默认右键菜单（剪切/复制/粘贴），TextBoxBase 在它的
        // ContextMenuOpening 类处理里会把事件标记为 Handled。普通订阅（handledEventsToo
        // 为 false）因此**根本不会被调用** —— 表现为极简模式下右键弹出来的仍是系统菜单，
        // 里面没有"退出极简模式"，用户被困在极简模式里出不去。
        AddHandler(ContextMenuService.ContextMenuOpeningEvent,
                   new ContextMenuEventHandler(Window_ContextMenuOpening),
                   handledEventsToo: true);

        MinWidth = MinLogicalWidth + ShadowMargin * 2;
        MinHeight = MinLogicalHeight + ShadowMargin * 2;

        RestorePositionAndSize(_viewModel.Note);

        // 贴边自动隐藏。注意：**不要求用户先开启开关** —— "把便签拖到屏幕边缘"
        // 本身就是明确的贴边意图，DragFinished 里会自动把开关打开（见 DockController）。
        // 早期版本在开关未开启时直接 return，用户拖到边缘毫无反应，
        // 却完全不知道是因为功能没开，只会认为"这个功能坏了"。
        _dockController = new DockController(
            this,
            // 贴边只对**常规形态**开放：折叠胶囊和极简磁贴都不该被自动收走
            // （极简磁贴的存在意义就是"一直摆在桌面上"，被收成 5px 条等于功能打架）。
            // 这里以前手写 `!IsCollapsed && !IsMinimal` —— 收口后统一读 IsCompactDisplay，
            // 将来若再加第三种形态，这一行不用改。
            isEnabled: () => _viewModel.IsDockEnabled && !_viewModel.IsCompactDisplay,
            // 「忙」的定义：拖动中/编辑标题中/被锁定手动调整中都不该自动收起，
            // 否则用户正在操作时窗口会自己跑掉
            isBusy: () => _isDragging || _viewModel.IsEditingTitle,
            // ⚠️ 必须取「窗口当前所在显示器」的工作区，不能用 SystemParameters.WorkArea ——
            // 后者永远只返回主屏工作区。右侧接副屏时，右贴边会因此对齐到主屏右边界
            // （在整体桌面里只是中间一条竖线），便签凭空跳到屏幕中央偏右；
            // 左贴边却看不出问题，因为主屏左边界恰好等于真实桌面左边界。
            workArea: () => MonitorHelper.WorkAreaForWindow(this),
            // ⚠️ 还要告诉控制器「本屏哪几条边是真实桌面外边界」。
            // 多屏拼接时相邻两屏之间有一条「接缝」，它不是可贴边的边缘 ——
            // 在接缝处收起便签等于把它藏进看不见的缝里，视觉上就是"跳到了桌面中间"。
            // 由 MonitorHelper 比较「本屏显示区」与「所有显示器并集」算出。
            outerSides: () => MonitorHelper.GetOuterSidesForWindow(this),
            onEdgeChanged: edge => _viewModel.UpdateDockEdge(edge),
            // 必须传真实的阴影边距：贴边对齐算的是「可见内容」的边界，
            // 窗口外框比可见内容多出的透明阴影不能算进去，否则窗口会整体偏移一个边距
            edgeMargin: ShadowMargin,
            // 拖动过程中实时提示"松手会吸到哪条边"，解决"找不到刚刚好的距离"
            onSnapPreviewChanged: ShowSnapPreview,
            // 拖到边缘但开关没开：自动打开并落库
            onAutoEnable: () => _viewModel.AutoEnableDock(),
            // 用户在菜单里主动关过：不再自动打开
            suppressAutoEnable: () => _viewModel.DockExplicitlyDisabled);

        _dockController.Restore(_viewModel.Note.DockEdge);

        // 从「更多」菜单关闭贴边时立刻滑回来，别把便签留在半收起状态
        // （必须在 _dockController 创建之后订阅）
        _viewModel.DockDisabled += (_, _) =>
        {
            _dockController.ExpandNow();
            UpdateAwakenTimerState();
        };

        // 唤起带轮询：收起后窗口只有 5px，鼠标很可能从未真正进入窗口，
        // 所以 MouseEnter 不可靠，必须按全局指针位置兜底检查。
        _awakenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _awakenTimer.Tick += (_, _) => _dockController.PollAwakenZone();

        // 吸附预览采样：拖动期间实时告诉用户"松手会吸到哪条边"。
        // 不用更短的间隔 —— 25fps 已足够跟手，且拖动本身正在占用 UI 线程做合成。
        _snapPreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _snapPreviewTimer.Tick += (_, _) => UpdateSnapPreviewTick();
    }

    /// <summary>聚焦编辑区（新建便签后调用，让用户可直接输入）。</summary>
    public void FocusEditor()
    {
        Activate();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            // 聚焦到内容区第一个 TextBox
            var firstTextBox = FindVisualChild<TextBox>(ContentItemsControl);
            firstTextBox?.Focus();
        }));
    }

    /// <summary>
    /// 取「桌面上其它便签的可见内容矩形」的回调，供拖动时的对齐吸附计算用。
    ///
    /// 做成静态注入点而不是直接依赖 <c>WindowService</c>，是为了守住
    /// 「窗口类 → 服务」的单向依赖：便签窗口不该知道窗口管理器长什么样，
    /// 它只需要"问一句别人都在哪"。由 App 启动时注入实现。
    /// </summary>
    internal static Func<NoteWindow, List<Rect>>? OtherNoteRectsProvider { get; set; }

    /// <summary>
    /// 「这张便签此刻是否属于框选选中集合」的查询回调。
    ///
    /// **为什么必须有它（这是一个真实缺陷的修复）**：用户框选两张便签后，在**其中一张**上按下想整组拖动。
    /// 此时按下的落点在那张便签身上，便签自己的标题栏处理也会被触发 ——
    /// 于是「统一拖动」与「单张拖动（<see cref="Window.DragMove"/>）」**同时**生效：
    /// 被按住的那张同时被两条路径移动，位移比其他成员多一截，
    /// 组内相对位置被破坏（实测多出 11 物理像素）。
    ///
    /// 两条路径的时序**不可靠**（实测 WPF 输入管线先于全局钩子的转投执行），
    /// 所以不能在钩子里"事后通知便签别拖"，必须让便签在**决定拖之前**主动问一句。
    /// 这里做成静态注入点，与 <see cref="OtherNoteRectsProvider"/> 同理，
    /// 守住「窗口类不依赖 WindowService / MarqueeSelector」的单向依赖。
    /// </summary>
    internal static Func<NoteWindow, bool>? IsSelectedByMarqueeProvider { get; set; }

    /// <summary>
    /// 阴影边距（DIP）。框选协调器要靠它把「含阴影的窗口外框」换算成「可见内容矩形」——
    /// 阴影是透明的，用户看到的便签边界是内容边界，用外框做命中判定会误判。
    /// </summary>
    internal double ShadowMarginValue => ShadowMargin;

    /// <summary>
    /// 框选时的高亮反馈（被框中 → 描一圈琥珀色边）。
    ///
    /// 用一条独立于形态的边缘高亮，而不是改便签底色 ——
    /// 便签颜色是用户自己选的，不该被临时选中状态覆盖。
    /// </summary>
    internal void SetMarqueeHighlight(bool on)
    {
        if (MarqueeHighlight is null)
        {
            return;
        }

        MarqueeHighlight.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 框选批量拖动结束后落库位置。
    ///
    /// 走的是与便签自身拖动完全相同的保存路径（<c>UpdateWindowPosition</c> + 贴边判定），
    /// 这样"框选拖过去的便签"与"手动拖过去的便签"在数据上没有任何差别 ——
    /// 位置会正确保存，拖到屏幕边缘也会正确贴边。
    /// </summary>
    internal void CommitPositionAfterGroupDrag()
    {
        _viewModel.UpdateWindowPosition(Left, Top);
    }

    // ---------- 框选批量拖动的位置写入 ----------
    //
    // 🔴 为什么不能直接 `Left = xxx`：折叠胶囊的**窗口高度是一条 200ms 动画**，
    // 动画在跑的时候 `Height` 是「动画值」，而 `Left/Top` 一旦被赋值，
    // WPF 会把窗口按新的布局重新放置 —— 表现为拖动胶囊时它上下抖、或者跳到别处。
    // 批量拖动是"高频连续写位置"，必然与正在跑的动画打架。
    //
    // 所以批量拖动**先掐掉正在跑的高度动画**（`BeginAnimation(HeightProperty, null)`
    // 会把当前动画值固化成当前值），之后就是一条干净的位置写入路径。
    // 用户看到的差异是"拖动胶囊期间高度动画被跳过" —— 这是可接受的，
    // 因为拖动的视觉重心本来就在"整体跟着鼠标走"，高度渐变会分散注意力。

    /// <summary>批量拖动的起始窗口位置。折叠态在动画中时按"已落定的位置"记。</summary>
    internal (double Left, double Top) ResolveGroupDragOrigin()
    {
        FreezeHeightAnimation();
        return (Left, Top);
    }

    /// <summary>批量拖动中：设置窗口绝对位置。</summary>
    internal void SetGroupDragPosition(double left, double top)
    {
        Left = left;
        Top = top;
    }

    /// <summary>批量拖动中的对齐吸附位移。</summary>
    internal void NudgeGroupDragX(double dx) => Left += dx;

    /// <summary>批量拖动中的对齐吸附位移。</summary>
    internal void NudgeGroupDragY(double dy) => Top += dy;

    /// <summary>
    /// 把正在跑的高度动画固化成当前值。
    ///
    /// `BeginAnimation(HeightProperty, null)` 是 WPF 里"停止动画并保留当前值"的
    /// 标准写法：直接传 null 会把动画移除，属性回落到基值 —— 那会让窗口瞬间弹回
    /// 展开高度。必须传 null 的**同时**先手工把当前（动画中的）值写进基值。
    /// </summary>
    private void FreezeHeightAnimation()
    {
        // HasAnimatedProperties 为 false 时说明没有动画在跑，直接返回，
        // 避免多写一次 Height 触发一轮多余的布局。
        if (!HasAnimatedProperties)
        {
            return;
        }

        var current = Height;
        BeginAnimation(HeightProperty, null);
        Height = current;
    }

    /// <summary>在视觉树中查找指定类型的第一个子元素。</summary>
    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result) return result;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    /// <summary>VM 状态变化驱动窗口视觉（形态动画、置顶不抢焦点样式）。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // 形态变化统一在这里处理。以前监听的是 IsCollapsed —— 那时极简切换
            // 不经过这个分支，窗口尺寸/缩放的调整（极简下不该是折叠的固定高度）
            // 就漏掉了。改听 DisplayMode：三种形态共用一条路径。
            case nameof(NoteWindowViewModel.DisplayMode):
                ApplyDisplayModeVisual(_viewModel.DisplayMode);
                // 折叠/极简态本来就不该被自动收起，也不需要贴边轮询
                UpdateAwakenTimerState();
                break;
            case nameof(NoteWindowViewModel.IsPinned):
                ApplyNoActivateStyle(_viewModel.IsPinned && _noFocusSetting);
                if (_viewModel.IsPinned)
                {
                    BringToFront();
                }
                break;
            case nameof(NoteWindowViewModel.IsEditingTitle):
                if (_viewModel.IsEditingTitle)
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                    {
                        TitleEditBox.Focus();
                        TitleEditBox.SelectAll();
                    }));
                }
                break;
            case nameof(NoteWindowViewModel.IsDockEnabled):
                UpdateAwakenTimerState();
                break;
        }
    }

    /// <summary>启动时恢复位置/尺寸，并修正到屏幕工作区内（外接显示器拔掉等情况）。</summary>
    private void RestorePositionAndSize(Note note)
    {
        // 构造期还没有窗口句柄，无法问「窗口在哪块屏」；改为按记录下来的坐标
        // 找它当初所在的显示器，否则副屏上的便签会被一律拽回主屏工作区。
        // 仍保留 SystemParameters.WorkArea 作为坐标缺失时的兜底。
        var workArea = note.PositionX == 0 && note.PositionY == 0
            ? SystemParameters.WorkArea
            : MonitorHelper.WorkAreaForDipPoint(note.PositionX, note.PositionY);
        var width = (double)Math.Max(note.Width, (int)MinLogicalWidth);
        var height = (double)Math.Max(note.Height, (int)MinLogicalHeight);
        _expandedLogicalHeight = height;

        var logicalWidth = width + ShadowMargin * 2;
        // 用于 Clamp 的实际窗口内容高度：折叠态只露标题栏，展开态用记录高度。
        // ⚠️ 这里必须用「真实会显示的高度」——若折叠态仍按 300px 去 Clamp，窗口会被
        // 误判成"放得下"而允许越过工作区下边界。
        // 走 DisplayMode 而非 note.IsCollapsed，与 ApplyDisplayModeVisualImmediate 保持同一真相源。
        var logicalHeight = note.DisplayMode == NoteDisplayMode.Collapsed
            ? CollapsedLogicalHeight
            : height;

        // 贴边启用且记录过贴边边：按该边纵向对齐，保证「上次贴在哪边，这次还在哪边」，
        // 而不是把窗口落在中间（那会让用户以为贴边设置丢了）。
        //
        // ⚠️ 但必须校验「这条边现在还成立吗」：显示器布局可能变了（插拔屏、
        // 改排列顺序），原来记录的那条边现在可能变成两块屏之间的**接缝** ——
        // 接缝不是可贴边的边缘，硬对齐上去会把便签放进看不见的缝里。
        // 此时退回按记录坐标摆放（就当它没贴过边），交由下面的 Clamp 兜底。
        var savedEdge = note.DockEdge;
        if (savedEdge != DockEdge.None)
        {
            // 用记录坐标所在屏判定（此时还没有窗口句柄）。左右分别校验。
            var outer = MonitorHelper.GetOuterSidesForDipPoint(note.PositionX, note.PositionY);
            if (savedEdge == DockEdge.Left && (outer & DesktopSides.Left) == 0) savedEdge = DockEdge.None;
            if (savedEdge == DockEdge.Right && (outer & DesktopSides.Right) == 0) savedEdge = DockEdge.None;
        }

        double left;
        if (note.IsDockEnabled && savedEdge != DockEdge.None)
        {
            left = DockGeometry.ExpandedLeft(savedEdge, logicalWidth, workArea, ShadowMargin);
        }
        else
        {
            left = (double)note.PositionX;
        }

        var top = (double)note.PositionY;

        // 统一走 ClampIntoWorkArea 兜住「换显示器 / 改分辨率 / 拔掉外接屏」后
        // 旧坐标完全落在工作区外的情况。
        // 贴边便签的最小可见宽度传 VisibleStripWidth：否则会被推进去 5px，
        // 后续收起时又推回来，形成每次启动都抖一下的观感。
        var minVisible = note.IsDockEnabled ? DockGeometry.VisibleStripWidth : 0;
        var clamped = DockGeometry.ClampIntoWorkArea(
            left, top, logicalWidth, logicalHeight + ShadowMargin * 2, workArea, ShadowMargin, minVisible);
        left = clamped.Left;
        top = clamped.Top;

        Left = left;
        Top = top;
        Width = logicalWidth;

        // 启动时按形态给窗口约束。走 DisplayMode 而不是 `if (note.IsCollapsed)`：
        // 后者只区分两态，极简形态会落到 else 分支碰巧拿到正确结果，但一旦
        // 将来再加形态就会悄悄错 —— 统一由形态决定更稳。
        // 用 Immediate 版本：启动阶段不做高度动画。
        ApplyDisplayModeVisualImmediate(note.DisplayMode);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 关闭前立即保存（不等防抖），再释放 VM（取消事件订阅避免泄漏）
        _reminderService.ReminderTriggered -= OnReminderTriggered;
        _viewModel.LinesRebuilt -= OnLinesRebuilt;

        // 停掉贴边相关的定时器与动画：窗口即将销毁，若动画仍在跑会操作已失效的 HWND
        _awakenTimer.Stop();
        _snapPreviewTimer.Stop();
        _dockController.Dispose();

        // 行容器即将随窗口销毁，作废缓存并退订装饰器，避免事件把窗口钉住
        _lineEditor.Detach();

        _viewModel.SaveCommand.Execute(null);
        _viewModel.Cleanup();
        base.OnClosing(e);
    }

    /// <summary>行集合整体重建后，缓存里的 TextBox 已随容器卸载，必须作废。</summary>
    private void OnLinesRebuilt(object? sender, EventArgs e) => _lineEditor.InvalidateBoxCache();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hWndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _hWndSource?.AddHook(WndProc);

        // 启动时按 IsPinned 恢复「不抢焦点」样式（Topmost 已由 XAML 绑定 IsPinned）
        ApplyNoActivateStyle(_viewModel.IsPinned && _noFocusSetting);
    }

    /// <summary>处理 WM_MOUSEACTIVATE：置顶不抢焦点 或 极简模式下，单击不激活窗口（不抢其他窗口键盘焦点）。</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WindowNativeMethods.WM_MOUSEACTIVATE)
        {
            if ((_viewModel.IsPinned && _noFocusSetting) || _viewModel.IsMinimal)
            {
                handled = true;
                return (IntPtr)WindowNativeMethods.MA_NOACTIVATE;
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>切换 WS_EX_NOACTIVATE 扩展样式：开启后点击窗口不激活、不抢焦点。</summary>
    private void ApplyNoActivateStyle(bool enabled)
    {
        if (_hWndSource is null)
        {
            return;
        }

        var hwnd = _hWndSource.Handle;
        long style = WindowNativeMethods.GetWindowLongPtr(hwnd, WindowNativeMethods.GWL_EXSTYLE).ToInt64() & 0xFFFFFFFFL;
        style = enabled
            ? style | WindowNativeMethods.WS_EX_NOACTIVATE
            : style & ~(long)WindowNativeMethods.WS_EX_NOACTIVATE;
        WindowNativeMethods.SetWindowLongPtr(hwnd, WindowNativeMethods.GWL_EXSTYLE, new IntPtr(style));
    }

    /// <summary>置顶便签点击时提升到同级最上（不激活，避免抢焦点）。</summary>
    private void BringToFront()
    {
        if (_hWndSource is null)
        {
            return;
        }

        WindowNativeMethods.SetWindowPos(
            _hWndSource.Handle,
            WindowNativeMethods.HWND_TOP,
            0, 0, 0, 0,
            WindowNativeMethods.SWP_NOMOVE | WindowNativeMethods.SWP_NOSIZE | WindowNativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>置顶便签点击：提升层级；点击内容区则激活窗口以便输入（设计规范「除非用户点击内容区」）。</summary>
    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 极简模式：Alt+拖拽移动窗口（Windows 标准操作）
        if (_viewModel.IsMinimal && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            e.Handled = true;
            DragWindowWithLift();
            return;
        }

        // 极简模式：单击仅提升层级，不激活（不抢焦点）
        if (_viewModel.IsMinimal)
        {
            BringToFront();
            return;
        }

        if (!_viewModel.IsPinned)
        {
            return;
        }

        BringToFront();

        if (IsDescendantOf(ContentBox, e.OriginalSource))
        {
            if (_hWndSource is not null)
            {
                WindowNativeMethods.SetForegroundWindow(_hWndSource.Handle);
            }
            Activate();
            FocusFirstTextBox();
        }
    }

    /// <summary>聚焦到内容区第一个 TextBox。</summary>
    private void FocusFirstTextBox()
    {
        var firstTextBox = FindVisualChild<TextBox>(ContentItemsControl);
        firstTextBox?.Focus();
    }

    /// <summary>极简模式双击内容区：激活窗口并聚焦编辑框，进入编辑。</summary>
    private void ContentBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.IsMinimal)
        {
            return;
        }

        Activate();
        if (_hWndSource is not null)
        {
            WindowNativeMethods.SetForegroundWindow(_hWndSource.Handle);
        }
        FocusFirstTextBox();
    }

    /// <summary>内容区右键：极简模式下弹完整菜单（复制粘贴 + 便签操作，可退出极简）；非极简保持系统复制粘贴菜单。</summary>
    private void Window_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (!_viewModel.IsMinimal) return;
        if (!IsDescendantOf(ContentBox, e.OriginalSource as DependencyObject)) return;

        // 拦截默认菜单（行文本框默认菜单/空白区菜单），统一弹出完整菜单
        e.Handled = true;
        var menu = BuildContentContextMenu();
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;

        // 唤起带轮询只在贴边启用时才需要，否则纯属空转（每 120ms 一次全屏判定）
        UpdateAwakenTimerState();
    }

    /// <summary>鼠标进入便签：若处于贴边收起态则滑出。</summary>
    private void Window_MouseEnter(object sender, MouseEventArgs e) => _dockController.OnPointerEnter();

    /// <summary>鼠标离开便签：安排延迟回缩（仅贴边展开态生效）。</summary>
    private void Window_MouseLeave(object sender, MouseEventArgs e) => _dockController.OnPointerLeave();

    /// <summary>
    /// 按「是否需要兜底唤醒」启停轮询定时器。
    ///
    /// 只在**贴边启用且已收起**时才需要轮询：此时窗口只剩 5px，
    /// 鼠标可能从未真正进入窗口，`MouseEnter` 收不到，必须按全局指针位置兜底。
    /// 其它情况（未启用 / 已展开）轮询是纯浪费。
    /// </summary>
    private void UpdateAwakenTimerState()
    {
        var needed = _viewModel.IsDockEnabled && _dockController.IsCollapsed
                     && !_viewModel.IsCompactDisplay;

        if (needed)
        {
            _awakenTimer.Start();
        }
        else
        {
            _awakenTimer.Stop();
        }
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 初始布局（RestorePositionAndSize 触发）不写库；折叠态不保存尺寸（保留展开高度）
        if (!_isLoaded || _viewModel.IsCollapsed) return;

        _viewModel.UpdateWindowSize(Width - ShadowMargin * 2, Height - ShadowMargin * 2);
    }

}
