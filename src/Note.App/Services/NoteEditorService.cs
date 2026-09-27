using System.Windows;

using NoteApp.Helpers;
using NoteApp.Views;

namespace NoteApp.Services;

/// <summary>便签新建/删除编排：定位、聚焦、撤销 Toast。数据读写经 INoteService，窗口经 IWindowService。</summary>
public class NoteEditorService : INoteEditorService
{
    private readonly INoteService _noteService;
    private readonly ISettingsService _settingsService;
    private readonly IWindowService _windowService;

    public NoteEditorService(INoteService noteService, ISettingsService settingsService, IWindowService windowService)
    {
        _noteService = noteService;
        _settingsService = settingsService;
        _windowService = windowService;
    }

    public Guid CreateNewNote(Point? mousePosition = null)
    {
        var note = _noteService.CreateNote(color: _settingsService.DefaultColor);

        int x, y;
        if (mousePosition.HasValue)
        {
            x = (int)mousePosition.Value.X - 100;
            y = (int)mousePosition.Value.Y - 50;
        }
        else
        {
            var count = _noteService.GetAllNotes(includeArchived: false).Count;
            x = 100 + count * 30;
            y = 100 + count * 30;
        }

        note.Width = 240;
        note.Height = 200;
        note.Opacity = _settingsService.DefaultOpacity;
        // 夹取到「目标点所在的显示器」工作区，否则在副屏上新建的便签会被拽回主屏。
        // mousePosition 是物理像素，工作区也是物理像素，直接比较即可；
        // 级联摆放（无鼠标位置）时按主屏处理。
        var workArea = mousePosition.HasValue
            ? MonitorHelper.WorkAreaPhysicalForPoint(mousePosition.Value.X, mousePosition.Value.Y)
            : MonitorHelper.PrimaryWorkArea();
        note.PositionX = Math.Clamp(x, (int)workArea.Left, (int)workArea.Right - note.Width);
        note.PositionY = Math.Clamp(y, (int)workArea.Top, (int)workArea.Bottom - note.Height);

        _noteService.UpdateNote(note);
        _windowService.ShowNoteWindow(note.Id);
        _windowService.GetWindow(note.Id)?.FocusEditor();

        LogHelper.Debug($"新建便签：{note.Id}，位置：({note.PositionX}, {note.PositionY})");
        return note.Id;
    }

    public void DeleteNote(Guid noteId, bool permanent = false)
    {
        _noteService.DeleteNote(noteId, permanent);
        _windowService.CloseNoteWindow(noteId);

        if (permanent)
        {
            return;
        }

        ToastWindow.Show("便签已删除", "撤销", () =>
        {
            _noteService.RestoreNote(noteId);
            _windowService.ShowNoteWindow(noteId);
        });
    }
}
