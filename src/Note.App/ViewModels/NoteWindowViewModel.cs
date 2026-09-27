using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using NoteApp.Data.Repositories;
using NoteApp.Helpers;
using NoteApp.Models;
using NoteApp.Models.Enums;
using NoteApp.Services;

namespace NoteApp.ViewModels;

/// <summary>
/// 便签窗口 ViewModel：状态、命令、防抖自动保存、位置/尺寸保存。不含窗口 HWND 交互。
///
/// <para>
/// 行编辑、撤销栈、防抖保存、字体与配色解析全部继承自 <see cref="NoteEditorViewModelBase"/>，
/// 与主页编辑区 <see cref="NotePreviewViewModel"/> 共用同一份实现 —— 本类只保留窗口固有的差异
/// （贴边、几何、标题编辑、标签菜单、折叠/极简形态）。
/// </para>
/// </summary>
public partial class NoteWindowViewModel : NoteEditorViewModelBase
{
    private const int GeometryDebounceMilliseconds = 300;

    private readonly INoteService _noteService;
    private readonly ITagRepository _tagRepository;
    private readonly INoteEditorService _editorService;
    private readonly IReminderService _reminderService;
    private readonly ISettingsService _settingsService;
    private readonly DispatcherTimer _geometryTimer;

    /// <summary>当前便签已关联的标签 ID 集合（驱动「更多」菜单勾选状态）。</summary>
    private readonly HashSet<int> _noteTagIds;

    /// <summary>正在保存中标志：防止自己触发的 NoteUpdated 事件重新赋值 EditableTitle 导致光标重置。</summary>
    private bool _isSaving;

    [ObservableProperty]
    private string _content;

    /// <summary>图片附件（缩略图条）。与主页编辑区共用同一份实现，避免两处行为漂移。</summary>
    public AttachmentCollection Attachments { get; }

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    private bool _isLocked;

    /// <summary>
    /// 当前呈现形态（常规 / 折叠胶囊 / 极简磁贴）—— 形态判断的**唯一真相源**。
    ///
    /// 以前这里是两个独立的 bool（`_isCollapsed` / `_isMinimal`），互斥关系靠
    /// `ToggleCollapse` 与 `ToggleMinimal` 里两段手写 if 维持。问题是所有派生视觉
    /// （内容区可见性、标题栏可见性、状态栏、边框粗细、内边距、卷角、贴边资格…）
    /// 都要同时读这两个字段并各自组合，任何一处漏判就是一个边界 bug。
    ///
    /// 现在改为单一枚举：互斥由类型保证（写不出"既折叠又极简"），
    /// 派生属性只读这一个值。两个 bool 仍留在 <see cref="Note"/> 上用于落库，
    /// 由这里统一同步，业务代码不必再直接碰它们。
    /// </summary>
    [ObservableProperty]
    private NoteDisplayMode _displayMode = NoteDisplayMode.Normal;

    /// <summary>
    /// 是否启用「贴边自动隐藏」。默认**关闭** —— 这是会改变窗口行为的开关，
    /// 不该对老用户默认生效（便签"自己消失"会被当成 bug）。
    /// 逐条便签通过「更多」菜单开启。
    /// </summary>
    [ObservableProperty]
    private bool _isDockEnabled;

    /// <summary>当前贴靠的边（None 表示未贴边）。持久化后供下次启动恢复。</summary>
    [ObservableProperty]
    private DockEdge _dockEdge;

    /// <summary>
    /// 是否显示右下角「卷角」（翻折的纸角）。纯外观开关，默认**关闭** ——
    /// 老便签保持原样，只有用户通过「更多」菜单主动开启的便签才带这个细节。
    /// </summary>
    [ObservableProperty]
    private bool _isDogEar;

    /// <summary>不透明度（30-100）。</summary>
    [ObservableProperty]
    private int _opacity;

    /// <summary>是否已设置提醒（驱动标题栏铃铛高亮）。</summary>
    [ObservableProperty]
    private bool _hasReminder;

    /// <summary>是否处于标题编辑模式（标题栏显示输入框）。</summary>
    [ObservableProperty]
    private bool _isEditingTitle;

    /// <summary>标题编辑框的临时文本。</summary>
    [ObservableProperty]
    private string _editingTitleText = string.Empty;

    /// <summary>展开状态标题输入框的可编辑标题。</summary>
    [ObservableProperty]
    private string _editableTitle = string.Empty;

    /// <summary>所有标签（供「更多」菜单标签子菜单列表）。</summary>
    public ObservableCollection<Tag> AllTags { get; } = new();

    /// <summary>请求关闭窗口（View 订阅后调用 Close()）。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>请求打开新便签窗口（View 订阅后创建 NoteWindow）。</summary>
    public event EventHandler<NoteEventArgs>? NewNoteRequested;

    /// <summary>位置已保存（防抖 300ms 后触发）。</summary>
    public event EventHandler? WindowPositionChanged;

    /// <summary>尺寸已保存（防抖 300ms 后触发）。</summary>
    public event EventHandler? WindowSizeChanged;

    /// <summary>
    /// 请求窗口立即展开（用户从「更多」菜单关闭贴边时）。
    /// 由 View 订阅后调用 <c>DockController.ExpandNow()</c>。
    /// </summary>
    public event EventHandler? DockDisabled;

    /// <summary>
    /// 用户是否**主动**在菜单里取消过「贴边自动隐藏」。
    ///
    /// 存在的理由：现在"把便签拖到边缘"会自动开启贴边（否则用户根本不知道功能没开），
    /// 但用户若明确关掉过它，就不能再被自动打开 —— 否则就成了关不掉的功能。
    /// 这个标志**只在用户点击菜单项时置位，不持久化**：重启后恢复"拖到边缘即吸附"的
    /// 默认行为，符合"拖到边缘 = 想贴边"的直觉。
    /// </summary>
    public bool DockExplicitlyDisabled { get; private set; }

    /// <summary>由 View 在检测到"拖到边缘但功能未开"时调用：自动启用并落库。</summary>
    public void AutoEnableDock()
    {
        DockExplicitlyDisabled = false;
        SetDockEnabled(true);
    }

    // ===== 形态（唯一真相源 = DisplayMode）=====
    //
    // 下面两个是**只读派生属性**，只为兼容现有 XAML 绑定（DataTrigger 直接绑 IsCollapsed / IsMinimal）。
    // 它们没有 setter —— 想改形态只能动 DisplayMode，这样就不会出现"某个 bool 被单独改坏"。
    // 新增代码请优先用 DisplayMode / IsCompactDisplay / Is*Form 系列语义属性。

    /// <summary>是否折叠成胶囊。派生自 <see cref="DisplayMode"/>，只读。</summary>
    public bool IsCollapsed => DisplayMode == NoteDisplayMode.Collapsed;

    /// <summary>是否极简磁贴。派生自 <see cref="DisplayMode"/>，只读。</summary>
    public bool IsMinimal => DisplayMode == NoteDisplayMode.Minimal;

    /// <summary>是否处于某个非默认形态（折叠或极简都用得上「形态受限」的语义）。</summary>
    public bool IsCompactDisplay => DisplayMode != NoteDisplayMode.Normal;

    /// <summary>常规形态（标题栏与内容区同时可见）。</summary>
    public bool IsNormalForm => DisplayMode == NoteDisplayMode.Normal;

    public NoteWindowViewModel(INoteService noteService, ITagRepository tagRepository, INoteEditorService editorService, IReminderService reminderService, ISettingsService settingsService, IAttachmentService attachmentService, Guid noteId)
        : base(noteService.GetNote(noteId) ?? throw new InvalidOperationException($"便签不存在或已删除：{noteId}"), settingsService)
    {
        _noteService = noteService;
        _tagRepository = tagRepository;
        _editorService = editorService;
        _reminderService = reminderService;
        _settingsService = settingsService;
        Attachments = new AttachmentCollection(attachmentService);

        var note = Note;
        Attachments.Reload(noteId);
        _content = note.Content;
        SetUndoBaseline(note.Content);
        _isPinned = note.IsPinned;
        _isLocked = note.IsLocked;
        // 形态从模型读取：Note.DisplayMode 会把两个旧的 bool 列**安全归并**
        // （外部数据可能两个都为 true，那时按折叠处理，见 Note.DisplayMode 的注释）。
        _displayMode = note.DisplayMode;
        _isDockEnabled = note.IsDockEnabled;
        _dockEdge = note.DockEdge;
        _isDogEar = note.IsDogEar;
        _opacity = note.Opacity;
        _hasReminder = reminderService.HasReminder(noteId);
        _editableTitle = note.Title ?? string.Empty;

        // 从 Content 分割行（初始化待办事项渲染视图）
        RebuildLinesFromContent();

        _noteTagIds = _tagRepository.GetTagsForNote(noteId).Select(t => t.Id).ToHashSet();
        ReloadAllTags();

        _geometryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(GeometryDebounceMilliseconds) };
        _geometryTimer.Tick += OnGeometryTimerTick;

        _noteService.NoteUpdated += OnNoteUpdated;
        _noteService.NoteDeleted += OnNoteDeleted;
        _reminderService.ReminderCreated += OnReminderChanged;
        _reminderService.ReminderRemoved += OnReminderChanged;
        _settingsService.SettingChanged += OnSettingChanged;
    }

    /// <summary>防抖计时到点：便签窗口无条件落库（几何/状态变更依赖它有副作用地刷新 UpdatedAt）。</summary>
    protected override void OnAutosaveElapsed() => Save();

    /// <summary>纯文本真源是 <see cref="Content"/>。</summary>
    protected override string RawContent => Content;

    protected override void SetRawContent(string value) => Content = value;

    // ===== 派生属性 =====

    /// <summary>标题字面文本（用户显式设置的标题，未设置时为 null）。列表/占位符用；派生显示见基类 <see cref="NoteEditorViewModelBase.Title"/>。</summary>
    public string? NoteTitle => Note.Title;

    public NoteColor Color => Note.Color;

    /// <summary>窗口不透明度（0.3-1.0）。</summary>
    public double WindowOpacity => Opacity / 100.0;

    /// <summary>内容区可见性：折叠态收起（只剩胶囊），常规与极简都显示。</summary>
    public Visibility ContentVisibility => IsCollapsed ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>标题栏可见性：极简态隐藏（去装饰），常规与折叠都显示（折叠时它就是那个胶囊）。</summary>
    public Visibility TitleBarVisibility => IsMinimal ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>状态栏（字数 / 相对时间）：只在常规形态出现 -- 折叠态没有内容、极简态不要装饰。</summary>
    public Visibility StatusBarVisibility => IsCompactDisplay ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>极简模式边框更细（1px）。</summary>
    public Thickness NoteBorderThickness => IsMinimal ? new Thickness(1) : new Thickness(0.8);

    /// <summary>便签窗口圆角统一 12px。</summary>
    public CornerRadius NoteCornerRadius => new CornerRadius(12);

    // ===== 卷角（右下角翻折纸角）=====
    //
    // 视觉由三块拼成，全部取自这张便签**自己的**配色，不引入任何外部色：
    //   ① 折角亮面（纸背面）：比便签底色更亮一点 —— 翻过来的一面朝光，本就比正面亮；
    //   ② 折痕阴影：便签底色压暗一点，沿折线铺一段渐变，做出「掀起来了」的厚度；
    //   ③ 折角外沿描边：用便签描边色，跟窗口四周的边框同源。
    // 之前吃过「用品牌橙画在便签上」的亏（色彩不自洽），这里刻意守住。

    /// <summary>
    /// 卷角是否可见：仅折叠胶囊不显示（折成一条胶囊后没有"角"可言）；
    /// 常规与**极简都显示** —— 极简是"只留内容的磁贴"，卷角正是内容区的一部分。
    /// </summary>
    public Visibility DogEarVisibility =>
        IsDogEar && !IsCollapsed ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>卷角尺寸（DIP）：与圆角 12 协调，做成 22 的等腰直角三角形。</summary>
    public double DogEarSize => 22;

    /// <summary>折角亮面画刷（纸背面）。由便签底色提亮派生。</summary>
    public Brush DogEarFaceBrush => CreateDogEarBrush(DogEarPalette.FaceLightenAmount);

    /// <summary>折痕阴影画刷：便签底色压暗，做出翻折的立体感。</summary>
    public Brush DogEarShadowBrush => CreateDogEarBrush(DogEarPalette.ShadowDarkenAmount);

    /// <summary>
    /// 由便签自己底色的明度偏移派生一个纯色画刷。
    /// 与 <see cref="NoteEditorViewModelBase.NoteForegroundColor"/> 同理，这里也排除了
    /// 资源缺失时的全透明兜底 —— 若拿到无效底色，退回浅米白，宁可不精确也不要隐形
    /// （否则卷角会"什么都没画"，表象像功能坏了）。
    /// </summary>
    private Brush CreateDogEarBrush(double lighten)
    {
        var baseColor = NoteBackground is SolidColorBrush { Color.A: > 0 } brush
            ? brush.Color
            : System.Windows.Media.Color.FromRgb(0xF3, 0xF0, 0xEA);

        return new SolidColorBrush(DogEarPalette.ShiftBrightness(baseColor, lighten));
    }

    /// <summary>便签换色时刷新卷角的派生画刷（卷角取自便签底色，换色必须跟着变）。</summary>
    private void NotifyDogEarColorDependentProperties()
    {
        OnPropertyChanged(nameof(DogEarFaceBrush));
        OnPropertyChanged(nameof(DogEarShadowBrush));
    }

    /// <summary>极简模式内容区内边距更紧凑。</summary>
    public Thickness ContentPadding => IsMinimal ? new Thickness(8, 6, 8, 6) : new Thickness(12, 8, 12, 8);

    /// <summary>
    /// 内容列表底部留白：开启卷角时额外留出折角高度，
    /// 否则最后一行文字会滑到折角底下被压住（极简模式没有状态栏兜底，尤其明显）。
    /// </summary>
    public Thickness ContentBottomGap => IsDogEar ? new Thickness(0, 0, 0, DogEarSize) : new Thickness(0);

    /// <summary>
    /// 未完成待办数（驱动折叠态徽标）。折叠态看不到内容，这个数字是唯一的进度提示。
    /// </summary>
    public int UncheckedTodoCount => Lines.Count(l => l.LineType == NoteLineType.Todo && !l.IsChecked);

    /// <summary>折叠态是否显示待办徽标（有未完成待办才显示，避免空白便签挂个 0）。</summary>
    public Visibility TodoBadgeVisibility =>
        IsCollapsed && UncheckedTodoCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    public bool IsReadOnly => IsLocked;

    public Visibility LockIconVisibility => IsLocked ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>低透明度（&lt;50）时内容文字需加深为纯黑。</summary>
    public bool IsLowOpacity => Opacity < 50;

    // ===== 属性联动 =====

    partial void OnContentChanged(string value) => OnRawContentChanged(value);

    // ===== 行集合差异点 =====

    /// <summary>
    /// 行合并回纯文本后的补充：除基类的公共处理外，还要并入撤销基线（已在基类完成）
    /// 与刷新标题派生属性。
    /// </summary>
    protected override void OnLinesMergedToContent()
    {
        base.OnLinesMergedToContent();
        Note.Content = Content;
        OnPropertyChanged(nameof(Title));
    }

    /// <summary>行集合重建后刷新折叠态未完成待办徽标（内容整体换过，计数大概率变了）。</summary>
    protected override void OnLinesRebuilt()
    {
        OnPropertyChanged(nameof(UncheckedTodoCount));
        OnPropertyChanged(nameof(TodoBadgeVisibility));
    }

    /// <summary>待办勾选状态变化：刷新折叠态徽标的未完成计数（不触发内容保存）。</summary>
    public override void NotifyTodoStateChanged()
    {
        OnPropertyChanged(nameof(UncheckedTodoCount));
        OnPropertyChanged(nameof(TodoBadgeVisibility));
    }

    partial void OnEditableTitleChanged(string value)
    {
        Note.Title = value;
        OnPropertyChanged(nameof(Title));
        RestartAutosave();
    }

    /// <summary>
    /// 形态变化：一次性刷新所有与形态相关的派生视觉，并把形态**写回模型**（落库）。
    ///
    /// 收口前这里是两个独立的 `OnIsCollapsedChanged` / `OnIsMinimalChanged`，
    /// 各通知一部分属性 —— 于是"切换折叠"时忘通知某个极简相关属性这种漏网之鱼
    /// 就是必然的。现在只有一个入口，通知清单集中在一处，新增形态只需改这里。
    /// </summary>
    partial void OnDisplayModeChanged(NoteDisplayMode value)
    {
        // 落库：两个旧列由模型的 DisplayMode setter 负责同步，业务层不直接碰 bool。
        Note.DisplayMode = value;

        // —— 形态直接决定的可见性 ——
        OnPropertyChanged(nameof(IsCollapsed));
        OnPropertyChanged(nameof(IsMinimal));
        OnPropertyChanged(nameof(IsCompactDisplay));
        OnPropertyChanged(nameof(IsNormalForm));
        OnPropertyChanged(nameof(ContentVisibility));
        OnPropertyChanged(nameof(TitleBarVisibility));
        OnPropertyChanged(nameof(StatusBarVisibility));
        OnPropertyChanged(nameof(TodoBadgeVisibility));
        OnPropertyChanged(nameof(DogEarVisibility));

        // —— 形态影响的尺寸/描边细节 ——
        OnPropertyChanged(nameof(NoteBorderThickness));
        OnPropertyChanged(nameof(NoteCornerRadius));
        OnPropertyChanged(nameof(ContentPadding));
    }

    partial void OnIsDogEarChanged(bool value)
    {
        OnPropertyChanged(nameof(DogEarVisibility));
        OnPropertyChanged(nameof(DogEarFaceBrush));
        OnPropertyChanged(nameof(DogEarShadowBrush));
        OnPropertyChanged(nameof(ContentBottomGap));
    }

    partial void OnIsLockedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(LockIconVisibility));
    }

    partial void OnOpacityChanged(int value)
    {
        OnPropertyChanged(nameof(WindowOpacity));
        OnPropertyChanged(nameof(IsLowOpacity));
    }

    // ===== 命令 =====

    /// <summary>手动/自动保存：更新 UpdatedAt 并写库，异常静默记录不弹窗。</summary>
    [RelayCommand]
    private void Save()
    {
        StopAutosave();
        _isSaving = true;
        Note.UpdatedAt = DateTime.Now;

        try
        {
            _noteService.UpdateNote(Note);
            LogHelper.Debug($"便签保存：{Note.Id}");
        }
        catch (Exception ex)
        {
            LogHelper.Error($"便签保存失败：{Note.Id}", ex);
        }
        finally
        {
            _isSaving = false;
        }
    }

    [RelayCommand]
    private void TogglePin()
    {
        IsPinned = !IsPinned;
        Note.IsPinned = IsPinned;
        Save();
    }

    [RelayCommand]
    private void ToggleLock()
    {
        IsLocked = !IsLocked;
        Note.IsLocked = IsLocked;
        Save();
    }

    /// <summary>
    /// 折叠 / 展开切换。折叠与极简互斥 —— 但**互斥不再需要手写**：
    /// 形态是单一枚举，赋 Collapsed 本身就意味着不再是 Minimal。
    /// 收口前这里要额外写一段"如果极简开着就关掉它"，而 ToggleMinimal 里还要再写一遍反过来的。
    /// </summary>
    [RelayCommand]
    private void ToggleCollapse()
    {
        DisplayMode = IsCollapsed ? NoteDisplayMode.Normal : NoteDisplayMode.Collapsed;
        Save();
    }

    /// <summary>切换极简模式（桌面磁贴）：隐藏标题栏，只显示内容区。与折叠互斥（同样由枚举天然保证）。</summary>
    [RelayCommand]
    private void ToggleMinimal()
    {
        DisplayMode = IsMinimal ? NoteDisplayMode.Normal : NoteDisplayMode.Minimal;
        Save();
    }

    /// <summary>切换「贴边自动隐藏」：开启后拖到屏幕左右边缘松手会自动收成 5px 色条，鼠标靠近滑出。</summary>
    [RelayCommand]
    private void ToggleDock()
    {
        var next = !IsDockEnabled;

        // 用户主动点菜单才算"明确表态"。关掉之后就不再被"拖到边缘"自动打开，
        // 否则这个开关按不下去（关掉 → 下次拖到边缘又被打开）。
        DockExplicitlyDisabled = !next;

        SetDockEnabled(next);
    }

    /// <summary>切换「卷角」外观：右下角显示一个翻折的纸角。纯装饰，落库后下次启动保持。</summary>
    [RelayCommand]
    private void ToggleDogEar()
    {
        IsDogEar = !IsDogEar;
        Note.IsDogEar = IsDogEar;
        Save();
    }

    /// <summary>进入标题编辑模式：预填当前标题（无标题时为空），由 View 聚焦输入框。</summary>
    [RelayCommand]
    private void StartEditTitle()
    {
        EditingTitleText = Note.Title ?? string.Empty;
        IsEditingTitle = true;
    }

    /// <summary>保存标题：Trim 后为空则置 null（恢复显示内容预览），落库并退出编辑。</summary>
    [RelayCommand]
    private void SaveTitle()
    {
        var trimmed = EditingTitleText?.Trim();
        var newTitle = string.IsNullOrEmpty(trimmed) ? null : trimmed;

        Note.Title = newTitle;
        OnPropertyChanged(nameof(Title));
        IsEditingTitle = false;

        try
        {
            _noteService.UpdateNoteTitle(Note.Id, newTitle);
        }
        catch (Exception ex)
        {
            LogHelper.Error($"标题保存失败：{Note.Id}", ex);
        }
    }

    /// <summary>取消标题编辑：不保存，恢复原标题显示。</summary>
    [RelayCommand]
    private void CancelEditTitle()
    {
        IsEditingTitle = false;
    }

    /// <summary>当前便签是否已包含指定标签（驱动标签子菜单勾选状态）。</summary>
    public bool HasTag(int tagId) => _noteTagIds.Contains(tagId);

    /// <summary>重新加载所有标签（每次打开「更多」菜单时调用以即时刷新）。</summary>
    public void ReloadAllTags()
    {
        AllTags.Clear();
        foreach (var tag in _tagRepository.GetAll())
        {
            AllTags.Add(tag);
        }
    }

    /// <summary>切换便签的某个标签（有则移除、无则添加），并同步本地勾选集合。</summary>
    [RelayCommand]
    private void ToggleTag(int tagId)
    {
        _noteService.ToggleTag(Note.Id, tagId);

        if (_noteTagIds.Contains(tagId))
        {
            _noteTagIds.Remove(tagId);
        }
        else
        {
            _noteTagIds.Add(tagId);
        }
    }

    [RelayCommand]
    private void SetColor(NoteColor color)
    {
        if (Note.Color == color)
        {
            return;
        }

        Note.Color = color;
        OnPropertyChanged(nameof(Color));
        NotifyColorDependentProperties();
        NotifyDogEarColorDependentProperties();
        Save();
    }

    [RelayCommand]
    private void SetOpacity(int opacity) => ApplyOpacity(opacity, persist: true);

    /// <summary>滑块实时调节：仅更新窗口透明度，不落库（拖动结束由 <see cref="PersistOpacity"/> 落库）。</summary>
    public void SetOpacityLive(int opacity) => ApplyOpacity(opacity, persist: false);

    /// <summary>不透明度拖动结束落库。</summary>
    public void PersistOpacity() => Save();

    [RelayCommand]
    private void Close()
    {
        Save();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Delete() => _editorService.DeleteNote(Note.Id, permanent: false);

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

        _editorService.DeleteNote(Note.Id, permanent: true);
    }

    [RelayCommand]
    private void NewNote()
    {
        var note = _noteService.CreateNote();
        NewNoteRequested?.Invoke(this, new NoteEventArgs(note));
    }

    // ===== 窗口几何 =====

    /// <summary>拖拽移动后更新位置（防抖 300ms 后落库）。</summary>
    public void UpdateWindowPosition(double left, double top)
    {
        Note.PositionX = (int)left;
        Note.PositionY = (int)top;
        RestartGeometryTimer();
    }

    /// <summary>缩放后更新尺寸（防抖 300ms 后落库）。</summary>
    public void UpdateWindowSize(double width, double height)
    {
        Note.Width = (int)width;
        Note.Height = (int)height;
        RestartGeometryTimer();
    }

    // ===== 贴边自动隐藏 =====

    /// <summary>
    /// 更新贴边边并落库。
    ///
    /// ⚠️ 刻意**不触发 <see cref="NoteUpdated"/> 事件** —— 与 <see cref="UpdateWindowPosition"/> 同理：
    /// 贴边状态在拖动结束时变化，若抛事件会让主页列表跟着重建，造成不必要的 UI 抖动。
    /// </summary>
    public void UpdateDockEdge(DockEdge edge)
    {
        if (DockEdge == edge)
        {
            return;
        }

        DockEdge = edge;
        Note.DockEdge = edge;
        _noteService.UpdateDockState(Note.Id, IsDockEnabled, edge);
    }

    /// <summary>切换「贴边自动隐藏」开关并落库。</summary>
    public void SetDockEnabled(bool enabled)
    {
        if (IsDockEnabled == enabled)
        {
            return;
        }

        IsDockEnabled = enabled;
        Note.IsDockEnabled = enabled;

        if (!enabled)
        {
            // 关闭时清掉贴边记录，否则下次开启会"突然"贴回原来那条边
            DockEdge = DockEdge.None;
            Note.DockEdge = DockEdge.None;
            DockDisabled?.Invoke(this, EventArgs.Empty);
        }

        _noteService.UpdateDockState(Note.Id, enabled, Note.DockEdge);
    }

    // ===== 事件转发 =====

    private void OnNoteUpdated(object? sender, NoteEventArgs e)
    {
        if (e.Note.Id != Note.Id)
        {
            return;
        }

        // 同步外部状态更新
        IsPinned = e.Note.IsPinned;
        IsLocked = e.Note.IsLocked;
        // 形态也从模型取（外部可能通过列表窗口改了折叠/极简）。
        // 走 DisplayMode 而非直接赋两个 bool：非法组合（都 true）在这里就被归并掉，
        // 不会把坏状态传进 VM。
        DisplayMode = e.Note.DisplayMode;
        IsDogEar = e.Note.IsDogEar;
        Opacity = e.Note.Opacity;
        OnPropertyChanged(nameof(Color));
        NotifyColorDependentProperties();
        NotifyDogEarColorDependentProperties();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(WordCount));
        OnPropertyChanged(nameof(RelativeUpdateTime));

        // 保存期间不更新 EditableTitle 和 Content，避免 UI 重新绑定导致光标跳到最前面
        if (!_isSaving)
        {
            EditableTitle = e.Note.Title ?? string.Empty;

            // 同步外部内容更新（如主页编辑区修改），内容不同时才更新，避免打断输入
            if (e.Note.Content != Content)
            {
                Note.Content = e.Note.Content;
                Content = e.Note.Content;
                // 外部变更并入撤销基线，不产生撤销步
                SetUndoBaseline(e.Note.Content);
            }
        }
    }

    private void OnNoteDeleted(object? sender, NoteEventArgs e)
    {
        if (e.Note.Id != Note.Id)
        {
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>提醒创建/删除后刷新铃铛高亮状态（仅响应本便签的提醒）。</summary>
    private void OnReminderChanged(object? sender, ReminderEventArgs e)
    {
        if (e.Reminder.NoteId != Note.Id)
        {
            return;
        }

        HasReminder = _reminderService.HasReminder(Note.Id);
    }

    /// <summary>设置变更：便签字体切换时即时刷新内容区字体（逻辑与主页编辑区共用基类实现）。</summary>
    private void OnSettingChanged(object? sender, SettingChangedEventArgs e) =>
        HandleSettingChanged(sender, e, _settingsService);

    // ===== 生命周期 =====

    /// <summary>窗口关闭时调用：停止计时器并取消事件订阅，避免单例 NoteService 持有本 VM 导致泄漏。</summary>
    public void Cleanup()
    {
        StopAutosave();
        _geometryTimer.Stop();
        _noteService.NoteUpdated -= OnNoteUpdated;
        _noteService.NoteDeleted -= OnNoteDeleted;
        _reminderService.ReminderCreated -= OnReminderChanged;
        _reminderService.ReminderRemoved -= OnReminderChanged;
        _settingsService.SettingChanged -= OnSettingChanged;
    }

    // ===== 内部 =====

    private void ApplyOpacity(int opacity, bool persist)
    {
        opacity = Math.Clamp(opacity, 30, 100);
        if (Note.Opacity == opacity)
        {
            return;
        }

        Note.Opacity = opacity;
        Opacity = opacity;
        if (persist)
        {
            Save();
        }
    }

    private void RestartGeometryTimer()
    {
        _geometryTimer.Stop();
        _geometryTimer.Start();
    }

    private void OnGeometryTimerTick(object? sender, EventArgs e)
    {
        _geometryTimer.Stop();
        // 仅保存窗口几何（位置+尺寸），不触发 NoteUpdated 事件，避免移动便签时主页列表刷新
        _noteService.UpdateNoteGeometry(Note.Id, Note.PositionX, Note.PositionY, Note.Width, Note.Height);
        WindowPositionChanged?.Invoke(this, EventArgs.Empty);
        WindowSizeChanged?.Invoke(this, EventArgs.Empty);
    }
}
