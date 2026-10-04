using System.Runtime.InteropServices;

namespace NoteApp.Native;

/// <summary>用户界面相关的 P/Invoke 封装（用于单实例激活已有窗口）。</summary>
internal static class WindowNativeMethods
{
    internal const int SW_RESTORE = 9;
    internal const int SW_SHOW = 5;

    // 置顶不抢焦点：WS_EX_NOACTIVATE 扩展样式 + WM_MOUSEACTIVATE 返回 MA_NOACTIVATE
    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_NOACTIVATE = 0x08000000;
    internal const int WM_MOUSEACTIVATE = 0x0021;
    internal const int MA_NOACTIVATE = 3;

    // 窗口层级调整（SetWindowPos）
    internal static readonly IntPtr HWND_TOP = IntPtr.Zero;
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOACTIVATE = 0x0010;

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    /// <summary>获取窗口扩展样式（32/64 位自适应）。</summary>
    internal static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    /// <summary>设置窗口扩展样式（32/64 位自适应）。</summary>
    internal static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>获取屏幕坐标下的鼠标光标位置。</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    // ---- 全局快捷键 ----

    internal const int WM_HOTKEY = 0x0312;
    internal const uint MOD_ALT = 0x0001;
    internal const uint MOD_CONTROL = 0x0002;
    internal const uint MOD_SHIFT = 0x0004;
    internal const uint MOD_WIN = 0x0008;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ---- Mica / Acrylic 材质（DWM 系统背景） ----

    internal const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    internal const int DWMSBT_MAINWINDOW = 2;       // Mica
    internal const int DWMSBT_TRANSIENTWINDOW = 3;  // Acrylic（弹窗）

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    // ---- DPI ----

    /// <summary>
    /// 取某窗口所在显示器的 DPI（96 = 100%）。
    /// 需要 Per-Monitor DPI 感知；调用方自己处理失败情况。
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>取系统（主显示器感知）DPI。拿不到窗口句柄时的兜底换算用。</summary>
    [DllImport("user32.dll")]
    internal static extern uint GetDpiForSystem();

    /// <summary>取指定显示器的 DPI（每屏 DPI 不同时唯一可靠的取值方式）。</summary>
    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary><c>GetDpiForMonitor</c> 的「有效 DPI」类型（缩放设置里的那个值）。</summary>
    internal const int MDT_EFFECTIVE_DPI = 0;

    // ---- 显示器查询（多屏支持） ----

    /// <summary>取「离指定点最近」的显示器（点落在两屏之间的空隙时也不返回 null）。</summary>
    internal const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    // ---- 虚拟桌面整体边界 ----

    /// <summary>虚拟桌面左上角 X（所有显示器并集的左边界，物理像素，可能为负）。</summary>
    internal const int SM_XVIRTUALSCREEN = 76;

    /// <summary>虚拟桌面左上角 Y（可能为负：副屏在主屏上方时）。</summary>
    internal const int SM_YVIRTUALSCREEN = 77;

    /// <summary>虚拟桌面宽度（所有显示器并集的宽度）。</summary>
    internal const int SM_CXVIRTUALSCREEN = 78;

    /// <summary>虚拟桌面高度。</summary>
    internal const int SM_CYVIRTUALSCREEN = 79;

    /// <summary>
    /// 取系统度量。用于拿「所有显示器并集」的边界 ——
    /// 据此判断某块屏的某条边外面还有没有别的屏（即是否为真实桌面外边界）。
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);

    // ---- 全局低级鼠标钩子（框选批量拖动用） ----

    /// <summary>
    /// 低级鼠标钩子 id。挂在 <c>WH_MOUSE_LL</c> 上，能在**任何窗口之外**收到鼠标消息
    /// —— 桌面空白处不属于任何我方窗口，没有它就收不到"在桌面上按下并拖动"这个动作。
    /// </summary>
    internal const int WH_MOUSE_LL = 14;

    // 钩子会收到的鼠标消息
    internal const int WM_MOUSEMOVE = 0x0200;
    internal const int WM_LBUTTONDOWN = 0x0201;
    internal const int WM_LBUTTONUP = 0x0202;
    internal const int WM_RBUTTONDOWN = 0x0204;
    internal const int WM_RBUTTONUP = 0x0205;
    internal const int WM_MBUTTONDOWN = 0x0207;
    internal const int WM_MBUTTONUP = 0x0208;

    /// <summary>
    /// 低级鼠标钩子回调。
    ///
    /// ⚠️ 这个委托**必须**由调用方用字段持有引用，绝不能让它在注册后变成可回收对象 ——
    /// 一旦被 GC 回收，系统会往一个已失效的地址回调，直接进程崩溃。
    /// </summary>
    internal delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookExW(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>取当前模块句柄（传给 <c>SetWindowsHookEx</c> 的 hMod）。</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr GetModuleHandleW(string? lpModuleName);

    /// <summary>
    /// 合成一次鼠标输入（按下/抬起/移动/滚轮）。
    ///
    /// ⚠️ <c>mouse_event</c> 是**过时 API**，微软推荐用 <c>SendInput</c>。
    /// 这里仍用它是因为它接受**绝对屏幕坐标**（<c>MOUSEEVENTF_ABSOLUTE</c>），
    /// 一次调用就能把「移动到某点并按下/抬起」表达清楚，不需要先 <c>SetCursorPos</c>
    /// 再补一次输入事件 —— 后者在低级钩子下会看到"光标跳过去了但没经过中间路径"。
    ///
    /// 🔴 <c>dx/dy</c> 在 <c>MOUSEEVENTF_ABSOLUTE</c> 下是 **0..65535 的归一化值**，
    /// 不是像素。换算公式见 <see cref="NormalizedAbsoluteCoordinate"/>。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    /// <summary>鼠标事件标志位（<c>mouse_event</c> 的 <c>dwFlags</c>）。</summary>
    internal const uint MOUSEEVENTF_MOVE = 0x0001;

    internal const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    internal const uint MOUSEEVENTF_LEFTUP = 0x0004;
    internal const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    internal const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    internal const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    internal const uint MOUSEEVENTF_MIDDLEUP = 0x0040;

    /// <summary>坐标使用绝对位置（相对虚拟桌面左上角）。</summary>
    internal const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    /// <summary>
    /// 把虚拟桌面坐标（物理像素，可能为负）换算成 <c>mouse_event</c> 需要的
    /// 0..65535 归一化绝对坐标。
    ///
    /// 公式来自微软文档：<c>normalized = round(65535 * (x - virtualLeft) / virtualWidth)</c>。
    /// 不做这个换算而直接把像素值填进去，光标会跑到完全无关的位置
    /// （因为 65535 被解释成"整个虚拟桌面"）。
    /// </summary>
    internal static (uint X, uint Y) NormalizedAbsoluteCoordinate(int x, int y)
    {
        var left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var width = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN));
        var height = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));

        var nx = (uint)Math.Round(65535.0 * (x - left) / width);
        var ny = (uint)Math.Round(65535.0 * (y - top) / height);
        return (nx, ny);
    }
}

/// <summary>
/// 低级鼠标钩子的回调数据（<c>MSLLHOOKSTRUCT</c>）。
///
/// ⚠️ 字段顺序与布局必须与 Win32 完全一致，否则 <c>pt</c> 读出来是垃圾值，
/// 表现为框选起点随机跳。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MSLLHOOKSTRUCT
{
    /// <summary>屏幕坐标**物理像素**（不是 DIP，使用前必须按屏换算）。</summary>
    public POINT pt;

    public uint mouseData;
    public uint flags;
    public uint time;
    public IntPtr dwExtraInfo;
}

/// <summary>屏幕坐标点（P/Invoke GetCursorPos 使用）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;
}

/// <summary>矩形（物理像素）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

/// <summary>显示器信息（<c>GetMonitorInfoW</c> 使用）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MONITORINFO
{
    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
}
