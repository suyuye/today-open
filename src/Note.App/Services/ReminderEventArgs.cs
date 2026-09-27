using NoteApp.Models;

namespace NoteApp.Services;

/// <summary>提醒事件参数：携带提醒及其关联的便签（便签可能已被删除，故可空）。</summary>
public class ReminderEventArgs : EventArgs
{
    public ReminderEventArgs(Reminder reminder, Note? note = null)
    {
        Reminder = reminder;
        Note = note;
    }

    public Reminder Reminder { get; }

    public Note? Note { get; }
}
