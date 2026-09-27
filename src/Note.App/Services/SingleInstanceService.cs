using System.Diagnostics;
using System.Threading;

using NoteApp.Native;

namespace NoteApp.Services;

/// <summary>
/// 通过命名 Mutex 确保单实例运行；第二个实例激活已有实例的窗口后退出。
/// </summary>
public class SingleInstanceService : ISingleInstanceService
{
    private const string MutexName = "TodayOpen_SingleInstance_Mutex_2026";

    // 持有 Mutex 引用，防止被 GC 回收导致锁失效
    private readonly Mutex _mutex;
    private readonly bool _createdNew;

    public SingleInstanceService()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out _createdNew);
    }

    public bool IsAlreadyRunning => !_createdNew;

    public void ActivateExistingInstance()
    {
        var currentProcessId = (uint)Environment.ProcessId;
        var currentExeName = Process.GetCurrentProcess().ProcessName;
        var target = IntPtr.Zero;

        WindowNativeMethods.EnumWindows((hWnd, _) =>
        {
            WindowNativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == 0 || pid == currentProcessId)
                return true; // 跳过当前进程与无效窗口，继续枚举

            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (string.Equals(process.ProcessName, currentExeName, StringComparison.OrdinalIgnoreCase))
                {
                    target = hWnd;
                    return false; // 找到目标窗口，停止枚举
                }
            }
            catch
            {
                // 进程可能已退出，忽略并继续
            }

            return true;
        }, IntPtr.Zero);

        if (target == IntPtr.Zero)
            return; // 已有实例暂无可见窗口（托盘态）

        if (WindowNativeMethods.IsIconic(target))
            _ = WindowNativeMethods.ShowWindow(target, WindowNativeMethods.SW_RESTORE);
        else
            _ = WindowNativeMethods.ShowWindow(target, WindowNativeMethods.SW_SHOW);

        _ = WindowNativeMethods.SetForegroundWindow(target);
    }

    /// <summary>释放持有的 Mutex（退出时调用）。</summary>
    public void Release()
    {
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 当前实例未持有 Mutex（第二个实例退出时），忽略
        }
    }
}
