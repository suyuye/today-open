using NoteApp.Models;
using NoteApp.Models.Enums;

namespace NoteApp.Services;

/// <summary>提醒调度服务。</summary>
public interface IReminderService
{
    // CRUD
    Reminder? GetReminder(int id);
    List<Reminder> GetRemindersForNote(Guid noteId);
    Reminder SetReminder(Guid noteId, DateTime remindAt, RepeatRule repeat = RepeatRule.None);
    void UpdateReminder(int id, DateTime remindAt, RepeatRule repeat);
    void RemoveReminder(int id);
    void RemoveRemindersForNote(Guid noteId);

    // 调度
    void Start();
    void Stop();
    void Snooze(int reminderId, int minutes);
    void CompleteReminder(int id);

    // 查询
    List<Reminder> GetUpcomingReminders(int count = 10);
    List<Reminder> GetPendingReminders();
    bool HasReminder(Guid noteId);

    // 事件
    event EventHandler<ReminderEventArgs>? ReminderTriggered;
    event EventHandler<ReminderEventArgs>? ReminderCreated;
    event EventHandler<ReminderEventArgs>? ReminderRemoved;
}
