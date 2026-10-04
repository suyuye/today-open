using System.Windows;

using NoteApp.Native;

namespace NoteApp.Helpers;

/// <summary>
/// 解析「某块显示器」的工作区，用于多屏环境下的窗口定位。
///
/// **为什么不能直接用 <see cref="SystemParameters.WorkArea"/>**：
/// 它**永远只返回主显示器**的工作区，与窗口实际在哪块屏无关。
/// 单屏机器上二者恰好一致，所以这个问题在开发机上很难被发现；
/// 一旦用户右侧接了副屏，右贴边就会把便签对齐到**主屏的右边界** ——
/// 而那在整体桌面里只是「中间的一条竖线」，于是便签凭空跳到屏幕中央偏右，
/// 看起来完全不像贴边。左贴边却看不出问题，因为主屏左边界恰好等于真实桌面左边界。
///
/// 全部方法都返回 **DIP**（与 <see cref="Window.Left"/>/<see cref="Window.Top"/> 一致），
/// 内部把 Win32 的物理像素按该屏 DPI 换算好 —— 这一层换算不放在调用方，
/// 是因为它极容易被漏掉，而漏掉的后果（窗口位置整体偏 1.5 倍）只在非 100% 缩放机器上出现。
/// </summary>
internal static class MonitorHelper
{
    /// <summary>取主显示器的工作区（DIP）。无更明确信息时的兜底。</summary>
    internal static Rect PrimaryWorkArea()
        => ToDipRect(GetWorkAreaPhysical(IntPtr.Zero, fallbackToPrimary: true), GetSystemDpiScale());

    /// <summary>
    /// 取指定窗口所在显示器的**显示区**（DIP，含任务栏区域）。
    /// 判定「哪几条边是桌面外边界」必须用显示区而不是工作区 —— 工作区被任务栏挤过，
    /// 底边/侧边会内缩，拿它去比会误判。
    /// </summary>
    internal static Rect MonitorBoundsForWindow(Window window)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var monitor = handle == IntPtr.Zero
            ? WindowNativeMethods.MonitorFromPoint(
                DipToPhysicalPoint(window.Left, window.Top), WindowNativeMethods.MONITOR_DEFAULTTONEAREST)
            : WindowNativeMethods.MonitorFromWindow(handle, WindowNativeMethods.MONITOR_DEFAULTTONEAREST);

        var bounds = GetMonitorBoundsPhysical(monitor);
        var scale = GetDpiScaleForHandle(handle);
        return ToDipRect(bounds, scale);
    }

    /// <summary>
    /// 判断某块显示器（以物理像素显示区给出）的四条边里，哪些是**整个虚拟桌面的真实外边界**。
    ///
    /// 算法：把该屏显示区与「所有显示器并集」（<c>GetSystemMetrics</c> 的虚拟屏边界）比较 ——
    /// 若某条边与并集边界重合，说明外面没有别的屏，该方向可以贴边；
    /// 否则外面还连着别的显示器（即接缝），该方向**不可贴边**。
    ///
    /// 容差 <paramref name="tolerance"/> 用于吸收多屏并集边界的取整误差。
    /// 纯几何，不依赖任何屏的具体排列 —— 副屏在左/右/上/下都成立。
    /// </summary>
    internal static DesktopSides GetOuterSides(Rect monitorBoundsPhysical, double tolerance = 1.0)
    {
        var virtualBounds = VirtualScreenBoundsPhysical();
        if (virtualBounds.IsEmpty || monitorBoundsPhysical.IsEmpty)
        {
            // 拿不到整体边界：保守地认为都可以贴（等价于单屏行为），
            // 总比一律禁止贴边导致"功能像坏了"要好。
            return DesktopSides.All;
        }

        var sides = DesktopSides.None;

        if (Math.Abs(monitorBoundsPhysical.Left - virtualBounds.Left) <= tolerance)
        {
            sides |= DesktopSides.Left;
        }

        if (Math.Abs(monitorBoundsPhysical.Right - virtualBounds.Right) <= tolerance)
        {
            sides |= DesktopSides.Right;
        }

        if (Math.Abs(monitorBoundsPhysical.Top - virtualBounds.Top) <= tolerance)
        {
            sides |= DesktopSides.Top;
        }

        if (Math.Abs(monitorBoundsPhysical.Bottom - virtualBounds.Bottom) <= tolerance)
        {
            sides |= DesktopSides.Bottom;
        }

        return sides;
    }

    /// <summary>窗口所在屏的「哪些边是真外边界」（DIP 无关，纯边界判定）。</summary>
    internal static DesktopSides GetOuterSidesForWindow(Window window)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var monitor = handle == IntPtr.Zero
            ? WindowNativeMethods.MonitorFromPoint(
                DipToPhysicalPoint(window.Left, window.Top), WindowNativeMethods.MONITOR_DEFAULTTONEAREST)
            : WindowNativeMethods.MonitorFromWindow(handle, WindowNativeMethods.MONITOR_DEFAULTTONEAREST);

        return GetOuterSides(GetMonitorBoundsPhysical(monitor));
    }

    /// <summary>
    /// 给定 DIP 坐标点所在屏的「哪些边是真外边界」。
    /// 用于窗口尚无句柄的启动恢复阶段（那时只能靠记录坐标判断它在哪块屏）。
    /// 注意：跨屏 DPI 不同时，此处按系统 DPI 近似换算点位，仅用于「落在哪块屏」的方向性判断，
    /// 不参与精确坐标计算，因此这点误差无影响。
    /// </summary>
    internal static DesktopSides GetOuterSidesForDipPoint(double dipX, double dipY)
    {
        var monitor = WindowNativeMethods.MonitorFromPoint(
            DipToPhysicalPoint(dipX, dipY), WindowNativeMethods.MONITOR_DEFAULTTONEAREST);
        return GetOuterSides(GetMonitorBoundsPhysical(monitor));
    }

    /// <summary>所有显示器并集的边界（物理像素）。可能含负坐标。</summary>
    internal static Rect VirtualScreenBoundsPhysical()
    {
        var x = WindowNativeMethods.GetSystemMetrics(WindowNativeMethods.SM_XVIRTUALSCREEN);
        var y = WindowNativeMethods.GetSystemMetrics(WindowNativeMethods.SM_YVIRTUALSCREEN);
        var w = WindowNativeMethods.GetSystemMetrics(WindowNativeMethods.SM_CXVIRTUALSCREEN);
        var h = WindowNativeMethods.GetSystemMetrics(WindowNativeMethods.SM_CYVIRTUALSCREEN);

        if (w <= 0 || h <= 0)
        {
            return Rect.Empty;
        }

        return new Rect(x, y, w, h);
    }

    /// <summary>DIP 点 → 物理像素点。</summary>
    private static POINT DipToPhysicalPoint(double dipX, double dipY)
    {
        var scale = GetSystemDpiScale();
        return new POINT { X = (int)Math.Round(dipX * scale), Y = (int)Math.Round(dipY * scale) };
    }

    /// <summary>取某显示器的**显示区**（物理像素，含任务栏）。monitor 为零时按主屏处理。</summary>
    internal static Rect GetMonitorBoundsPhysical(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero)
        {
            monitor = WindowNativeMethods.MonitorFromPoint(
                new POINT { X = 0, Y = 0 }, WindowNativeMethods.MONITOR_DEFAULTTONEAREST);
        }

        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !WindowNativeMethods.GetMonitorInfoW(monitor, ref info))
        {
            return Rect.Empty;
        }

        var r = info.rcMonitor;
        return new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>
    /// 取指定窗口所在显示器的工作区（DIP）。
    /// 窗口尚无句柄时回退到「窗口逻辑坐标所在的显示器」。
    /// </summary>
    internal static Rect WorkAreaForWindow(Window window)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;

        if (handle == IntPtr.Zero)
        {
            return WorkAreaForDipPoint(window.Left, window.Top);
        }

        var monitor = WindowNativeMethods.MonitorFromWindow(handle, WindowNativeMethods.MONITOR_DEFAULTTONEAREST);
        var work = GetWorkAreaPhysical(monitor, fallbackToPrimary: true);
        return ToDipRect(work, GetDpiScaleForHandle(handle));
    }

    /// <summary>
    /// 取「包含给定 DIP 坐标点」的显示器工作区（DIP）。
    ///
    /// 用于窗口尚无句柄的启动恢复阶段：只能拿记录下来的坐标去找它当初所在的屏，
    /// 否则会一律按主屏工作区夹取 —— 副屏上的便签会被拽回主屏。
    /// </summary>
    internal static Rect WorkAreaForDipPoint(double dipX, double dipY)
    {
        var physical = WorkAreaPhysicalForPoint(dipX * GetSystemDpiScale(), dipY * GetSystemDpiScale());
        return ToDipRect(physical, GetSystemDpiScale());
    }

    /// <summary>
    /// 取「包含给定**物理像素**坐标点」的显示器工作区（**物理像素**）。
    /// 供已经持有 <c>GetCursorPos</c> 结果（物理像素）的调用方使用。
    /// </summary>
    internal static Rect WorkAreaPhysicalForPoint(double physicalX, double physicalY)
    {
        var pt = new POINT { X = (int)Math.Round(physicalX), Y = (int)Math.Round(physicalY) };
        var monitor = WindowNativeMethods.MonitorFromPoint(pt, WindowNativeMethods.MONITOR_DEFAULTTONEAREST);
        return GetWorkAreaPhysical(monitor, fallbackToPrimary: true);
    }

    /// <summary>取指定窗口所在显示器的 DPI 缩放（150% → 1.5）。</summary>
    internal static double GetDpiScaleForHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return GetSystemDpiScale();
        }

        var dpi = WindowNativeMethods.GetDpiForWindow(handle);
        return dpi <= 0 ? 1.0 : dpi / 96.0;
    }

    /// <summary>系统级 DPI 缩放（主屏）。</summary>
    private static double GetSystemDpiScale()
    {
        var dpi = WindowNativeMethods.GetDpiForSystem();
        return dpi <= 0 ? 1.0 : dpi / 96.0;
    }

    /// <summary>取某显示器的工作区（物理像素）。monitor 为零时按主屏处理。</summary>
    private static Rect GetWorkAreaPhysical(IntPtr monitor, bool fallbackToPrimary)
    {
        if (monitor == IntPtr.Zero)
        {
            if (!fallbackToPrimary)
            {
                return Rect.Empty;
            }

            // (0,0) 一定落在主屏
            monitor = WindowNativeMethods.MonitorFromPoint(
                new POINT { X = 0, Y = 0 }, WindowNativeMethods.MONITOR_DEFAULTTONEAREST);
        }

        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !WindowNativeMethods.GetMonitorInfoW(monitor, ref info))
        {
            return Rect.Empty;
        }

        var r = info.rcWork;
        return new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>物理像素矩形 → DIP 矩形（除以该屏 DPI 缩放）。查询失败时退回主屏工作区。</summary>
    private static Rect ToDipRect(Rect physical, double scale)
    {
        if (physical.IsEmpty || (physical.Width <= 0 && physical.Height <= 0))
        {
            // 查询失败：退回主屏工作区语义，避免把窗口夹到 (0,0) 而消失
            return SystemParameters.WorkArea;
        }

        if (scale <= 0)
        {
            scale = 1.0;
        }

        return new Rect(
            physical.Left / scale,
            physical.Top / scale,
            physical.Width / scale,
            physical.Height / scale);
    }
}
