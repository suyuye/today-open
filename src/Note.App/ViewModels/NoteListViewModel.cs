using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using NoteApp.Data.Repositories;
using NoteApp.Helpers;
using NoteApp.Models;
using NoteApp.Models.Enums;
using NoteApp.Services;
using NoteApp.Views;

namespace NoteApp.ViewModels;

/// <summary>便签列表面板 ViewModel：侧边栏分类、数量徽标、搜索过滤、便签列表、分类切换、批量操作。</summary>
public partial class NoteListViewModel : ViewModelBase
{
    private const int SearchDebounceMilliseconds = 200;

    private readonly INoteService _noteService;
    private readonly ITagRepository _tagRepository;
    private readonly IReminderService _reminderService;
    private readonly IWindowService _windowService;
    private readonly INoteEditorService _noteEditorService;
    private readonly ISettingsService _settingsService;
    private readonly IAttachmentService _attachmentService;
    private readonly DispatcherTimer _searchTimer;

    /// <summary>批量操作进行中标记：期间抑制单个便签事件触发的刷新，结束后统一刷新一次。</summary>
    private bool _isBatchOperating;

    /// <summary>新建便签进行中标记：期间抑制事件触发的刷新，结束后统一刷新一次，避免多次刷新导致 UI 异常。</summary>
    private bool _isNoteCreating;

    /// <summary>列表刷新进行中标记：防止递归调用 ReloadNotes 导致重复添加。</summary>
    private bool _isReloading;

    // ===== 列表数据快照 =====
    // 分类切换、搜索、数量徽标、列表项标签文字全部基于下面这份内存快照计算，
    // 不再每次刷新都读库（此前一次刷新要读 3~4 次全表 + 每条便签各一次标签查询）。
    // 脏标记只由「结构性变更」置位；普通的内容自动保存只把字段并回快照，不读库。

    /// <summary>全部便签（含已删除、已归档）。列表一切派生数据都从它算。</summary>
    private List<Note> _notesSnapshot = new();

    private bool _notesDirty = true;

    /// <summary>标签目录（侧边栏分类与列表项标签文字都用它）。</summary>
    private List<Tag> _tags = new();

    /// <summary>便签 → 其标签列表，由一次 NoteTags 全表读出后在内存里拼成。</summary>
    private Dictionary<Guid, List<Tag>> _tagsByNote = new();

    private bool _tagsDirty = true;

    /// <summary>待提醒便签集合与各自最近提醒时间（提醒事件到达时置脏）。</summary>
    private HashSet<Guid> _pendingNoteIds = new();

    private Dictionary<Guid, DateTime> _pendingReminderTimes = new();

    private bool _pendingDirty = true;

    // ===== 组合筛选（颜色 / 标签 / 时间范围）=====
    // 与搜索词分开：搜索词是「找某个词」，筛选是「缩小范围」，两者可叠加。

    /// <summary>颜色筛选项（含「全部颜色」，其 Value 为 null）。</summary>
    public ObservableCollection<FilterOption<NoteColor?>> ColorFilterOptions { get; } = new();

    /// <summary>标签筛选项（含「全部标签」，其 Value 为 null）。</summary>
    public ObservableCollection<FilterOption<int?>> TagFilterOptions { get; } = new();

    /// <summary>时间范围筛选项。</summary>
    public ObservableCollection<FilterOption<DateRangeFilter>> RangeFilterOptions { get; } = new();

    [ObservableProperty]
    private FilterOption<NoteColor?>? _colorFilter;

    [ObservableProperty]
    private FilterOption<int?>? _tagFilter;

    [ObservableProperty]
    private FilterOption<DateRangeFilter>? _rangeFilter;

    /// <summary>是否有任何筛选生效（控制「清除筛选」按钮显隐）。</summary>
    public bool HasActiveFilter =>
        ColorFilter?.Value is not null
        || TagFilter?.Value is not null
        || (RangeFilter?.Value ?? DateRangeFilter.All) != DateRangeFilter.All;

    /// <summary>侧边栏分类列表（固定分类 + 「标签」分组标题 + 动态标签）。</summary>
    public ObservableCollection<CategoryItem> Categories { get; } = new();

    /// <summary>当前分类下（经搜索过滤后）的便签列表项。</summary>
    public ObservableCollection<NoteItemViewModel> Notes { get; } = new();

    [ObservableProperty]
    private CategoryItem? _currentCategory;

    [ObservableProperty]
    private NoteItemViewModel? _selectedNote;

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>批量选中的便签（由 View 的 SelectionChanged 同步而来）。</summary>
    [ObservableProperty]
    private List<NoteItemViewModel> _selectedNotes = new();

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private bool _isBatchMode;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>当前排序方式（列表面板打开期间保持，MVP 不持久化到设置）。</summary>
    [ObservableProperty]
    private SortBy _currentSortBy = SortBy.ModifiedTimeDesc;

    /// <summary>标题栏中间显示：分类名或搜索关键词 + 数量。</summary>
    [ObservableProperty]
    private string _currentCategoryTitle = string.Empty;

    private NotePreviewViewModel? _currentNote;

    /// <summary>右侧预览面板当前展示的便签（null 表示未选中）。</summary>
    public NotePreviewViewModel? CurrentNote
    {
        get => _currentNote;
        private set
        {
            if (ReferenceEquals(_currentNote, value))
            {
                return;
            }

            // 切换前保存未落库内容，并释放旧编辑区订阅
            if (_currentNote is not null)
            {
                _currentNote.Flush();
                _currentNote.TagsChanged -= OnPreviewTagsChanged;
                _currentNote.Cleanup();
            }

            _currentNote = value;

            if (_currentNote is not null)
            {
                _currentNote.TagsChanged += OnPreviewTagsChanged;
            }

            OnPropertyChanged();
        }
    }

    /// <summary>请求 View 全选列表项（SelectedItems 不可绑定，经事件桥接到 View）。</summary>
    public event EventHandler? SelectAllRequested;

    /// <summary>请求 View 清除列表选中项（批量操作完成后调用）。</summary>
    public event EventHandler? ClearSelectionRequested;

    public NoteListViewModel(
        INoteService noteService,
        ITagRepository tagRepository,
        IReminderService reminderService,
        IWindowService windowService,
        INoteEditorService noteEditorService,
        ISettingsService settingsService,
        IAttachmentService attachmentService)
    {
        _noteService = noteService;
        _tagRepository = tagRepository;
        _reminderService = reminderService;
        _windowService = windowService;
        _noteEditorService = noteEditorService;
        _settingsService = settingsService;
        _attachmentService = attachmentService;

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SearchDebounceMilliseconds) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            ReloadNotes();
        };

        BuildCategories();
        BuildFilterOptions();
        RefreshCategoryCounts();

        // 单条更新与结构性变更分开处理：
        // 前者（绝大多数是编辑器自动保存）只做内存就地更新，不读库、不重建列表。
        _noteService.NoteCreated += OnNoteCreated;
        _noteService.NoteDeleted += OnNoteDeleted;
        _noteService.NoteRestored += OnNoteRestored;
        _noteService.NoteUpdated += OnNoteUpdated;
        _noteService.NotesBulkChanged += OnNotesBulkChanged;
        _noteService.TagAssignmentsChanged += OnTagAssignmentsChanged;

        _reminderService.ReminderCreated += OnReminderChanged;
        _reminderService.ReminderRemoved += OnReminderChanged;
        _reminderService.ReminderTriggered += OnReminderChanged;

        CurrentCategory = Categories[0];
    }

    /// <summary>是否有搜索关键词（控制清除按钮显隐）。</summary>
    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>列表为空时显示占位。</summary>
    public Visibility EmptyStateVisibility => Notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearchText));
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    partial void OnCurrentCategoryChanged(CategoryItem? value)
    {
        if (value is null || value.IsGroupHeader)
        {
            return;
        }

        ReloadNotes();
    }

    partial void OnSelectedNoteChanged(NoteItemViewModel? value)
    {
        // 列表刷新（ReloadNotes）期间不处理选中变化，保留当前编辑区（避免打断输入、丢失焦点和未保存内容）
        if (_isReloading)
        {
            return;
        }

        if (value is null)
        {
            CurrentNote = null;
            return;
        }

        // 列表刷新（ReloadNotes）时若选中同一便签，保留编辑区（避免打断输入、丢失未保存内容）
        if (CurrentNote?.Note.Id == value.Note.Id)
        {
            return;
        }

        CurrentNote = new NotePreviewViewModel(value.Note, _noteService, _windowService, _settingsService, _attachmentService);
    }

    partial void OnSelectedNotesChanged(List<NoteItemViewModel> value)
    {
        SelectedCount = value.Count;
        IsBatchMode = value.Count >= 2;
    }

    partial void OnCurrentSortByChanged(SortBy value) => OnPropertyChanged(nameof(CurrentSortDisplayName));

    partial void OnColorFilterChanged(FilterOption<NoteColor?>? value) => ApplyFilterChange();

    partial void OnTagFilterChanged(FilterOption<int?>? value) => ApplyFilterChange();

    partial void OnRangeFilterChanged(FilterOption<DateRangeFilter>? value) => ApplyFilterChange();

    /// <summary>筛选变化：重建数量徽标与列表（纯内存，不读库）。</summary>
    private void ApplyFilterChange()
    {
        OnPropertyChanged(nameof(HasActiveFilter));
        RefreshCategoryCounts();
        ReloadNotes();
    }

    /// <summary>清空全部筛选条件（回到「全部颜色 / 全部标签 / 全部时间」）。</summary>
    [RelayCommand]
    private void ClearFilters()
    {
        ColorFilter = ColorFilterOptions.FirstOrDefault();
        TagFilter = TagFilterOptions.FirstOrDefault();
        RangeFilter = RangeFilterOptions.FirstOrDefault();
    }

    /// <summary>构建筛选项。颜色与时间范围是静态的，标签随侧边栏一起变。</summary>
    private void BuildFilterOptions()
    {
        ColorFilterOptions.Clear();
        ColorFilterOptions.Add(new FilterOption<NoteColor?>("全部颜色", null));
        foreach (var color in NoteColorHelper.All)
        {
            ColorFilterOptions.Add(new FilterOption<NoteColor?>(NoteColorHelper.GetDisplayName(color), color));
        }

        RangeFilterOptions.Clear();
        RangeFilterOptions.Add(new FilterOption<DateRangeFilter>("全部时间", DateRangeFilter.All));
        RangeFilterOptions.Add(new FilterOption<DateRangeFilter>("今天", DateRangeFilter.Today));
        RangeFilterOptions.Add(new FilterOption<DateRangeFilter>("近 7 天", DateRangeFilter.Last7Days));
        RangeFilterOptions.Add(new FilterOption<DateRangeFilter>("近 30 天", DateRangeFilter.Last30Days));

        ColorFilter = ColorFilterOptions[0];
        RangeFilter = RangeFilterOptions[0];
        RebuildTagFilterOptions();
    }

    /// <summary>重建标签筛选项（标签增删后调用），尽量保留当前选择。</summary>
    private void RebuildTagFilterOptions()
    {
        var keep = TagFilter?.Value;

        TagFilterOptions.Clear();
        TagFilterOptions.Add(new FilterOption<int?>("全部标签", null));

        EnsureTagCache();
        foreach (var tag in _tags)
        {
            TagFilterOptions.Add(new FilterOption<int?>(tag.Name, tag.Id));
        }

        TagFilter = TagFilterOptions.FirstOrDefault(o => o.Value == keep) ?? TagFilterOptions[0];
    }

    /// <summary>排序下拉按钮显示的当前排序方式名。</summary>
    public string CurrentSortDisplayName => CurrentSortBy switch
    {
        SortBy.CreatedTimeDesc => "按创建时间",
        SortBy.TitleAsc => "按标题",
        SortBy.ByColor => "按颜色",
        _ => "按修改时间",
    };

    // ===== 命令 =====

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    /// <summary>新建便签并在列表面板右侧编辑区打开（不弹独立窗口）。</summary>
    /// 若当前选中的是某个标签分类，则新便签自动带上该标签，并刷新列表后选中高亮。
    [RelayCommand]
    private void NewNote()
    {
        // 新建便签期间抑制事件触发的刷新，避免多次刷新导致 UI 异常（如重复显示）
        _isNoteCreating = true;
        Note note;
        try
        {
            note = _noteService.CreateNote(color: _settingsService.DefaultColor);

            // 位置偏移按现有便签数递推；先置脏再取快照，保证把刚建的这条也数进去
            InvalidateNotesSnapshot();
            var count = NotesSnapshot().Count(n => n.DeletedAt is null && !n.IsArchived);
            note.PositionX = 100 + count * 30;
            note.PositionY = 100 + count * 30;
            note.Width = 240;
            note.Height = 200;
            note.Opacity = _settingsService.DefaultOpacity;
            _noteService.UpdateNote(note);

            // 若当前选中的是标签分类，自动给新便签加上该标签
            if (CurrentCategory is { CategoryType: CategoryType.Tag, TagId: { } tagId })
            {
                _noteService.AddTag(note.Id, tagId);
                // 期间事件被 _isNoteCreating 抑制，这里补一次标签缓存失效
                InvalidateTagCache();
            }
        }
        finally
        {
            _isNoteCreating = false;
        }

        // 新建完成后统一刷新一次列表和分类计数
        RefreshCategoryCounts();
        ReloadNotes();

        // 选中新建的便签，右侧编辑区自动加载，列表项高亮显示
        SelectedNote = Notes.FirstOrDefault(x => x.Note.Id == note.Id);

        // 请求 View 层滚动到新便签位置
        if (SelectedNote is not null)
        {
            ScrollToNoteRequested?.Invoke(this, SelectedNote);
        }
    }

    /// <summary>请求 View 层滚动到指定便签（新建便签后触发）。</summary>
    public event EventHandler<NoteItemViewModel>? ScrollToNoteRequested;

    /// <summary>打开便签窗口（双击列表项）。</summary>
    [RelayCommand]
    private void OpenNote(Guid noteId) => _windowService.ShowNoteWindow(noteId);

    /// <summary>手动刷新列表与数量徽标（强制重新读取数据库，绕过内存快照）。</summary>
    [RelayCommand]
    private void Refresh()
    {
        InvalidateNotesSnapshot();
        InvalidateTagCache();
        InvalidatePendingReminders();
        RefreshCategoryCounts();
        ReloadNotes();
    }

    /// <summary>全选当前列表项（经事件桥接到 View 的 ListBox.SelectAll）。</summary>
    [RelayCommand]
    private void SelectAll() => SelectAllRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>切换列表排序方式并立即重排。</summary>
    [RelayCommand]
    private void ChangeSort(SortBy sortBy)
    {
        if (CurrentSortBy == sortBy)
        {
            return;
        }

        CurrentSortBy = sortBy;
        ReloadNotes();
    }

    [RelayCommand]
    private void BatchTogglePin()
    {
        var ids = GetSelectedIds();
        if (ids.Count == 0)
        {
            return;
        }

        var succeeded = RunBatch(ids, id => _noteService.TogglePin(id), out var failed);
        FinishBatch(succeeded.Count, failed, "批量置顶/取消置顶");
    }

    [RelayCommand]
    private void BatchSetColor(string? colorName)
    {
        if (!Enum.TryParse<NoteColor>(colorName, ignoreCase: true, out var color))
        {
            return;
        }

        var ids = GetSelectedIds();
        if (ids.Count == 0)
        {
            return;
        }

        var succeeded = RunBatch(ids, id => _noteService.SetColor(id, color), out var failed);
        FinishBatch(succeeded.Count, failed, "批量改色");
    }

    [RelayCommand]
    private void BatchArchive()
    {
        var ids = GetSelectedIds();
        if (ids.Count == 0)
        {
            return;
        }

        var succeeded = RunBatch(ids, id => _noteService.ToggleArchive(id), out var failed);
        FinishBatch(succeeded.Count, failed, "批量归档/取消归档");
    }

    [RelayCommand]
    private void BatchDelete()
    {
        var ids = GetSelectedIds();
        if (ids.Count == 0)
        {
            return;
        }

        var result = MessageBox.Show(
            $"确定要删除选中的 {ids.Count} 条便签吗？",
            "批量删除",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK)
        {
            return;
        }

        var succeeded = RunBatch(ids, id =>
        {
            _noteService.DeleteNote(id, permanent: false);
            _windowService.CloseNoteWindow(id);
        }, out var failed);

        FinishBatch(succeeded.Count, failed, "批量删除");

        if (succeeded.Count > 0)
        {
            ToastWindow.Show($"已删除 {succeeded.Count} 项", "撤销", () =>
            {
                foreach (var id in succeeded)
                {
                    try
                    {
                        _noteService.RestoreNote(id);
                    }
                    catch (Exception ex)
                    {
                        LogHelper.Error($"撤销恢复便签失败：{id}", ex);
                    }
                }
            });
        }
    }

    [RelayCommand]
    private void BatchAddTag(int tagId)
    {
        var ids = GetSelectedIds();
        if (ids.Count == 0)
        {
            return;
        }

        var succeeded = RunBatch(ids, id => _noteService.AddTag(id, tagId), out var failed);
        FinishBatch(succeeded.Count, failed, "批量添加标签");
    }

    /// <summary>取消事件订阅与计时器，避免单例 NoteService 持有本 VM 导致泄漏。</summary>
    public void Cleanup()
    {
        _searchTimer.Stop();
        _currentNote?.Flush();
        _currentNote?.Cleanup();
        _noteService.NoteCreated -= OnNoteCreated;
        _noteService.NoteDeleted -= OnNoteDeleted;
        _noteService.NoteRestored -= OnNoteRestored;
        _noteService.NoteUpdated -= OnNoteUpdated;
        _noteService.NotesBulkChanged -= OnNotesBulkChanged;
        _noteService.TagAssignmentsChanged -= OnTagAssignmentsChanged;
        _reminderService.ReminderCreated -= OnReminderChanged;
        _reminderService.ReminderRemoved -= OnReminderChanged;
        _reminderService.ReminderTriggered -= OnReminderChanged;
    }

    // ===== 数据快照的加载与失效 =====

    /// <summary>把便签快照标脏：下次取用时重新读一次全表。</summary>
    private void InvalidateNotesSnapshot() => _notesDirty = true;

    /// <summary>把标签缓存标脏（标签增删、便签标签关联变化时调用）。</summary>
    private void InvalidateTagCache() => _tagsDirty = true;

    /// <summary>把「待提醒」缓存标脏（提醒增删或触发时调用）。</summary>
    private void InvalidatePendingReminders() => _pendingDirty = true;

    /// <summary>取便签快照（脏则重新读一次，否则直接复用）。</summary>
    private List<Note> NotesSnapshot()
    {
        if (_notesDirty)
        {
            _notesSnapshot = _noteService.GetAllNotesSnapshot();
            _notesDirty = false;
        }

        return _notesSnapshot;
    }

    /// <summary>
    /// 取标签目录与「便签 → 标签」映射。
    /// 一次 Tags 全表 + 一次 NoteTags 全表，在内存里拼成字典，
    /// 避免此前「每条便签查一次标签」的 N 次查询。
    /// </summary>
    private void EnsureTagCache()
    {
        if (!_tagsDirty)
        {
            return;
        }

        _tags = _tagRepository.GetAll();

        var tagsById = new Dictionary<int, Tag>();
        foreach (var tag in _tags)
        {
            tagsById[tag.Id] = tag;
        }

        var map = new Dictionary<Guid, List<Tag>>();
        foreach (var link in _tagRepository.GetAllNoteTagLinks())
        {
            if (!tagsById.TryGetValue(link.TagId, out var tag))
            {
                continue;
            }

            if (!map.TryGetValue(link.NoteId, out var list))
            {
                map[link.NoteId] = list = new List<Tag>();
            }

            list.Add(tag);
        }

        // 与原先 GetTagsForNote 的 ORDER BY t.CreatedAt ASC 保持一致
        foreach (var list in map.Values)
        {
            list.Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
        }

        _tagsByNote = map;
        _tagsDirty = false;
    }

    /// <summary>某个便签的标签列表（内存查询）。</summary>
    private IReadOnlyList<Tag> TagsFor(Guid noteId)
    {
        EnsureTagCache();
        return _tagsByNote.TryGetValue(noteId, out var list) ? list : Array.Empty<Tag>();
    }

    /// <summary>待提醒便签 Id 集合（内存查询）。</summary>
    private HashSet<Guid> PendingNoteIds()
    {
        EnsurePendingReminders();
        return _pendingNoteIds;
    }

    /// <summary>待提醒便签 → 最近一次提醒时间（内存查询）。</summary>
    private Dictionary<Guid, DateTime> GetPendingReminderTimes()
    {
        EnsurePendingReminders();
        return _pendingReminderTimes;
    }

    /// <summary>待提醒数据只在提醒事件到达时失效，故平时一次查询都不会发生。</summary>
    private void EnsurePendingReminders()
    {
        if (!_pendingDirty)
        {
            return;
        }

        var reminders = _reminderService.GetPendingReminders();
        _pendingNoteIds = reminders.Select(r => r.NoteId).ToHashSet();
        _pendingReminderTimes = reminders
            .GroupBy(r => r.NoteId)
            .ToDictionary(g => g.Key, g => g.Min(r => r.RemindAt));
        _pendingDirty = false;
    }

    private void BuildCategories()
    {
        Categories.Add(new CategoryItem("全部", "IconAll", CategoryType.All));
        Categories.Add(new CategoryItem("置顶", "IconPin", CategoryType.Pinned));
        Categories.Add(new CategoryItem("活跃", "IconActive", CategoryType.Active));
        Categories.Add(new CategoryItem("待提醒", "IconReminder", CategoryType.PendingReminder));
        Categories.Add(new CategoryItem("标签", string.Empty, CategoryType.TagGroup));

        EnsureTagCache();
        foreach (var tag in _tags)
        {
            Categories.Add(new CategoryItem(tag.Name, "IconTag", CategoryType.Tag, tag.Id));
        }

        Categories.Add(new CategoryItem("归档", "IconArchive", CategoryType.Archived));
        Categories.Add(new CategoryItem("回收站", "IconTrash", CategoryType.Trash));
    }

    /// <summary>创建标签并刷新侧边栏（由 View 在确认「新建标签」对话框后调用），返回新标签的 ID。</summary>
    public int CreateTag(string name, NoteColor color)
    {
        var tag = _noteService.CreateTag(name, color);
        ReloadTagCategories();
        return tag.Id;
    }

    /// <summary>
    /// 删除标签（NoteTags 关联经外键级联清除），刷新侧边栏与列表；
    /// 当前分类是被删标签时切回「全部」。
    /// </summary>
    public void DeleteTag(int tagId)
    {
        _noteService.DeleteTag(tagId);

        ReloadTagCategories();
        RefreshCategoryCounts();

        // ReloadTagCategories 对已删除标签解析不到选中项，CurrentCategory 可能为 null，切回「全部」
        if (CurrentCategory is null || CurrentCategory.CategoryType == CategoryType.Tag)
        {
            CurrentCategory = Categories[0];
        }
    }

    /// <summary>重建侧边栏「标签」分组下的动态标签分类，并保持当前选中分类不变。</summary>
    private void ReloadTagCategories()
    {
        var currentTagId = CurrentCategory?.CategoryType == CategoryType.Tag ? CurrentCategory.TagId : null;

        // 移除旧的标签分类项（保留分组标题、归档、回收站等固定项）
        var tagItems = Categories.Where(c => c.CategoryType == CategoryType.Tag).ToList();
        foreach (var item in tagItems)
        {
            Categories.Remove(item);
        }

        // 在「标签」分组标题之后插入新的标签分类
        var groupIndex = -1;
        for (var i = 0; i < Categories.Count; i++)
        {
            if (Categories[i].CategoryType == CategoryType.TagGroup)
            {
                groupIndex = i;
                break;
            }
        }

        var insertIndex = groupIndex + 1;
        InvalidateTagCache();
        EnsureTagCache();
        foreach (var tag in _tags)
        {
            Categories.Insert(insertIndex++, new CategoryItem(tag.Name, "IconTag", CategoryType.Tag, tag.Id));
        }

        // 重建后 CategoryItem 引用已变化，重新解析当前标签分类以保持选中态
        if (currentTagId is int id)
        {
            CurrentCategory = Categories.FirstOrDefault(c => c.CategoryType == CategoryType.Tag && c.TagId == id);
        }

        // 标签本身增删了，筛选下拉也要跟着更新
        RebuildTagFilterOptions();
        RefreshCategoryCounts();
    }

    /// <summary>
    /// 重新计算所有分类的数量徽标。纯内存计算（基于便签快照），不产生数据库查询。
    /// </summary>
    private void RefreshCategoryCounts()
    {
        var snapshot = NotesSnapshot();

        foreach (var item in Categories)
        {
            // 分组标题不显示数量，保持原值不动
            if (item.IsGroupHeader)
            {
                continue;
            }

            item.Count = snapshot.Count(n => MatchesCategory(n, item) && MatchesFilters(n));
        }
    }

    /// <summary>
    /// 分类之外的筛选条件：颜色 / 标签 / 时间范围。
    /// 未选择的条件一律视为「不筛选」，因此默认状态下恒为 true（不影响原有行为）。
    /// </summary>
    private bool MatchesFilters(Note note)
    {
        if (ColorFilter?.Value is { } color && note.Color != color)
        {
            return false;
        }

        if (TagFilter?.Value is { } tagId && !TagsFor(note.Id).Any(t => t.Id == tagId))
        {
            return false;
        }

        var range = RangeFilter?.Value ?? DateRangeFilter.All;
        if (range == DateRangeFilter.All)
        {
            return true;
        }

        // 按「最近修改时间」判断：对便签来说「近 7 天」通常指最近动过的
        var since = range switch
        {
            DateRangeFilter.Today => DateTime.Today,
            DateRangeFilter.Last7Days => DateTime.Now.AddDays(-7),
            DateRangeFilter.Last30Days => DateTime.Now.AddDays(-30),
            _ => DateTime.MinValue,
        };

        return note.UpdatedAt >= since;
    }

    /// <summary>
    /// 便签是否命中搜索关键词。
    /// 复用 <see cref="HighlightSegmenter.Contains"/>，保证「搜到的」与「高亮的」
    /// 用的是同一套比较规则（否则会出现搜到了却没高亮这种自相矛盾的表现）。
    /// </summary>
    private bool MatchesSearch(Note note)
    {
        var keyword = SearchText?.Trim();
        if (string.IsNullOrEmpty(keyword))
        {
            return true;
        }

        return HighlightSegmenter.Contains(note.Title, keyword)
               || HighlightSegmenter.Contains(note.Content, keyword);
    }

    /// <summary>便签是否属于指定分类（只看分类，不看搜索词）。</summary>
    private bool MatchesCategory(Note note, CategoryItem category) => category.CategoryType switch
    {
        CategoryType.All => note.DeletedAt is null && !note.IsArchived,
        CategoryType.Pinned => note.DeletedAt is null && !note.IsArchived && note.IsPinned,
        CategoryType.Active => note.DeletedAt is null && !note.IsArchived && !note.IsLocked,
        CategoryType.PendingReminder => note.DeletedAt is null && !note.IsArchived && PendingNoteIds().Contains(note.Id),
        // 标签分类与原 GetTagNotes 一致：包含已归档便签
        CategoryType.Tag => note.DeletedAt is null
                            && category.TagId is { } tagId
                            && TagsFor(note.Id).Any(t => t.Id == tagId),
        CategoryType.Archived => note.DeletedAt is null && note.IsArchived,
        CategoryType.Trash => note.DeletedAt is not null,
        _ => false,
    };

    /// <summary>便签是否应出现在当前视图（分类 + 筛选 + 搜索词）。</summary>
    private bool MatchesCurrentView(Note note)
    {
        var category = CurrentCategory;
        if (category is null || category.IsGroupHeader || !MatchesCategory(note, category))
        {
            return false;
        }

        return MatchesFilters(note) && MatchesSearch(note);
    }

    /// <summary>当前视图应显示的便签（按当前排序）。纯内存。</summary>
    private List<Note> SelectForCurrentView() =>
        SortNotes(NotesSnapshot().Where(MatchesCurrentView).ToList(), CurrentSortBy);

    /// <summary>
    /// 让列表与当前视图保持一致。
    ///
    /// 做法是「就地增删改 + 移动」，**不清空重建**：
    /// 复用既有的列表项实例可保留选中态、滚动位置与右侧编辑区，
    /// 避免内容自动保存时列表整体闪动重排。
    /// 全程只读内存快照，不产生数据库查询。
    /// </summary>
    private void ReloadNotes()
    {
        // 防止递归调用（如 SelectedNote 变化触发的事件再次调用本方法）导致重复添加
        if (_isReloading)
        {
            return;
        }

        _isReloading = true;
        IsLoading = true;
        try
        {
            var keyword = SearchText?.Trim();
            var desired = SelectForCurrentView();
            var selectedId = SelectedNote?.Note.Id;
            var reminderTimes = CurrentCategory?.CategoryType == CategoryType.PendingReminder
                ? GetPendingReminderTimes()
                : null;

            // 1) 移除已不属于当前视图的项
            var desiredIds = new HashSet<Guid>(desired.Select(n => n.Id));
            for (var i = Notes.Count - 1; i >= 0; i--)
            {
                if (!desiredIds.Contains(Notes[i].Note.Id))
                {
                    Notes.RemoveAt(i);
                }
            }

            var existing = new Dictionary<Guid, NoteItemViewModel>();
            foreach (var item in Notes)
            {
                existing[item.Note.Id] = item;
            }

            // 2) 按目标顺序就地刷新 / 插入 / 移动
            for (var i = 0; i < desired.Count; i++)
            {
                var note = desired[i];
                DateTime? reminderAt = reminderTimes is not null && reminderTimes.TryGetValue(note.Id, out var t) ? t : null;
                var tags = TagsFor(note.Id);

                if (existing.TryGetValue(note.Id, out var item))
                {
                    // 列表项与快照共享同一个 Note 实例，字段无需复制，只需刷新标签/提醒显示
                    item.ApplySnapshot(tags, reminderAt);

                    var currentIndex = Notes.IndexOf(item);
                    if (currentIndex != i)
                    {
                        Notes.Move(currentIndex, i);
                    }
                }
                else
                {
                    Notes.Insert(i, new NoteItemViewModel(note, _noteService, tags, reminderAt));
                }
            }

            // 与刷新前保持一致：仍存在则维持选中（复用同一实例，不会触发选中变化事件）
            SelectedNote = selectedId is Guid id ? Notes.FirstOrDefault(x => x.Note.Id == id) : null;

            OnPropertyChanged(nameof(EmptyStateVisibility));
            UpdateTitleBar(keyword);
        }
        finally
        {
            IsLoading = false;
            _isReloading = false;
        }
    }

    /// <summary>按当前排序方式对便签列表排序。</summary>
    private static List<Note> SortNotes(List<Note> notes, SortBy sortBy) => sortBy switch
    {
        SortBy.CreatedTimeDesc => notes.OrderByDescending(n => n.CreatedAt).ToList(),
        SortBy.TitleAsc => notes
            .OrderBy(n => string.IsNullOrWhiteSpace(n.Title))
            .ThenBy(n => n.Title ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList(),
        SortBy.ByColor => notes
            .OrderBy(n => n.Color)
            .ThenByDescending(n => n.UpdatedAt)
            .ToList(),
        _ => notes.OrderByDescending(n => n.UpdatedAt).ToList(),
    };

    /// <summary>
    /// 把更新后的便签字段并回快照中的**同一实例**（保持对象标识稳定，
    /// 这样列表项、选中项与右侧编辑区持有的引用都不会失配）。
    /// 快照里找不到该便签时返回 false，调用方据此退回全量刷新。
    /// </summary>
    private bool TryPatchSnapshot(Note updated)
    {
        var target = NotesSnapshot().FirstOrDefault(n => n.Id == updated.Id);
        if (target is null)
        {
            return false;
        }

        if (!ReferenceEquals(target, updated))
        {
            CopyNoteFields(updated, target);
        }

        return true;
    }

    /// <summary>复制便签的全部可变字段（不含 Id，保留目标的对象标识）。</summary>
    private static void CopyNoteFields(Note source, Note target)
    {
        target.Title = source.Title;
        target.Content = source.Content;
        target.Color = source.Color;
        target.PositionX = source.PositionX;
        target.PositionY = source.PositionY;
        target.Width = source.Width;
        target.Height = source.Height;
        target.Opacity = source.Opacity;
        target.IsPinned = source.IsPinned;
        target.IsLocked = source.IsLocked;
        target.IsArchived = source.IsArchived;
        // 形态经 DisplayMode 复制，而不是分别拷两个 bool：
        // 若源数据非法（两列同时为 true），DisplayMode 会把它归并成唯一合法的形态，
        // 不会把坏状态传染给目标。
        target.DisplayMode = source.DisplayMode;
        target.FontSize = source.FontSize;
        target.ZIndex = source.ZIndex;
        target.CreatedAt = source.CreatedAt;
        target.UpdatedAt = source.UpdatedAt;
        target.DeletedAt = source.DeletedAt;
    }

    private void UpdateTitleBar(string? keyword)
    {
        if (!string.IsNullOrEmpty(keyword))
        {
            CurrentCategoryTitle = $"搜索：{keyword}（{Notes.Count} 个结果）";
        }
        else if (CurrentCategory is { } c && !c.IsGroupHeader)
        {
            CurrentCategoryTitle = $"{c.Title}（{Notes.Count}）";
        }
        else
        {
            CurrentCategoryTitle = string.Empty;
        }
    }

    // ===== 便签变更事件 =====

    private void OnNoteCreated(object? sender, NoteEventArgs e) => OnStructuralChange();

    private void OnNoteDeleted(object? sender, NoteEventArgs e) => OnStructuralChange();

    private void OnNoteRestored(object? sender, NoteEventArgs e) => OnStructuralChange();

    private void OnNotesBulkChanged(object? sender, EventArgs e) => OnStructuralChange();

    /// <summary>
    /// 结构性变化（新建 / 删除 / 恢复 / 批量增删）：快照失效，重新读一次并刷新列表。
    /// </summary>
    private void OnStructuralChange()
    {
        if (_isBatchOperating || _isNoteCreating)
        {
            return;
        }

        InvalidateNotesSnapshot();
        RefreshCategoryCounts();
        ReloadNotes();
    }

    /// <summary>
    /// 单条便签被更新。
    ///
    /// 这类事件绝大多数来自编辑器的自动保存（每停顿 500ms 一次），
    /// 所以这里刻意**不读数据库、不重建列表**：把新字段并回快照中的同一实例，
    /// 通知该项刷新显示，再让列表与目标顺序对齐即可 —— 整个过程零查询。
    /// 若该便签不在快照里（异常情况），退回全量刷新兜底。
    /// </summary>
    private void OnNoteUpdated(object? sender, NoteEventArgs e)
    {
        if (_isBatchOperating || _isNoteCreating)
        {
            return;
        }

        if (!TryPatchSnapshot(e.Note))
        {
            OnStructuralChange();
            return;
        }

        RefreshCategoryCounts();
        ReloadNotes();

        // 列表项与快照共享同一个 Note 实例，字段已就地改写，需通知派生显示属性刷新
        Notes.FirstOrDefault(x => x.Note.Id == e.Note.Id)?.NotifyNoteFieldsChanged();
    }

    private void OnReminderChanged(object? sender, ReminderEventArgs e)
    {
        if (_isBatchOperating)
        {
            return;
        }

        InvalidatePendingReminders();
        RefreshCategoryCounts();
        ReloadNotes();
    }

    private void OnPreviewTagsChanged(object? sender, EventArgs e)
    {
        InvalidateTagCache();
        RefreshCategoryCounts();
        ReloadNotes();
    }

    /// <summary>
    /// 便签与标签的关联发生变化（加/删标签、标签增删）。
    /// 便签字段没变，但列表项要显示的标签文字与「标签」分类的归属变了。
    /// </summary>
    private void OnTagAssignmentsChanged(object? sender, EventArgs e)
    {
        if (_isBatchOperating || _isNoteCreating)
        {
            return;
        }

        InvalidateTagCache();
        RebuildTagFilterOptions();
        RefreshCategoryCounts();
        ReloadNotes();
    }

    // ===== 批量操作辅助 =====

    private List<Guid> GetSelectedIds() => SelectedNotes.Select(n => n.Note.Id).ToList();

    /// <summary>遍历执行批量操作：单个失败不阻断整体，返回成功项并统计失败数；期间抑制事件触发的刷新。</summary>
    private List<Guid> RunBatch(IReadOnlyList<Guid> ids, Action<Guid> action, out int failed)
    {
        var succeeded = new List<Guid>();
        failed = 0;
        _isBatchOperating = true;
        try
        {
            foreach (var id in ids)
            {
                try
                {
                    action(id);
                    succeeded.Add(id);
                }
                catch (Exception ex)
                {
                    failed++;
                    LogHelper.Error($"批量操作失败：便签 {id}", ex);
                }
            }
        }
        finally
        {
            _isBatchOperating = false;
        }

        return succeeded;
    }

    /// <summary>批量操作收尾：记录结果、统一刷新列表与数量、清除多选。</summary>
    private void FinishBatch(int succeeded, int failed, string action)
    {
        LogHelper.Info($"{action}：成功 {succeeded}，失败 {failed}");
        RefreshCategoryCounts();
        ReloadNotes();
        ClearSelectionRequested?.Invoke(this, EventArgs.Empty);
    }
}
