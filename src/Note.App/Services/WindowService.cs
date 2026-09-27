using System.Windows;
using System.Windows.Interop;

using NoteApp.Helpers;
using NoteApp.Native;
using NoteApp.Views;

namespace NoteApp.Services;

/// <summary>便签窗口管理服务：创建 / 显示 / 隐藏 / 关闭 / 层级排序。数据读写全部经 INoteService。</summary>
public class WindowService : IWindowService
{
    private readonly Dictionary<Guid, NoteWindow> _openWindows = new();
    private readonly INoteService _noteService;

    public WindowService(INoteService noteService)
    {
        _noteService = noteService;
    }

    public event EventHandler<Guid>? WindowOpened;
    public event EventHandler<Guid>? WindowClosed;

    // ===== 窗口管理 =====

    public void ShowNoteWindow(Guid noteId)
    {
        if (_openWindows.TryGetValue(noteId, out var existing))
        {
            ActivateExistingWindow(existing);
            return;
        }

        if (_noteService.GetNote(noteId) is null)
        {
            LogHelper.Warning($"打开便签窗口失败：便签 {noteId} 不存在或已删除");
            return;
        }

        var window = new NoteWindow(noteId);
        _openWindows[noteId] = window;
        window.Closed += (_, _) => OnWindowClosed(noteId);
        window.Activated += (_, _) => OnWindowActivated(noteId);

        window.Show();
        WindowOpened?.Invoke(this, noteId);
        LogHelper.Debug($"打开便签窗口：{noteId}");
    }

    public void HideNoteWindow(Guid noteId)
    {
        if (_openWindows.TryGetValue(noteId, out var window))
        {
            window.Hide();
        }
    }

    public void CloseNoteWindow(Guid noteId)
    {
        if (_openWindows.TryGetValue(noteId, out var window))
        {
            window.Close();
        }
    }

    public void FocusNoteWindow(Guid noteId)
    {
        if (!_openWindows.TryGetValue(noteId, out var window))
        {
            return;
        }

        window.Activate();
        if (IsPinned(noteId))
        {
            BringToFront(noteId);
        }
    }

    // ===== 批量 =====

    public void ShowAllNoteWindows()
    {
        foreach (var window in _openWindows.Values)
        {
            window.Show();
        }

        foreach (var note in _noteService.GetAllNotes())
        {
            if (!_openWindows.ContainsKey(note.Id))
            {
                ShowNoteWindow(note.Id);
            }
        }
    }

    public void HideAllNoteWindows()
    {
        foreach (var window in _openWindows.Values)
        {
            window.Hide();
        }
    }

    public void ToggleAllNoteWindows()
    {
        if (_openWindows.Values.Any(w => w.IsVisible))
        {
            HideAllNoteWindows();
        }
        else
        {
            ShowAllNoteWindows();
        }
    }

    public void CloseAllNoteWindows()
    {
        foreach (var noteId in _openWindows.Keys.ToList())
        {
            CloseNoteWindow(noteId);
        }
    }

    // ===== 状态查询 =====

    public bool IsWindowOpen(Guid noteId) => _openWindows.ContainsKey(noteId);

    public List<Guid> GetOpenWindowIds() => _openWindows.Keys.ToList();

    public NoteWindow? GetWindow(Guid noteId) => _openWindows.GetValueOrDefault(noteId);

    // ===== 窗口层级 =====

    public void BringToFront(Guid noteId)
    {
        if (!_openWindows.TryGetValue(noteId, out var window))
        {
            return;
        }

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        WindowNativeMethods.SetWindowPos(
            hwnd,
            WindowNativeMethods.HWND_TOP,
            0, 0, 0, 0,
            WindowNativeMethods.SWP_NOMOVE | WindowNativeMethods.SWP_NOSIZE | WindowNativeMethods.SWP_NOACTIVATE);
    }

    public void UpdateZOrder()
    {
        // 置顶便签按 ZIndex 升序依次提升到最前（ZIndex 越大越靠上）；非置顶由系统默认管理。
        var pinned = new List<(Guid Id, int ZIndex)>();
        foreach (var noteId in _openWindows.Keys)
        {
            var note = _noteService.GetNote(noteId);
            if (note?.IsPinned == true)
            {
                pinned.Add((noteId, note.ZIndex));
            }
        }

        foreach (var (noteId, _) in pinned.OrderBy(x => x.ZIndex))
        {
            BringToFront(noteId);
        }
    }

    // ===== 位置与尺寸 =====

    public void SaveWindowState(Guid noteId)
    {
        // 位置/尺寸已由 NoteWindowViewModel 在移动/缩放时通过 300ms 防抖自动保存，无额外动作。
    }

    public void RestoreWindowState(Guid noteId)
    {
        // 位置/尺寸在 NoteWindow 构造时（RestorePositionAndSize）自动恢复，无额外动作。
    }

    // ===== 内部 =====

    private void OnWindowClosed(Guid noteId)
    {
        _openWindows.Remove(noteId);
        WindowClosed?.Invoke(this, noteId);
        LogHelper.Debug($"关闭便签窗口：{noteId}");
    }

    private void OnWindowActivated(Guid noteId)
    {
        if (IsPinned(noteId))
        {
            BringToFront(noteId);
        }
    }

    private bool IsPinned(Guid noteId) => _noteService.GetNote(noteId)?.IsPinned == true;

    private static void ActivateExistingWindow(NoteWindow window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Activate();
    }
}
