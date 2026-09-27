using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using NoteApp.Models;
using NoteApp.Models.Enums;
using NoteApp.Services;

namespace NoteApp.ViewModels;

/// <summary>提醒设置弹窗 ViewModel：日期/时间/重复规则选择，保存创建或更新提醒。</summary>
public partial class ReminderPickerViewModel : ObservableObject
{
    private readonly IReminderService _reminderService;

    /// <summary>关联便签 ID。</summary>
    public Guid NoteId { get; }

    /// <summary>编辑模式下的已有提醒；为空表示新建。</summary>
    public Reminder? ExistingReminder { get; }

    /// <summary>请求关闭窗口（View 订阅后调用 Close()）。</summary>
    public event EventHandler? CloseRequested;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(IsPastTime))]
    private DateTime? _selectedDate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(IsPastTime))]
    private int _selectedHour;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(IsPastTime))]
    private int _selectedMinute;

    [ObservableProperty]
    private RepeatRule _selectedRepeatRule;

    /// <summary>小时选项（0-23）。</summary>
    public IReadOnlyList<int> Hours { get; } = Enumerable.Range(0, 24).ToList();

    /// <summary>分钟选项（0-55，步进 5）。</summary>
    public IReadOnlyList<int> Minutes { get; } = Enumerable.Range(0, 12).Select(i => i * 5).ToList();

    /// <summary>重复规则选项（显示名 + 枚举值）。</summary>
    public IReadOnlyList<RepeatRuleOption> RepeatOptions { get; } = new List<RepeatRuleOption>
    {
        new(RepeatRule.None, "无"),
        new(RepeatRule.Daily, "每天"),
        new(RepeatRule.Weekly, "每周"),
        new(RepeatRule.Workdays, "工作日"),
        new(RepeatRule.Monthly, "每月"),
    };

    public ReminderPickerViewModel(IReminderService reminderService, Guid noteId, Reminder? existingReminder)
    {
        _reminderService = reminderService;
        NoteId = noteId;
        ExistingReminder = existingReminder;

        if (existingReminder is not null)
        {
            _selectedDate = existingReminder.RemindAt.Date;
            _selectedHour = existingReminder.RemindAt.Hour;
            _selectedMinute = FloorToStep5(existingReminder.RemindAt.Minute);
            _selectedRepeatRule = existingReminder.RepeatRule;
        }
        else
        {
            var now = DateTime.Now;
            _selectedDate = now.Date;
            _selectedHour = now.Hour;
            _selectedMinute = FloorToStep5(now.Minute);
            _selectedRepeatRule = RepeatRule.None;
        }
    }

    /// <summary>所选日期时间是否已过（用于提示与禁用保存）。</summary>
    public bool IsPastTime => SelectedDate.HasValue && GetRemindAt() <= DateTime.Now;

    private bool CanSave() => SelectedDate.HasValue && GetRemindAt() > DateTime.Now;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (!SelectedDate.HasValue)
        {
            return;
        }

        var remindAt = GetRemindAt();
        if (ExistingReminder is not null)
        {
            _reminderService.UpdateReminder(ExistingReminder.Id, remindAt, SelectedRepeatRule);
        }
        else
        {
            _reminderService.SetReminder(NoteId, remindAt, SelectedRepeatRule);
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private DateTime GetRemindAt() =>
        SelectedDate!.Value.Date.AddHours(SelectedHour).AddMinutes(SelectedMinute);

    /// <summary>分钟向下取整到 5 的倍数（保证落入分钟选项 0/5/…/55）。</summary>
    private static int FloorToStep5(int minute) => minute / 5 * 5;
}

/// <summary>重复规则下拉选项。</summary>
public sealed record RepeatRuleOption(RepeatRule Value, string Display);
