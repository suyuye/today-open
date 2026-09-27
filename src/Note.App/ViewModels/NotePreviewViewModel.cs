using System.Collections.ObjectModel;
using System.Windows;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using NoteApp.Helpers;
using NoteApp.Models;
using NoteApp.Models.Enums;
using NoteApp.Services;

namespace NoteApp.ViewModels;

/// <summary>
/// 右侧编辑区 ViewModel：可编辑标题/内容、500ms 防抖自动保存、操作命令与标签。
///
/// <para>
/// 行编辑、撤销栈、防抖保存、字体与配色解析全部继承自 <see cref="NoteEditorViewModelBase"/>，
/// 与便签窗口 <see cref="NoteWindowViewModel"/> 共用同一份实现 —— 本类只保留编辑区固有的差异
/// （脏标记、切换便签时的同步、标签增删）。
/// </para>
/// </summary>
public partial class NotePreviewViewModel : NoteEditorViewModelBase
{
    private readonly INoteService _noteService;
    private readonly IWindowService _windowService;
    private readonly ISettingsService _settingsService;

    /// <summary>同步中标记：NoteUpdated 回写时置位，避免触发自动保存造成循环。</summary>
    private bool _isSyncing;

    /// <summary>脏数据标记：标题或内容实际改变后置位，保存后复位；避免无变化时误更新 UpdatedAt。</summary>
    private bool _isDirty;

    /// <summary>正在保存中标志：防止自己触发的 NoteUpdated 事件重新赋值可编辑属性导致光标重置。</summary>
    private bool _isSaving;

    /// <summary>图片附件（缩略图条）。与便签窗口共用同一份实现，避免两处行为漂移。</summary>
    public AttachmentCollection Attachments { get; }

    public NotePreviewViewModel(Note note, INoteService noteService, IWindowService windowService, ISettingsService settingsService, IAttachmentService attachmentService)
        : base(note, settingsService)
    {
        _noteService = noteService;
        _windowService = windowService;
        _settingsService = settingsService;
        Attachments = new AttachmentCollection(attachmentService);
        Attachments.Reload(note.Id);

        _editableTitle = note.Title ?? string.Empty;
        _editableContent = note.Content;
        SetUndoBaseline(note.Content);

        // 从 Content 分割行（初始化待办事项渲染视图）
        RebuildLinesFromContent();

        ReloadTags();

        _noteService.NoteUpdated += OnNoteUpdated;
        _settingsService.SettingChanged += OnSettingChanged;
    }

    /// <summary>标签增删后触发（供列表刷新数量徽标与列表项标签）。</summary>
    public event EventHandler? TagsChanged;

    /// <summary>可编辑标题（空显示占位）。</summary>
    [ObservableProperty]
    private string _editableTitle;

    /// <summary>可编辑内容。</summary>
    [ObservableProperty]
    private string _editableContent;

    public string CreatedAtText => Note.CreatedAt.ToString("yyyy-MM-dd HH:mm");

    public string UpdatedAtText => Note.UpdatedAt.ToString("yyyy-MM-dd HH:mm");

    public bool IsPinned => Note.IsPinned;
    public bool IsLocked => Note.IsLocked;
    public bool IsArchived => Note.IsArchived;

    /// <summary>是否在回收站（软删除）。</summary>
    public bool IsInTrash => Note.DeletedAt is not null;

    /// <summary>是否普通便签（非回收站）。</summary>
    public bool IsNormal => Note.DeletedAt is null;

    /// <summary>当前便签的全部标签（可删除）。</summary>
    public ObservableCollection<Tag> Tags { get; } = new();

    // ===== 差异点 =====

    /// <summary>纯文本真源是 <see cref="EditableContent"/>（主页编辑区绑定的可编辑内容）。</summary>
    protected override string RawContent => EditableContent;

    protected override void SetRawContent(string value) => EditableContent = value;

    /// <summary>防抖计时到点：主页编辑区只在真有改动时落库（无变化时误更新 UpdatedAt 会让时间显示"刚刚"）。</summary>
    protected override void OnAutosaveElapsed() => SaveContent();

    /// <summary>行内容合并回纯文本后的补充：标记脏数据，确保提交时不会被「无改动」判断吞掉。</summary>
    protected override void OnLinesMergedToContent()
    {
        base.OnLinesMergedToContent();
        _isDirty = true;
    }

    // ===== 属性联动 =====

    partial void OnEditableTitleChanged(string value)
    {
        if (_isSyncing) return;
        // 值未改变时不更新、不启动自动保存（避免输入框加载时误触发保存导致显示"刚刚"）
        if (Note.Title == value) return;
        Note.Title = value;
        _isDirty = true;
        RestartAutosave();
    }

    partial void OnEditableContentChanged(string value)
    {
        if (_isSyncing) return;
        // 值未改变时不更新、不启动自动保存
        if (Note.Content == value) return;
        _isDirty = true;
        OnRawContentChanged(value);
    }

    // ===== 命令 =====

    [RelayCommand]
    private void OpenNote() => _windowService.ShowNoteWindow(Note.Id);

    [RelayCommand]
    private void TogglePin() => _noteService.TogglePin(Note.Id);

    [RelayCommand]
    private void ToggleLock() => _noteService.ToggleLock(Note.Id);

    [RelayCommand]
    private void ToggleArchive() => _noteService.ToggleArchive(Note.Id);

    [RelayCommand]
    private void SetColor(NoteColor color)
    {
        if (Note.Color == color)
        {
            return;
        }

        Note.Color = color;
        _noteService.SetColor(Note.Id, color);
        NotifyColorDependentProperties();
    }

    [RelayCommand]
    private void Delete()
    {
        var result = MessageBox.Show(
            "确定要删除这条便签吗？",
            "删除便签",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK)
        {
            return;
        }

        _noteService.DeleteNote(Note.Id);
    }

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

    [RelayCommand]
    private void RemoveTag(int tagId)
    {
        _noteService.RemoveTag(Note.Id, tagId);
        ReloadTags();
        TagsChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void AddTag(int tagId)
    {
        _noteService.AddTag(Note.Id, tagId);
        ReloadTags();
        TagsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ===== 生命周期 =====

    /// <summary>立即保存未落库的修改（切换便签/关闭时调用）。</summary>
    public void Flush()
    {
        StopAutosave();
        SaveContent();
    }

    /// <summary>释放资源：停止计时器、取消事件订阅。</summary>
    public void Cleanup()
    {
        StopAutosave();
        _noteService.NoteUpdated -= OnNoteUpdated;
        _settingsService.SettingChanged -= OnSettingChanged;
    }

    private void SaveContent()
    {
        // 只有内容实际改变时才保存，避免无变化时误更新 UpdatedAt 导致显示"刚刚"
        if (!_isDirty) return;

        _isSaving = true;
        try
        {
            Note.UpdatedAt = DateTime.Now;
            _noteService.UpdateNote(Note);
            _isDirty = false;
        }
        catch (Exception ex)
        {
            LogHelper.Error($"便签编辑保存失败：{Note.Id}", ex);
        }
        finally
        {
            _isSaving = false;
        }
    }

    private void OnNoteUpdated(object? sender, NoteEventArgs e)
    {
        if (e.Note.Id != Note.Id)
        {
            return;
        }

        _isSyncing = true;
        Note.Title = e.Note.Title;
        Note.Content = e.Note.Content;
        Note.IsPinned = e.Note.IsPinned;
        Note.IsLocked = e.Note.IsLocked;
        Note.IsArchived = e.Note.IsArchived;
        Note.Color = e.Note.Color;
        Note.Opacity = e.Note.Opacity;
        Note.UpdatedAt = e.Note.UpdatedAt;

        OnPropertyChanged(nameof(WordCount));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(UpdatedAtText));
        OnPropertyChanged(nameof(RelativeUpdateTime));
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(IsLocked));
        OnPropertyChanged(nameof(IsArchived));
        OnPropertyChanged(nameof(IsNormal));
        OnPropertyChanged(nameof(IsInTrash));
        NotifyColorDependentProperties();

        // 保存期间或用户正在编辑时（_isDirty=true）不更新可编辑属性，避免 UI 重新绑定导致光标重置
        // 外部内容更新（如独立便签窗口修改）且内容不同时，同步 EditableContent 和 Lines
        bool needRebuildLines = false;
        if (!_isSaving && !_isDirty)
        {
            EditableTitle = e.Note.Title ?? string.Empty;

            if (e.Note.Content != EditableContent)
            {
                EditableContent = e.Note.Content;
                // 外部变更并入撤销基线，不产生撤销步
                SetUndoBaseline(e.Note.Content);
                // _isSyncing 为 true 时 OnEditableContentChanged 不会重建行集合
                // 所以标记需要重建，在 _isSyncing = false 之后调用
                needRebuildLines = true;
            }
        }

        _isSyncing = false;

        // 在 _isSyncing = false 之后重建行集合，确保编辑区显示最新内容
        if (needRebuildLines)
        {
            RebuildLinesFromContent();
        }
    }

    /// <summary>设置变更：便签字体切换时即时刷新内容区字体（逻辑与便签窗口共用基类实现）。</summary>
    private void OnSettingChanged(object? sender, SettingChangedEventArgs e) =>
        HandleSettingChanged(sender, e, _settingsService);

    private void ReloadTags()
    {
        Tags.Clear();
        foreach (var tag in _noteService.GetTagsForNote(Note.Id))
        {
            Tags.Add(tag);
        }
    }
}
