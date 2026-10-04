using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

using NoteApp.Helpers;

namespace NoteApp.Views;

/// <summary>
/// 框选批量拖动时显示选择框的全屏透明覆盖层。
///
/// **为什么要单独开一个窗口而不是在桌面或便签上画**：
/// 框选可能跨越整个虚拟桌面（多显示器），也可能落在没有便签的空白处 ——
/// 那张地方不属于任何我方窗口，无处可画。开一个覆盖全部显示器的透明置顶窗口，
/// 就有一个稳定的画布可用。
///
/// **三条设计约束**：
/// 1. <c>IsHitTestVisible=False</c>（XAML 里已设）：它绝不能抢鼠标 ——
///    框选的判定完全来自全局钩子，这个窗口只负责显示。
/// 2. <c>ShowActivated=False</c> + <c>WS_EX_NOACTIVATE</c>：显示它时不能把
///    用户正在用的窗口抢失焦。
/// 3. **只在框选期间显示**，拖完立刻 <see cref="Window.Hide"/> ——
///    常驻一个全屏置顶窗口会挡住壁纸交互，也会让某些游戏/全屏程序异常。
/// </summary>
public partial class MarqueeOverlayWindow : Window
{
    public MarqueeOverlayWindow()
    {
        InitializeComponent();
        // 覆盖整个虚拟桌面（含负坐标的多屏排列）
        CoverVirtualScreen();
    }

    /// <summary>把窗口铺满整个虚拟桌面（物理像素 → DIP）。</summary>
    private void CoverVirtualScreen()
    {
        var (left, top, width, height) = MonitorHelper.VirtualScreenBoundsDip();
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    /// <summary>
    /// 更新选择框矩形。传入的是**屏幕坐标（DIP）** ——
    /// 覆盖层窗口的原点在虚拟桌面左上角，所以要减去窗口自身的 Left/Top 换算成局部坐标。
    /// </summary>
    public void UpdateMarquee(Rect screenRect)
    {
        // 局部坐标 = 屏幕坐标 - 覆盖层原点
        var local = new Rect(
            screenRect.Left - Left,
            screenRect.Top - Top,
            screenRect.Width,
            screenRect.Height);

        Canvas.SetLeft(MarqueeRect, local.Left);
        Canvas.SetTop(MarqueeRect, local.Top);
        MarqueeRect.Width = local.Width;
        MarqueeRect.Height = local.Height;
        MarqueeRect.Visibility = Visibility.Visible;
    }

    /// <summary>隐藏选择框。</summary>
    public void ClearMarquee()
    {
        MarqueeRect.Visibility = Visibility.Collapsed;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 不激活样式：显示覆盖层时绝不抢走当前窗口的焦点
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        long style = Native.WindowNativeMethods.GetWindowLongPtr(hwnd, Native.WindowNativeMethods.GWL_EXSTYLE).ToInt64() & 0xFFFFFFFFL;
        style |= Native.WindowNativeMethods.WS_EX_NOACTIVATE;
        Native.WindowNativeMethods.SetWindowLongPtr(hwnd, Native.WindowNativeMethods.GWL_EXSTYLE, new IntPtr(style));
    }
}
