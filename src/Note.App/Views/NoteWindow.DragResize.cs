using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

using NoteApp.Helpers;
using NoteApp.Models.Enums;

namespace NoteApp.Views;

/// <summary>
/// <see cref="NoteWindow"/> 的窗口拖拽 / 缩放 / 吸附预览部分（partial）。
///
/// 拆出来的理由：这一整块是**有状态的持续交互**（按下 → 拖动 → 松手，
/// 期间还要跑定时器采样、播阴影动画、显示吸附提示），
/// 与菜单构建那种「一次性声明」的代码毫无相似之处。
///
/// **两条容易踩的设计约束**（改这里前必读）：
/// 1. <see cref="Window.DragMove"/> 是**同步阻塞**调用 —— 拖动期间对外没有任何回调可达。
///    所以「拖动中的实时反馈」只能靠一个并行高频定时器采样（<c>_snapPreviewTimer</c>, 40ms），
///    这是唯一可行的办法，不是偷懒。
/// 2. 拖动期间的视觉变化一律用 **Opacity 动画**，不要切 <c>Visibility</c> ——
///    后者每帧都触发布局失效（整窗重新测量），在 150% DPI + 分层窗口下明显发涩。
/// </summary>
public partial class NoteWindow
{
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (IsInsideButton(e.OriginalSource)) return;

        // 标题编辑态：点击交给编辑框，不触发拖拽/双击切换
        if (_viewModel.IsEditingTitle) return;

        // 双击标题栏：折叠态进入标题编辑，展开态切换折叠/展开
        if (e.ClickCount == 2)
        {
            if (_viewModel.IsCollapsed)
            {
                _viewModel.StartEditTitleCommand.Execute(null);
            }
            else
            {
                _viewModel.ToggleCollapseCommand.Execute(null);
            }
            return;
        }

        DragWindowWithLift();
    }

    /// <summary>
    /// 拖动窗口，并在拖动期间让阴影「抬起来」。
    ///
    /// 利用 <see cref="Window.DragMove"/> 的同步语义：它会一直阻塞到用户松开鼠标才返回，
    /// 所以「开始拖动」与「结束拖动」两个时机可以精确对应到调用前后，不需要额外的钩子。
    /// 松手后复位，不影响静止时的观感。
    /// </summary>
    private void DragWindowWithLift()
    {
        SetShadowLifted(true);
        _isDragging = true;
        try
        {
            // DragMove 是**同步阻塞**的，拖动期间无法在里面插入回调。
            // 所以用一个高频定时器并行采样窗口位置，实时刷新吸附预览 ——
            // 这是唯一能在"用户还在拖"的时候给出视觉反馈的办法。
            _snapPreviewTimer.Start();
            DragMove();
        }
        finally
        {
            // 即使 DragMove 抛异常（例如按键已松开）也要把阴影复原，避免永久停在「抬起」状态
            _snapPreviewTimer.Stop();
            SetShadowLifted(false);
            _isDragging = false;
        }

        // 先收起预览提示，再判定最终结果（避免"提示要吸"和"实际没吸"同时显示）
        _dockController.ClearSnapPreview();
        HideSnapPreview();

        // DragMove 同步阻塞到松开，返回时位置已更新
        _viewModel.UpdateWindowPosition(Left, Top);

        // 松手：判断是否贴边，是则对齐并安排延迟收起
        _dockController.OnDragFinished();
    }

    /// <summary>
    /// 拖动期间实时刷新吸附预览（由 <see cref="_snapPreviewTimer"/> 每 40ms 调用）。
    /// </summary>
    private void UpdateSnapPreviewTick()
    {
        if (!_isDragging)
        {
            return;
        }

        _dockController.UpdateSnapPreview();
    }

    /// <summary>
    /// 显示/隐藏吸附预览。
    ///
    /// 用透明度动画而不是 Visibility 切换：拖动中每帧改 Visibility 会触发
    /// 布局失效（整个窗口重新测量），在 150% DPI + 分层窗口下能明显感到拖拽发涩。
    /// 只改 Opacity 走的是合成层，几乎零成本。
    /// </summary>
    private void ShowSnapPreview(DockEdge edge)
    {
        if (!_isDragging)
        {
            return;
        }

        ApplySnapPreviewVisual(SnapBarLeft, SnapHintLeft, edge == DockEdge.Left);
        ApplySnapPreviewVisual(SnapBarRight, SnapHintRight, edge == DockEdge.Right);

        SnapPreviewLayer.Visibility = edge == DockEdge.None ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void ApplySnapPreviewVisual(UIElement bar, UIElement hint, bool on)
    {
        var target = on ? 1.0 : 0.0;
        var duration = TimeSpan.FromMilliseconds(on ? 90 : 40);

        bar.BeginAnimation(OpacityProperty, new DoubleAnimation(target, duration));
        hint.BeginAnimation(OpacityProperty, new DoubleAnimation(target, duration));
    }

    private void HideSnapPreview()
    {
        SnapPreviewLayer.Visibility = Visibility.Collapsed;
        SnapBarLeft.Opacity = 0;
        SnapBarRight.Opacity = 0;
        SnapHintLeft.Opacity = 0;
        SnapHintRight.Opacity = 0;
    }

    /// <summary>
    /// 切换「抬起」阴影：模糊半径、偏移与浓度一起变化，营造便签离开桌面的观感。
    /// 幅度刻意压得比较小 —— 模糊半径超出外层 Border 的 12px 边距会被裁掉，
    /// 所以取 13/4（外扩约 10.5px）而不是更夸张的值。
    /// </summary>
    private void SetShadowLifted(bool lifted)
    {
        if (NoteShadow is null)
        {
            return;
        }

        var duration = TimeSpan.FromMilliseconds(lifted ? 110 : 170);

        Animate(NoteShadow, DropShadowEffect.BlurRadiusProperty, lifted ? 13.0 : 8.0, duration);
        Animate(NoteShadow, DropShadowEffect.ShadowDepthProperty, lifted ? 4.0 : 2.0, duration);
        Animate(NoteShadow, DropShadowEffect.OpacityProperty, lifted ? 0.26 : 0.15, duration);
    }

    private static void Animate(Animatable target, DependencyProperty property, double to, TimeSpan duration)
    {
        target.BeginAnimation(
            property,
            new DoubleAnimation(to, duration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }

    /// <summary>判断点击源是否在按钮内（更多/最小化/置顶/关闭），是则不触发拖拽。</summary>
    private static bool IsInsideButton(object source)
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is Button) return true;
            // 🔴 标题栏文字被命中时 OriginalSource 可能是 Run，必须走 GetParentSafe
            current = TreeWalker.GetParentSafe(current);
        }
        return false;
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_viewModel.IsCollapsed) return;

        var direction = (sender as FrameworkElement)?.Tag as string ?? string.Empty;
        ResizeWindow(direction, e.HorizontalChange, e.VerticalChange);
    }

    /// <summary>八向缩放：按方向字符串（含 'N'/'S'/'E'/'W'）调整位置与尺寸，并在触达最小尺寸时夹取。</summary>
    private void ResizeWindow(string direction, double dx, double dy)
    {
        double newLeft = Left, newTop = Top, newWidth = Width, newHeight = Height;

        if (direction.Contains('E')) newWidth = Width + dx;
        if (direction.Contains('W')) { newWidth = Width - dx; newLeft = Left + dx; }
        if (direction.Contains('S')) newHeight = Height + dy;
        if (direction.Contains('N')) { newHeight = Height - dy; newTop = Top + dy; }

        if (newWidth < MinWidth)
        {
            if (direction.Contains('W')) newLeft -= MinWidth - newWidth;
            newWidth = MinWidth;
        }

        if (newHeight < MinHeight)
        {
            if (direction.Contains('N')) newTop -= MinHeight - newHeight;
            newHeight = MinHeight;
        }

        Left = newLeft;
        Top = newTop;
        Width = newWidth;
        Height = newHeight;
    }
}
