using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

using NoteApp.Data.Repositories;
using NoteApp.Helpers;
using NoteApp.Models;
using NoteApp.Models.Enums;
using NoteApp.Services;
using NoteApp.ViewModels;

namespace NoteApp.Views;

/// <summary>便签列表面板：标准窗口（有标题栏、显示在任务栏），三栏布局 + Mica 材质。列表/筛选/预览/批量逻辑在 NoteListViewModel，此处仅保留窗口交互（多选同步、下拉菜单、Mica）。</summary>
public partial class NoteListView : Window, NoteLineEditor.IHost
{
    private readonly NoteListViewModel _viewModel;
    private readonly NoteLineEditor _lineEditor;
    private readonly ITagRepository _tagRepository;
    private readonly IThemeService _themeService;
    private bool _allowClose;

    /// <summary>当前已挂接行重建通知的预览 VM（切换便签时换绑，避免事件残留）。</summary>
    private NotePreviewViewModel? _subscribedPreview;

    private static readonly ObservableCollection<NoteLineViewModel> EmptyLines = new();

    public NoteListView()
    {
        InitializeComponent();

        var noteService = ServiceLocator.GetRequiredService<INoteService>();
        var tagRepository = ServiceLocator.GetRequiredService<ITagRepository>();
        var reminderService = ServiceLocator.GetRequiredService<IReminderService>();
        var windowService = ServiceLocator.GetRequiredService<IWindowService>();
        var noteEditorService = ServiceLocator.GetRequiredService<INoteEditorService>();
        var settingsService = ServiceLocator.GetRequiredService<ISettingsService>();
        var attachmentService = ServiceLocator.GetRequiredService<IAttachmentService>();
        _themeService = ServiceLocator.GetRequiredService<IThemeService>();
        _viewModel = new NoteListViewModel(noteService, tagRepository, reminderService, windowService, noteEditorService, settingsService, attachmentService);
        _tagRepository = tagRepository;
        DataContext = _viewModel;
        _lineEditor = new NoteLineEditor(this, PreviewContentItemsControl, PreviewContentScroll);

        // 切换便签时重置行编辑器状态（拖选锚点、跨行选区引用旧便签的行）
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // 多选同步（SelectedItems 不可绑定）：VM 请求全选/清除时，由 View 操作 ListBox
        _viewModel.SelectAllRequested += (_, _) => NoteListBox.SelectAll();
        _viewModel.ClearSelectionRequested += (_, _) => NoteListBox.UnselectAll();

        // 新建便签后滚动到新便签位置（确保用户能看到高亮的新便签）
        _viewModel.ScrollToNoteRequested += (_, note) =>
        {
            if (note is not null)
            {
                NoteListBox.ScrollIntoView(note);
            }
        };

        // 设置标题栏颜色（根据当前主题自适应浅色/深色，解决某些系统主题下窗口选中时标题栏变黑的问题）
        TitleBarColorHelper.ApplyByCurrentTheme(this);

        // 订阅主题变化事件，主题切换时实时更新标题栏颜色
        _themeService.ThemeChanged += OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, AppTheme e)
    {
        // 主题变化时重新应用标题栏颜色（Dispatcher 确保在 UI 线程执行）
        Dispatcher.Invoke(() => TitleBarColorHelper.ApplyByCurrentTheme(this));
    }

    /// <summary>切换当前编辑便签时重置行编辑器（跨行选区引用旧便签的行）。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(NoteListViewModel.CurrentNote))
        {
            return;
        }

        // 旧便签不再被编辑，先退订它的行重建通知
        if (_subscribedPreview is not null)
        {
            _subscribedPreview.LinesRebuilt -= OnLinesRebuilt;
            _subscribedPreview = null;
        }

        _lineEditor.Reset();

        // 新便签的行集合整体重建时需要作废编辑器缓存
        if (_viewModel.CurrentNote is { } preview)
        {
            preview.LinesRebuilt += OnLinesRebuilt;
            _subscribedPreview = preview;
        }
    }

    /// <summary>行集合整体重建后，缓存里的 TextBox 已随容器卸载，必须作废。</summary>
    private void OnLinesRebuilt(object? sender, EventArgs e) => _lineEditor.InvalidateBoxCache();

    protected override void OnClosed(EventArgs e)
    {
        _themeService.ThemeChanged -= OnThemeChanged;
        if (_subscribedPreview is not null)
        {
            _subscribedPreview.LinesRebuilt -= OnLinesRebuilt;
            _subscribedPreview = null;
        }
        _lineEditor.Detach();
        _viewModel.Cleanup();
        base.OnClosed(e);
    }

    /// <summary>关闭时隐藏而非销毁（保留列表状态，下次打开瞬间显示）；仅应用退出时真正关闭。</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        Hide();
    }

    /// <summary>应用退出时真正关闭窗口（跳过隐藏逻辑）。</summary>
    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // TODO: 后续可恢复 Mica。当前 Mica 与透明背景（alpha=0）组合会导致中间列表区域渲染成黑色，
        // 故暂时放弃 Mica，窗口背景直接使用 XAML 中声明的纯色 #F3F0EA（无需任何透明设置）。
        // if (TryApplyMica())
        // {
        //     Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255));
        // }
    }

    /// <summary>多选同步：把 ListBox 选中项同步到 ViewModel.SelectedNotes（驱动批量操作栏与数量）。</summary>
    private void NoteList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _viewModel.SelectedNotes = NoteListBox.SelectedItems.Cast<NoteItemViewModel>().ToList();
    }

    /// <summary>侧边栏分组标题不可选中（但标题右侧「+」按钮点击需放行）。</summary>
    private void CategoryList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = TreeWalker.FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is CategoryItem { IsGroupHeader: true } &&
            TreeWalker.FindAncestor<Button>(e.OriginalSource as DependencyObject) is null)
        {
            e.Handled = true;
        }
    }

    /// <summary>点击侧边栏设置按钮：复用 App 的单例设置窗口打开逻辑。</summary>
    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
        {
            app.OpenSettings();
        }
    }

    /// <summary>双击列表项：打开对应便签窗口（不关闭列表面板）；批量模式下双击不打开避免误操作。</summary>
    private void NoteList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource))
        {
            return;
        }

        if (_viewModel.IsBatchMode)
        {
            return;
        }

        if (GetNoteItem(e.OriginalSource) is { } item)
        {
            _viewModel.OpenNoteCommand.Execute(item.Note.Id);
        }
    }

    /// <summary>右键列表项：选中并弹出上下文菜单。</summary>
    private void NoteList_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (GetNoteItem(e.OriginalSource) is not { } item)
        {
            return;
        }

        _viewModel.SelectedNote = item;

        var menu = BuildNoteContextMenu(item);
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>构建列表项右键菜单（回收站项与普通项不同）。</summary>
    private ContextMenu BuildNoteContextMenu(NoteItemViewModel item)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("NoteContextMenuStyle") };
        var itemStyle = (Style)FindResource("NoteMenuItemStyle");
        var separatorStyle = (Style)FindResource("NoteMenuSeparatorStyle");

        if (item.IsInTrash)
        {
            var restoreItem = new MenuItem { Header = "恢复", Icon = MakeIcon("IconArchive"), Style = itemStyle };
            restoreItem.Click += (_, _) => item.RestoreCommand.Execute(null);

            var permanentItem = new MenuItem { Header = "永久删除", Icon = MakeIcon("IconTrash"), Style = itemStyle };
            permanentItem.Click += (_, _) => item.PermanentDeleteCommand.Execute(null);

            menu.Items.Add(restoreItem);
            menu.Items.Add(permanentItem);
            return menu;
        }

        var pinItem = new MenuItem { Header = "置顶", Icon = MakeIcon("IconPin"), Style = itemStyle, IsCheckable = true, IsChecked = item.IsPinned };
        pinItem.Click += (_, _) => item.TogglePinCommand.Execute(null);

        var lockItem = new MenuItem { Header = "锁定", Icon = MakeIcon("IconLock"), Style = itemStyle, IsCheckable = true, IsChecked = item.IsLocked };
        lockItem.Click += (_, _) => item.ToggleLockCommand.Execute(null);

        var archiveItem = new MenuItem { Header = "归档", Icon = MakeIcon("IconArchive"), Style = itemStyle, IsCheckable = true, IsChecked = item.IsArchived };
        archiveItem.Click += (_, _) => item.ToggleArchiveCommand.Execute(null);

        var deleteItem = new MenuItem { Header = "删除", Icon = MakeIcon("IconTrash"), Style = itemStyle };
        deleteItem.Click += (_, _) => item.DeleteCommand.Execute(null);

        menu.Items.Add(pinItem);
        menu.Items.Add(lockItem);
        menu.Items.Add(archiveItem);
        menu.Items.Add(new Separator { Style = separatorStyle });
        menu.Items.Add(deleteItem);

        return menu;
    }

    /// <summary>点击「+ 添加标签」：弹出可用标签下拉菜单（复用便签菜单样式）。</summary>
    private void AddTagButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.CurrentNote is not { } current)
        {
            return;
        }

        var menu = new ContextMenu { Style = (Style)FindResource("NoteContextMenuStyle") };
        var itemStyle = (Style)FindResource("NoteMenuItemStyle");
        var separatorStyle = (Style)FindResource("NoteMenuSeparatorStyle");

        var existingIds = current.Tags.Select(t => t.Id).ToHashSet();
        var available = _tagRepository.GetAll().Where(t => !existingIds.Contains(t.Id)).ToList();

        if (available.Count == 0)
        {
            var empty = new MenuItem { Header = "暂无可用标签", IsEnabled = false, Style = itemStyle };
            menu.Items.Add(empty);
        }
        else
        {
            foreach (var tag in available)
            {
                var item = new MenuItem { Header = tag.Name, Icon = MakeTagSwatch(tag.Color), Style = itemStyle };
                item.Click += (_, _) => current.AddTagCommand.Execute(tag.Id);
                menu.Items.Add(item);
            }
        }

        // 新建标签入口：即使无可用标签也可创建，避免「暂无可用标签」成为死路
        // 创建成功后自动把新标签加到当前便签上
        menu.Items.Add(new Separator { Style = separatorStyle });
        var newTagItem = new MenuItem { Header = "新建标签…", Icon = MakeIcon("IconPlus"), Style = itemStyle };
        newTagItem.Click += (_, _) => CreateTagFlow(tagId => current.AddTagCommand.Execute(tagId));
        menu.Items.Add(newTagItem);

        menu.PlacementTarget = sender as UIElement;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>点击侧边栏「标签」分组标题右侧的「+」：打开新建标签对话框。</summary>
    private void NewTagButton_Click(object sender, RoutedEventArgs e) => CreateTagFlow();

    /// <summary>右键侧边栏标签项：弹出「删除标签」菜单。</summary>
    private void CategoryList_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var item = TreeWalker.FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not CategoryItem { CategoryType: CategoryType.Tag, TagId: { } tagId })
        {
            return;
        }

        var menu = new ContextMenu { Style = (Style)FindResource("NoteContextMenuStyle") };
        var itemStyle = (Style)FindResource("NoteMenuItemStyle");

        var deleteItem = new MenuItem { Header = "删除标签", Icon = MakeIcon("IconTrash"), Style = itemStyle };
        deleteItem.Click += (_, _) => DeleteTagFlow(tagId);
        menu.Items.Add(deleteItem);

        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>删除标签流程：确认后删除（便签上的该标签关联一并移除），Toast 提示结果。</summary>
    private void DeleteTagFlow(int tagId)
    {
        var tag = _tagRepository.GetById(tagId);
        if (tag is null)
        {
            return;
        }

        var usageCount = _tagRepository.GetUsageCount(tagId);
        var message = usageCount > 0
            ? $"确定要删除标签「{tag.Name}」吗？\n\n该标签已用于 {usageCount} 条便签，删除后这些便签上的此标签将同时移除。"
            : $"确定要删除标签「{tag.Name}」吗？";
        var result = MessageBox.Show(message, "删除标签", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            _viewModel.DeleteTag(tagId);
            ToastWindow.Show($"已删除标签「{tag.Name}」");
        }
        catch (Exception ex)
        {
            LogHelper.Error($"删除标签失败：{tagId}", ex);
            ToastWindow.Show("删除标签失败，请重试");
        }
    }

    /// <summary>打开新建标签对话框，确认后创建标签并刷新侧边栏；重名经 Toast 提示。</summary>
    /// <param name="onCreated">创建成功后的回调，参数为新标签的 ID；为 null 时不执行额外操作。</param>
    private void CreateTagFlow(Action<int>? onCreated = null)
    {
        var dialog = new TagCreateDialog { Owner = this };
        if (dialog.ShowDialog() == true && dialog.TagName is { } name)
        {
            try
            {
                var tagId = _viewModel.CreateTag(name, dialog.TagColor);
                onCreated?.Invoke(tagId);
            }
            catch (InvalidOperationException ex)
            {
                ToastWindow.Show(ex.Message);
            }
        }
    }

    /// <summary>点击编辑区「颜色」：弹出 12 色下拉，作用于当前便签。</summary>
    private void ColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.CurrentNote is not { } current)
        {
            return;
        }

        var menu = new ContextMenu { Style = (Style)FindResource("NoteContextMenuStyle") };
        var itemStyle = (Style)FindResource("NoteMenuItemStyle");

        foreach (var color in NoteColorHelper.All)
        {
            var item = new MenuItem
            {
                Header = NoteColorHelper.GetDisplayName(color),
                Icon = MakeTagSwatch(color),
                Style = itemStyle,
                IsCheckable = true,
                IsChecked = current.Note.Color == color,
            };
            item.Click += (_, _) => current.SetColorCommand.Execute(color);
            menu.Items.Add(item);
        }

        menu.PlacementTarget = sender as UIElement;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>点击编辑区「提醒」：打开提醒设置弹窗。</summary>
    private void ReminderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.CurrentNote is not { } current)
        {
            return;
        }

        var reminderService = ServiceLocator.GetRequiredService<IReminderService>();
        var existing = reminderService.GetRemindersForNote(current.Note.Id)
            .FirstOrDefault(r => !r.IsCompleted);

        var picker = new ReminderPicker(current.Note.Id, existing) { Owner = this };
        picker.Left = Left + (ActualWidth - picker.Width) / 2;
        picker.Top = Top + (ActualHeight - picker.Height) / 2;
        picker.ShowDialog();
    }

    /// <summary>标签色板（14×14 圆角方块）。</summary>
    private static Border MakeTagSwatch(NoteColor color) => new Border
    {
        Width = 14, Height = 14, CornerRadius = new CornerRadius(3),
        Background = (Brush)Application.Current.FindResource($"Note{color}_Bg"),
        BorderBrush = (Brush)Application.Current.FindResource($"Note{color}_Border"),
        BorderThickness = new Thickness(1),
    };

    /// <summary>点击批量「改色」：弹出 12 色下拉，选中后批量应用到所有选中便签。</summary>
    private void BatchColorButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("NoteContextMenuStyle") };
        var itemStyle = (Style)FindResource("NoteMenuItemStyle");

        foreach (var color in NoteColorHelper.All)
        {
            var item = new MenuItem { Header = NoteColorHelper.GetDisplayName(color), Icon = MakeTagSwatch(color), Style = itemStyle };
            item.Click += (_, _) => _viewModel.BatchSetColorCommand.Execute(color.ToString());
            menu.Items.Add(item);
        }

        menu.PlacementTarget = sender as UIElement;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>点击批量「移动标签」：弹出可用标签下拉，选中后添加到所有选中便签。</summary>
    private void BatchTagButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("NoteContextMenuStyle") };
        var itemStyle = (Style)FindResource("NoteMenuItemStyle");

        var available = _tagRepository.GetAll();
        if (available.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "暂无可用标签", IsEnabled = false, Style = itemStyle });
        }
        else
        {
            foreach (var tag in available)
            {
                var item = new MenuItem { Header = tag.Name, Icon = MakeTagSwatch(tag.Color), Style = itemStyle };
                item.Click += (_, _) => _viewModel.BatchAddTagCommand.Execute(tag.Id);
                menu.Items.Add(item);
            }
        }

        menu.PlacementTarget = sender as UIElement;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>点击排序按钮：弹出排序下拉菜单（菜单项绑定 ChangeSortCommand，勾选标记当前排序方式）。</summary>
    private void SortButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu })
        {
            menu.PlacementTarget = (UIElement)sender;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    /// <summary>从点击源向上查找 NoteItemViewModel。</summary>
    private static NoteItemViewModel? GetNoteItem(object source)
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: NoteItemViewModel item })
            {
                return item;
            }
            // 🔴 事件源可能是 Run（摘要高亮），必须走 GetParentSafe 而非 VisualTreeHelper.GetParent
            current = TreeWalker.GetParentSafe(current);
        }
        return null;
    }

    /// <summary>
    /// 打开筛选菜单。菜单项在打开时按当前数据构建，而不是在 XAML 里写死：
    /// 标签是动态的，且需要正确标记「当前选中项」并复用项目统一的菜单样式。
    /// </summary>
    private void FilterButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || DataContext is not NoteListViewModel vm)
        {
            return;
        }

        var menu = new ContextMenu { Style = (Style)FindResource("NoteContextMenuStyle") };

        menu.Items.Add(BuildFilterGroup("颜色", vm.ColorFilterOptions, vm.ColorFilter,
            option => vm.ColorFilter = option));
        menu.Items.Add(BuildFilterGroup("标签", vm.TagFilterOptions, vm.TagFilter,
            option => vm.TagFilter = option));
        menu.Items.Add(BuildFilterGroup("时间", vm.RangeFilterOptions, vm.RangeFilter,
            option => vm.RangeFilter = option));
        menu.Items.Add(new Separator());

        var clear = new MenuItem
        {
            Header = "清除筛选",
            Style = (Style)FindResource("NoteMenuItemStyle"),
            IsEnabled = vm.HasActiveFilter,
        };
        clear.Click += (_, _) => vm.ClearFiltersCommand.Execute(null);
        menu.Items.Add(clear);

        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>构建一组筛选菜单项，用勾选标记当前选中项。</summary>
    private MenuItem BuildFilterGroup<T>(
        string header,
        IEnumerable<FilterOption<T>> options,
        FilterOption<T>? current,
        Action<FilterOption<T>> apply)
    {
        var group = new MenuItem
        {
            Header = header,
            Style = (Style)FindResource("NoteMenuItemStyle"),
        };

        foreach (var option in options)
        {
            var item = new MenuItem
            {
                Header = option.Name,
                IsCheckable = true,
                IsChecked = ReferenceEquals(option, current),
                Style = (Style)FindResource("NoteMenuItemStyle"),
                Tag = option,
            };

            var captured = option;
            item.Click += (_, _) => apply(captured);
            group.Items.Add(item);
        }

        return group;
    }

    /// <summary>判断点击源是否在按钮内（按钮点击不触发双击打开）。</summary>
    private static bool IsInsideButton(object source)
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is Button)
            {
                return true;
            }
            current = TreeWalker.GetParentSafe(current);
        }
        return false;
    }

    /// <summary>从应用资源构建 14×14 描边图标。</summary>
    private static Path MakeIcon(object geometryKey) => new Path
    {
        Data = (Geometry)Application.Current.FindResource(geometryKey),
        Width = 14, Height = 14, Stretch = Stretch.Uniform,
        Stroke = (Brush)Application.Current.FindResource("TextSecondary"),
        StrokeThickness = 1.2,
        Fill = Brushes.Transparent,
    };

    /// <summary>尝试为窗口应用 Mica 材质（仅 Windows 11 22H2+，即 Build 22621 及以上）。成功返回 true。</summary>
    private bool TryApplyMica()
    {
        try
        {
            if (Environment.OSVersion.Version.Build < 22621)
            {
                LogHelper.Debug("Mica 不支持，使用纯色背景");
                return false;
            }

            // TODO: 后续可恢复 Mica。当前 Mica 与透明背景（alpha=0）组合会导致中间列表区域渲染成黑色，
            // 故暂时放弃 Mica，窗口直接使用 XAML 中声明的纯色 #F3F0EA 背景。
            // var hwnd = new WindowInteropHelper(this).Handle;
            // if (hwnd == IntPtr.Zero) return false;
            // var backdrop = WindowNativeMethods.DWMSBT_MAINWINDOW;
            // var hr = WindowNativeMethods.DwmSetWindowAttribute(
            //     hwnd,
            //     WindowNativeMethods.DWMWA_SYSTEMBACKDROP_TYPE,
            //     ref backdrop,
            //     sizeof(int));
            // if (hr != 0)
            // {
            //     LogHelper.Debug($"Mica 应用失败（HRESULT 0x{hr:X8}），使用纯色背景");
            //     return false;
            // }
            // LogHelper.Debug("Mica 材质已应用");
            // return true;

            return false;
        }
        catch (Exception ex)
        {
            LogHelper.Debug($"Mica 应用异常，使用纯色背景：{ex.Message}");
            return false;
        }
    }

    /// <summary>主页待办按钮点击：切换当前聚焦行的待办状态。</summary>
    private void PreviewTodoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.CurrentNote is null) return;

        // 获取当前聚焦的 TextBox
        var focused = Keyboard.FocusedElement as TextBox;
        NoteLineViewModel? line = null;
        if (focused is not null && focused.DataContext is NoteLineViewModel focusedLine)
        {
            line = focusedLine;
        }
        else
        {
            // 如果没有聚焦的行，使用第一行
            if (_viewModel.CurrentNote.Lines.Count > 0)
            {
                line = _viewModel.CurrentNote.Lines[0];
            }
        }
        if (line is null) return;

        // 切换待办状态
        if (line.LineType == NoteLineType.Todo)
        {
            line.LineType = NoteLineType.Normal;
        }
        else
        {
            line.LineType = NoteLineType.Todo;
            line.IsChecked = false;
        }

        // 通知内容变化（保存）
        _viewModel.CurrentNote.NotifyLineContentChanged();

        // 重新聚焦到当前行的 TextBox
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            var textBox = FindVisualChildInItem<TextBox>(PreviewContentItemsControl, line);
            textBox?.Focus();
        }));
    }

    /// <summary>主页行编辑事件转发：全部逻辑在共享的 NoteLineEditor（与独立便签窗口同一实现，保证行为一致）。</summary>
    private void PreviewLineTextBox_KeyDown(object sender, KeyEventArgs e) => _lineEditor.HandleLineKeyDown(sender, e);

    private void PreviewLineTextBox_TextInput(object sender, TextCompositionEventArgs e) => _lineEditor.HandleLineTextInput(sender, e);

    private void PreviewLineTextBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _lineEditor.HandleLineMouseDown(sender, e);

    private void PreviewLineTextBox_MouseMove(object sender, MouseEventArgs e) => _lineEditor.HandleEditorMouseMove(sender, e);

    private void PreviewLineTextBox_MouseUp(object sender, MouseButtonEventArgs e) => _lineEditor.HandleEditorMouseUp(sender, e);

    private void PreviewLineTextBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
        {
            _lineEditor.AttachTextBox(box);
        }
    }

    private void PreviewContent_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _lineEditor.HandleBackgroundMouseDown(sender, e);

    private void PreviewContent_PreviewMouseMove(object sender, MouseEventArgs e) => _lineEditor.HandleEditorMouseMove(sender, e);

    private void PreviewContent_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _lineEditor.HandleEditorMouseUp(sender, e);

    // ===== NoteLineEditor.IHost 适配 =====

    ObservableCollection<NoteLineViewModel> NoteLineEditor.IHost.Lines => _viewModel.CurrentNote?.Lines ?? EmptyLines;

    int NoteLineEditor.IHost.IndexOf(NoteLineViewModel line) => _viewModel.CurrentNote?.GetLineIndex(line) ?? -1;

    void NoteLineEditor.IHost.InsertLineAfter(int index, string text) => _viewModel.CurrentNote?.InsertLineAfter(index, text);

    void NoteLineEditor.IHost.InsertLinesAfter(int index, IReadOnlyList<string> rawLines) => _viewModel.CurrentNote?.InsertLinesAfter(index, rawLines);

    void NoteLineEditor.IHost.RemoveLine(int index) => _viewModel.CurrentNote?.RemoveLine(index);

    int NoteLineEditor.IHost.MergeLineWithPrevious(int index) => _viewModel.CurrentNote?.MergeLineWithPrevious(index) ?? -1;

    TextBox? NoteLineEditor.IHost.FindTextBox(NoteLineViewModel line) => FindVisualChildInItem<TextBox>(PreviewContentItemsControl, line);

    void NoteLineEditor.IHost.FocusLineByIndex(int index, int caret) => FocusPreviewLine(index, caret);

    bool NoteLineEditor.IHost.IsReadOnly => _viewModel.CurrentNote?.IsLocked ?? true;

    bool NoteLineEditor.IHost.CanUndo => _viewModel.CurrentNote?.CanUndo ?? false;

    bool NoteLineEditor.IHost.CanRedo => _viewModel.CurrentNote?.CanRedo ?? false;

    void NoteLineEditor.IHost.Undo() => _viewModel.CurrentNote?.Undo();

    void NoteLineEditor.IHost.Redo() => _viewModel.CurrentNote?.Redo();

    /// <summary>聚焦编辑区指定行并设置光标（容器未生成时延迟重试，行集合刚重建后也能正确定位）。</summary>
    private void FocusPreviewLine(int index, int caret)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            var lines = _viewModel.CurrentNote?.Lines;
            if (lines is null || index < 0 || index >= lines.Count) return;
            var box = FindVisualChildInItem<TextBox>(PreviewContentItemsControl, lines[index]);
            if (box is null)
            {
                // 容器尚未生成：再等一拍
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    lines = _viewModel.CurrentNote?.Lines;
                    if (lines is null || index < 0 || index >= lines.Count) return;
                    var retry = FindVisualChildInItem<TextBox>(PreviewContentItemsControl, lines[index]);
                    if (retry is not null)
                    {
                        retry.Focus();
                        retry.CaretIndex = Math.Min(caret, retry.Text?.Length ?? 0);
                    }
                }));
                return;
            }
            box.Focus();
            box.CaretIndex = Math.Min(caret, box.Text?.Length ?? 0);
        }));
    }

    /// <summary>在 ItemsControl 中查找指定 DataContext 对应的可视化子元素。</summary>
    private static T? FindVisualChildInItem<T>(ItemsControl itemsControl, object dataContext) where T : DependencyObject
    {
        for (int i = 0; i < itemsControl.Items.Count; i++)
        {
            var container = itemsControl.ItemContainerGenerator.ContainerFromIndex(i) as ContentPresenter;
            if (container is null) continue;
            if (container.DataContext != dataContext) continue;
            return FindVisualChild<T>(container);
        }
        return null;
    }

    /// <summary>在视觉树中查找指定类型的第一个子元素。</summary>
    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result) return result;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    /// <summary>最小化按钮点击。</summary>
    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    /// <summary>最大化/还原按钮点击。</summary>
    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    /// <summary>关闭按钮点击。</summary>
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
