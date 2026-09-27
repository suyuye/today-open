using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using NoteApp.Models;
using NoteApp.Models.Enums;
using NoteApp.Services;

namespace NoteApp.ViewModels;

/// <summary>提醒通知弹窗 ViewModel：展示便签标题/预览/颜色，处理「稍后」与「完成」。</summary>
public partial class ReminderNotificationViewModel : ObservableObject
{
    private readonly IReminderService _reminderService;
    private readonly IWindowService _windowService;
    private readonly ISettingsService _settingsService;
    private readonly Note _note;

    /// <summary>关联的提醒。</summary>
    public Reminder Reminder { get; }

    /// <summary>便签标题（无标题时取内容首行前 20 字）。</summary>
    public string NoteTitle { get; }

    /// <summary>便签内容预览（前 80 字，换行折叠为空格）。</summary>
    public string NotePreview { get; }

    /// <summary>便签颜色（驱动左侧颜色条）。</summary>
    public NoteColor NoteColor => _note.Color;

    /// <summary>默认稍后提醒间隔（分钟），来自设置 reminder.snooze_minutes。</summary>
    public int DefaultSnoozeMinutes { get; }

    /// <summary>请求关闭通知窗口（View 订阅后触发滑出动画）。</summary>
    public event EventHandler? CloseRequested;

    public ReminderNotificationViewModel(
        IReminderService reminderService,
        IWindowService windowService,
        ISettingsService settingsService,
        Reminder reminder,
        Note note)
    {
        _reminderService = reminderService;
        _windowService = windowService;
        _settingsService = settingsService;
        Reminder = reminder;
        _note = note;

        DefaultSnoozeMinutes = settingsService.GetInt("reminder.snooze_minutes", 10);
        NoteTitle = ResolveTitle(note);
        NotePreview = ResolvePreview(note);
    }

    /// <summary>稍后提醒：按指定分钟数重排，并关闭通知。</summary>
    [RelayCommand]
    private void Snooze(int minutes)
    {
        _reminderService.Snooze(Reminder.Id, minutes);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>完成提醒：标记完成并关闭通知；若便签窗口已打开则激活，否则打开。</summary>
    [RelayCommand]
    private void Complete()
    {
        _reminderService.CompleteReminder(Reminder.Id);
        CloseRequested?.Invoke(this, EventArgs.Empty);

        if (_windowService.IsWindowOpen(_note.Id))
        {
            _windowService.FocusNoteWindow(_note.Id);
        }
        else
        {
            _windowService.ShowNoteWindow(_note.Id);
        }
    }

    private static string ResolveTitle(Note note)
    {
        if (!string.IsNullOrWhiteSpace(note.Title))
        {
            return note.Title;
        }

        var firstLine = note.Content.Split('\n')[0].Trim();
        return firstLine.Length <= 20 ? firstLine : firstLine[..20];
    }

    private static string ResolvePreview(Note note)
    {
        var text = note.Content.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= 80 ? text : text[..80];
    }
}
