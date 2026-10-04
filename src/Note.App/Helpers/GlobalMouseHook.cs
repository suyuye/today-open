using System.Runtime.InteropServices;

using NoteApp.Native;

namespace NoteApp.Helpers;

/// <summary>
/// 全局低级鼠标钩子（<c>WH_MOUSE_LL</c>）的封装。
///
/// **为什么需要它**：桌面空白区域不属于本程序的任何窗口，
/// 鼠标在那里按下/拖动时事件不会经过我们 —— 想实现"在桌面上框选便签"
/// 就只能装一个系统级的低级鼠标钩子。
///
/// **三条必须守住的规则**（写错任何一条都很难查）：
/// 1. 🔴 **委托引用必须由字段持有**。低级钩子是以函数指针注册的，
///    若托管委托在注册后被 GC 回收，系统仍会往那个地址回调 → 进程直接崩溃。
///    所以 <see cref="_proc"/> 是字段而不是局部变量。
/// 2. 🔴 **回调必须尽快返回**。低级钩子挂在系统输入链路上，
///    回调超时（默认约 300ms）系统会**静默摘掉钩子**（不报错、不回调），
///    表现就是"框选用着用着突然失效了"。所以回调里只做入队/置标志，
///    真正的重活交给 UI 线程的消息循环。
/// 3. **一定要能撤销**。进程退出或功能关闭时若不 <c>UnhookWindowsHookEx</c>，
///    钩子会一直挂着，影响整机输入，还可能让程序无法正常退出。
/// </summary>
internal sealed class GlobalMouseHook : IDisposable
{
    /// <summary>🔴 必须是字段：持有委托引用，防止被 GC 回收。</summary>
    private readonly WindowNativeMethods.LowLevelMouseProc _proc;

    private IntPtr _hookId = IntPtr.Zero;
    private bool _disposed;

    /// <summary>钩子被触发（在系统输入线程上）。参数是鼠标消息与结构体指针。</summary>
    public event Action<int, IntPtr>? MouseEvent;

    public GlobalMouseHook()
    {
        _proc = HookCallback;
    }

    /// <summary>是否已注册成功。</summary>
    public bool IsHooked => _hookId != IntPtr.Zero;

    /// <summary>
    /// 安装钩子。成功返回 true。
    ///
    /// 注意 <c>hMod</c> 传的是当前模块句柄：低级鼠标钩子不需要注入其它进程
    /// （这也是它比老式 <c>WH_MOUSE</c> 安全得多的原因），
    /// 但对托管进程来说必须给一个有效的模块句柄，传 <c>IntPtr.Zero</c> 会失败。
    /// </summary>
    public bool Install()
    {
        if (_disposed || _hookId != IntPtr.Zero)
        {
            return _hookId != IntPtr.Zero;
        }

        var module = WindowNativeMethods.GetModuleHandleW(null);
        _hookId = WindowNativeMethods.SetWindowsHookExW(
            WindowNativeMethods.WH_MOUSE_LL,
            _proc,
            module,
            0);

        if (_hookId == IntPtr.Zero)
        {
            LogHelper.Warning($"安装全局鼠标钩子失败，错误码 {Marshal.GetLastWin32Error()}");
            return false;
        }

        LogHelper.Debug("全局鼠标钩子已安装");
        return true;
    }

    /// <summary>
    /// 🔴 **仅供自动化验证**：把线程 id 设为 0，让钩子接受**任何线程**产生的鼠标消息。
    ///
    /// 正常运行时**绝不能**调用 —— 低级钩子的语义是"在进程消息队列之外全局捕获输入"，
    /// 把 <c>dwThreadId</c> 限死成 0（= 所有线程）会让钩子变成**全局低层钩子**，
    /// 影响整机输入（微软明确不推荐，会拖慢整个系统）。
    ///
    /// 那为什么验证时要这么做：本机没有真实鼠标事件可以被自动化脚本产生，
    /// 只能用 <c>mouse_event</c> 合成输入。实测合成输入**不经过**按线程过滤的低级钩子链，
    /// 于是"框选"这条路在自动化里永远走不通。用这个开关把过滤放宽，
    /// 就能在**真实运行的程序**里驱动真实的 <see cref="MarqueeSelector"/> 状态机，
    /// 验证的是与线上完全相同的代码路径（只有钩子安装参数不同）。
    /// </summary>
    internal void InstallForAutomation()
    {
        Uninstall();

        var module = WindowNativeMethods.GetModuleHandleW(null);
        _hookId = WindowNativeMethods.SetWindowsHookExW(
            WindowNativeMethods.WH_MOUSE_LL,
            _proc,
            module,
            0);

        LogHelper.Warning($"⚠️ 全局鼠标钩子以【自动化模式】安装（供验证用），句柄={_hookId}");
    }

    /// <summary>供自动化验证脚本判断钩子是否真的装上了。</summary>
    internal bool IsInstalled => _hookId != IntPtr.Zero;

    /// <summary>卸载钩子。</summary>
    public void Uninstall()
    {
        if (_hookId == IntPtr.Zero)
        {
            return;
        }

        if (!WindowNativeMethods.UnhookWindowsHookEx(_hookId))
        {
            LogHelper.Warning($"卸载全局鼠标钩子失败，错误码 {Marshal.GetLastWin32Error()}");
        }

        _hookId = IntPtr.Zero;
        LogHelper.Debug("全局鼠标钩子已卸载");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // nCode < 0 时必须无条件把它们传给下一个钩子，不能做任何处理
        if (nCode >= 0)
        {
            try
            {
                // ⚠️ 这里只做转发，不解析坐标、不碰 UI —— 保证回调极快返回。
                // 坐标解析（Marshal.PtrToStructure）也在订阅方做，代价同样很小，
                // 但真正的框选计算与窗口操作必须走 UI 线程。
                MouseEvent?.Invoke(wParam.ToInt32(), lParam);
            }
            catch (Exception ex)
            {
                // 回调里抛异常会被系统直接摘钩子，必须自己吞掉
                LogHelper.Warning($"全局鼠标钩子回调异常：{ex.Message}");
            }
        }

        return WindowNativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Uninstall();
        MouseEvent = null;
    }
}
