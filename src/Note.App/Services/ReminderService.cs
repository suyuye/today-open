using System.Windows.Threading;

using NoteApp.Data.Repositories;
using NoteApp.Models;
using NoteApp.Models.Enums;

using Serilog;

namespace NoteApp.Services;

/// <summary>提醒调度服务：管理提醒的增删改查，每分钟检查到期提醒并按重复规则推进。</summary>
public class ReminderService : IReminderService
{
    private const int CheckIntervalSeconds = 60;

    private readonly IReminderRepository _reminderRepository;
    private readonly INoteRepository _noteRepository;
    private readonly ISettingsService _settingsService;
    private readonly ILogger _logger;

    private DispatcherTimer? _timer;

    public ReminderService(
        IReminderRepository reminderRepository,
        INoteRepository noteRepository,
        ISettingsService settingsService,
        ILogger logger)
    {
        _reminderRepository = reminderRepository;
        _noteRepository = noteRepository;
        _settingsService = settingsService;
        _logger = logger;
    }

    public event EventHandler<ReminderEventArgs>? ReminderTriggered;
    public event EventHandler<ReminderEventArgs>? ReminderCreated;
    public event EventHandler<ReminderEventArgs>? ReminderRemoved;

    // ===== CRUD =====

    public Reminder? GetReminder(int id) => _reminderRepository.GetById(id);

    public List<Reminder> GetRemindersForNote(Guid noteId) => _reminderRepository.GetByNoteId(noteId);

    public Reminder SetReminder(Guid noteId, DateTime remindAt, RepeatRule repeat = RepeatRule.None)
    {
        var reminder = _reminderRepository.Create(new Reminder
        {
            NoteId = noteId,
            RemindAt = remindAt,
            RepeatRule = repeat,
        });

        _logger.Information($"创建提醒 {reminder.Id}：便签 {noteId}，时间 {remindAt:yyyy-MM-dd HH:mm:ss}，重复 {repeat}");
        ReminderCreated?.Invoke(this, new ReminderEventArgs(reminder, _noteRepository.GetById(noteId)));
        return reminder;
    }

    public void UpdateReminder(int id, DateTime remindAt, RepeatRule repeat)
    {
        var reminder = _reminderRepository.GetById(id);
        if (reminder is null)
        {
            _logger.Warning($"更新提醒失败：提醒 {id} 不存在");
            return;
        }

        reminder.RemindAt = remindAt;
        reminder.RepeatRule = repeat;
        _reminderRepository.Update(reminder);

        _logger.Information($"更新提醒 {id}：时间 {remindAt:yyyy-MM-dd HH:mm:ss}，重复 {repeat}");
        ReminderCreated?.Invoke(this, new ReminderEventArgs(reminder, _noteRepository.GetById(reminder.NoteId)));
    }

    public void RemoveReminder(int id)
    {
        var reminder = _reminderRepository.GetById(id);
        _reminderRepository.Delete(id);

        _logger.Information($"删除提醒 {id}");
        if (reminder is not null)
        {
            ReminderRemoved?.Invoke(this, new ReminderEventArgs(reminder, _noteRepository.GetById(reminder.NoteId)));
        }
    }

    public void RemoveRemindersForNote(Guid noteId)
    {
        var reminders = _reminderRepository.GetByNoteId(noteId);
        _reminderRepository.DeleteByNoteId(noteId);

        _logger.Information($"删除便签 {noteId} 的 {reminders.Count} 个提醒");
        var note = _noteRepository.GetById(noteId);
        foreach (var reminder in reminders)
        {
            ReminderRemoved?.Invoke(this, new ReminderEventArgs(reminder, note));
        }
    }

    // ===== 调度 =====

    public void Start()
    {
        if (_timer is not null)
        {
            return;
        }

        // 启动时立即检查一次，处理应用关闭期间到期的提醒
        CheckDueReminders();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(CheckIntervalSeconds),
        };
        _timer.Tick += OnTimerTick;
        _timer.Start();

        _logger.Information($"提醒调度器已启动（每 {CheckIntervalSeconds} 秒检查一次）。");
    }

    public void Stop()
    {
        if (_timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _timer = null;
        _logger.Information("提醒调度器已停止。");
    }

    public void Snooze(int reminderId, int minutes)
    {
        var reminder = _reminderRepository.GetById(reminderId);
        if (reminder is null)
        {
            _logger.Warning($"稍后提醒失败：提醒 {reminderId} 不存在");
            return;
        }

        reminder.RemindAt = DateTime.Now.AddMinutes(minutes);
        reminder.IsCompleted = false;
        _reminderRepository.Update(reminder);

        _logger.Information($"稍后提醒 {reminderId}：{minutes} 分钟后（{reminder.RemindAt:yyyy-MM-dd HH:mm:ss}）");
        ReminderCreated?.Invoke(this, new ReminderEventArgs(reminder, _noteRepository.GetById(reminder.NoteId)));
    }

    public void CompleteReminder(int id)
    {
        var reminder = _reminderRepository.GetById(id);
        if (reminder is null)
        {
            _logger.Warning($"完成提醒失败：提醒 {id} 不存在");
            return;
        }

        reminder.IsCompleted = true;
        _reminderRepository.MarkCompleted(id);

        _logger.Information($"完成提醒 {id}");
        ReminderRemoved?.Invoke(this, new ReminderEventArgs(reminder, _noteRepository.GetById(reminder.NoteId)));
    }

    // ===== 查询 =====

    public List<Reminder> GetUpcomingReminders(int count = 10)
    {
        var now = DateTime.Now;
        var horizon = now.AddDays(7);
        return _reminderRepository.GetAll()
            .Where(r => !r.IsCompleted && r.RemindAt >= now && r.RemindAt <= horizon)
            .OrderBy(r => r.RemindAt)
            .Take(count)
            .ToList();
    }

    public List<Reminder> GetPendingReminders() =>
        _reminderRepository.GetAll()
            .Where(r => !r.IsCompleted && r.RemindAt > DateTime.Now)
            .OrderBy(r => r.RemindAt)
            .ToList();

    public bool HasReminder(Guid noteId) =>
        _reminderRepository.GetByNoteId(noteId).Any(r => !r.IsCompleted);

    private void OnTimerTick(object? sender, EventArgs e) => CheckDueReminders();

    /// <summary>检查到期提醒：触发事件；单次提醒标记完成，重复提醒计算并写入下次时间。</summary>
    private void CheckDueReminders()
    {
        try
        {
            var now = DateTime.Now;
            var due = _reminderRepository.GetUpcoming(now);

            foreach (var reminder in due)
            {
                _logger.Information($"提醒到期：{reminder.Id}（便签 {reminder.NoteId}，原定 {reminder.RemindAt:yyyy-MM-dd HH:mm:ss}）");

                // 先触发事件（携带到期原定时间 + 关联便签），再推进
                ReminderTriggered?.Invoke(this, new ReminderEventArgs(reminder, _noteRepository.GetById(reminder.NoteId)));

                if (reminder.RepeatRule == RepeatRule.None)
                {
                    _reminderRepository.MarkCompleted(reminder.Id);
                }
                else
                {
                    reminder.RemindAt = CalculateNextOccurrence(reminder.RemindAt, reminder.RepeatRule);
                    _reminderRepository.Update(reminder);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "提醒调度检查失败");
        }
    }

    /// <summary>按重复规则计算下一次提醒时间（以原定提醒时间为基准，时分秒保持不变）。</summary>
    private static DateTime CalculateNextOccurrence(DateTime current, RepeatRule rule) => rule switch
    {
        RepeatRule.Daily => current.AddDays(1),
        RepeatRule.Weekly => current.AddDays(7),
        RepeatRule.Workdays => NextWeekday(current),
        RepeatRule.Monthly => current.AddMonths(1),
        _ => current,
    };

    /// <summary>计算下一个工作日：从次日起跳过周六、周日，保持原有时分秒。</summary>
    private static DateTime NextWeekday(DateTime current)
    {
        var next = current.AddDays(1);
        while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            next = next.AddDays(1);
        }

        return next;
    }
}
