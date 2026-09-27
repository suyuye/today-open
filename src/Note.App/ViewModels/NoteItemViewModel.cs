using System.Globalization;
using System.Windows;
using System.Windows.Media;

using CommunityToolkit.Mvvm.Input;

using NoteApp.Helpers;
using NoteApp.Models;
using NoteApp.Services;

namespace NoteApp.ViewModels;

/// <summary>便签列表项 ViewModel：映射便签显示字段，并提供置顶/锁定/归档/删除/恢复/永久删除命令。</summary>
public partial class NoteItemViewModel : ViewModelBase
{
    private readonly INoteService _noteService;
    private readonly NoteColorConverter _colorConverter = new();
    private readonly RelativeTimeConverter _relativeTimeConverter = new();

    private string _tagText = string.Empty;
    private string _reminderTimeText = string.Empty;

    /// <summary>
    /// 标签由调用方批量解析后传入，**不在构造里查库** ——
    /// 此前每条便签各查一次标签，列表一刷新就是 N 次查询。
    /// </summary>
    public NoteItemViewModel(Note note, INoteService noteService, IReadOnlyList<Tag> tags, DateTime? reminderAt = null)
    {
        Note = note;
        _noteService = noteService;
        ApplySnapshot(tags, reminderAt);
    }

    /// <summary>
    /// 原始便签模型。与列表快照中是**同一个实例**，
    /// 因此刷新时是就地改写它的字段（而非替换引用），
    /// 这样 SelectedNote / 编辑区持有的引用不会失配。
    /// </summary>
    public Note Note { get; }

    /// <summary>提醒时间（仅「待提醒」分类显示），无提醒时为空。</summary>
    public string ReminderTimeText => _reminderTimeText;

    /// <summary>是否有提醒时间（控制列表项元信息区提醒时间显隐）。</summary>
    public bool HasReminderTime => !string.IsNullOrEmpty(_reminderTimeText);

    /// <summary>标题：为空时取内容首行前 20 字。</summary>
    public string Title => !string.IsNullOrWhiteSpace(Note.Title) ? Note.Title : DeriveTitle(Note.Content);

    /// <summary>预览文字：内容去除换行后前 80 字。</summary>
    public string PreviewText => BuildPreview(Note.Content);

    /// <summary>相对修改时间（刚刚 / X分钟前 / HH:mm / MM-dd HH:mm）。</summary>
    public string RelativeUpdateTime =>
        (string)(_relativeTimeConverter.Convert(Note.UpdatedAt, typeof(string), null!, CultureInfo.CurrentCulture) ?? string.Empty);

    /// <summary>标签文本：无标签为空；有标签为「 · 第一个标签」，多个再追加「 +N」。</summary>
    public string TagText => _tagText;

    /// <summary>颜色条画刷（便签本色）。</summary>
    public Brush ColorBrush =>
        (Brush)(_colorConverter.Convert(Note.Color, typeof(Brush), "Bg", CultureInfo.CurrentCulture) ?? Brushes.Transparent);

    public bool IsPinned => Note.IsPinned;
    public bool IsLocked => Note.IsLocked;
    public bool IsArchived => Note.IsArchived;

    /// <summary>是否在回收站（软删除）。</summary>
    public bool IsInTrash => Note.DeletedAt is not null;

    /// <summary>是否普通便签（非回收站），用于切换操作按钮。</summary>
    public bool IsNormal => Note.DeletedAt is null;

    /// <summary>
    /// 刷新「不随便签字段变化」的显示内容：标签文本与提醒时间。
    /// 列表刷新时对已存在的项调用，避免重建列表项（重建会打断选中、滚动与编辑区）。
    /// </summary>
    public void ApplySnapshot(IReadOnlyList<Tag> tags, DateTime? reminderAt)
    {
        var tagText = BuildTagText(tags);
        var reminderText = reminderAt is DateTime t ? t.ToString("yyyy-MM-dd HH:mm") : string.Empty;

        if (tagText != _tagText)
        {
            _tagText = tagText;
            OnPropertyChanged(nameof(TagText));
        }

        if (reminderText != _reminderTimeText)
        {
            _reminderTimeText = reminderText;
            OnPropertyChanged(nameof(ReminderTimeText));
            OnPropertyChanged(nameof(HasReminderTime));
        }
    }

    /// <summary>底层 <see cref="Note"/> 的字段被就地改写后，通知全部派生显示属性刷新。</summary>
    public void NotifyNoteFieldsChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(PreviewText));
        OnPropertyChanged(nameof(RelativeUpdateTime));
        OnPropertyChanged(nameof(ColorBrush));
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(IsLocked));
        OnPropertyChanged(nameof(IsArchived));
        OnPropertyChanged(nameof(IsInTrash));
        OnPropertyChanged(nameof(IsNormal));
    }

    [RelayCommand]
    private void TogglePin() => _noteService.TogglePin(Note.Id);

    [RelayCommand]
    private void ToggleLock() => _noteService.ToggleLock(Note.Id);

    [RelayCommand]
    private void ToggleArchive() => _noteService.ToggleArchive(Note.Id);

    [RelayCommand]
    private void Delete() => _noteService.DeleteNote(Note.Id);

    [RelayCommand]
    private void Restore() => _noteService.RestoreNote(Note.Id);

    [RelayCommand]
    private void PermanentDelete()
    {
        var result = MessageBox.Show(
            "确定要永久删除这条便签吗？此操作不可恢复。",
            "永久删除",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK)
        {
            return;
        }

        _noteService.DeleteNote(Note.Id, permanent: true);
    }

    private static string BuildTagText(IReadOnlyList<Tag> tags)
    {
        if (tags.Count == 0)
        {
            return string.Empty;
        }

        var name = tags[0].Name;
        return tags.Count == 1 ? $" · {name}" : $" · {name} +{tags.Count - 1}";
    }

    private static string DeriveTitle(string content)
    {
        var firstLine = content.Split('\n')[0].Trim();
        return firstLine.Length <= 20 ? firstLine : firstLine[..20];
    }

    private static string BuildPreview(string content)
    {
        var single = content.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length <= 80 ? single : single[..80];
    }
}
