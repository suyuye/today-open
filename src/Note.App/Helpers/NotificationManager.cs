using NoteApp.Services;
using NoteApp.Views;

namespace NoteApp.Helpers;

/// <summary>提醒通知管理器：订阅 ReminderTriggered，队列式逐个显示右下角通知弹窗（上一个关闭后再显示下一个）。</summary>
public class NotificationManager
{
    private readonly Queue<ReminderEventArgs> _queue = new();
    private ReminderNotification? _current;

    public NotificationManager(IReminderService reminderService)
    {
        reminderService.ReminderTriggered += OnReminderTriggered;
    }

    private void OnReminderTriggered(object? sender, ReminderEventArgs e)
    {
        // 便签已删除（理论上级联删除已移除提醒，此处兜底）→ 不显示
        if (e.Note is null)
        {
            return;
        }

        _queue.Enqueue(e);
        ShowNext();
    }

    private void ShowNext()
    {
        if (_current is not null || _queue.Count == 0)
        {
            return;
        }

        var e = _queue.Dequeue();
        var notification = new ReminderNotification(e.Reminder, e.Note!);
        _current = notification;
        notification.Closed += (_, _) =>
        {
            _current = null;
            ShowNext();
        };
        notification.Show();
    }
}
