using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

using NoteApp.Helpers;
using NoteApp.Models.Enums;

namespace NoteApp.Views;

/// <summary>
/// <see cref="NoteWindow"/> 的菜单构建部分（partial）。
///
/// 单独拆出来而不是留在 <c>NoteWindow.xaml.cs</c> 里，原因：
/// 菜单是**纯构建代码**（声明 UI 元素、订阅 Click 转发到 VM 命令），
/// 与窗口的拖拽/缩放/贴边/形态那套「有状态交互」是完全不同的两类逻辑。
/// 混在一起时，1200 行的窗口文件里有一半在读起来像 XAML 的代码，
/// 想找「窗口怎么拖的」得先翻过三百行菜单。
///
/// **约定**：本文件只做「把 VM 命令接到菜单项上」，不写业务判断。
/// 菜单每次打开时重建（见 <see cref="BuildContextMenu"/> 调用点），
/// 因此这里的 <c>IsChecked</c> 读的是**当下**的 VM 状态，天然同步。
/// </summary>
public partial class NoteWindow
{
    /// <summary>构建标题栏「更多」菜单（每次点击重建，以同步勾选/颜色/透明度状态）；菜单动作全部转发到 VM 命令。</summary>
    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu { Style = (Style)FindResource("NoteContextMenuStyle") };
        var itemStyle = (Style)FindResource("NoteMenuItemStyle");
        var separatorStyle = (Style)FindResource("NoteMenuSeparatorStyle");

        // 新建便签
        var newItem = new MenuItem { Header = "新建便签", Icon = MakeIcon("IconPlus"), Style = itemStyle };
        newItem.Click += (_, _) => _viewModel.NewNoteCommand.Execute(null);

        // 置顶（同步勾选）
        var pinItem = new MenuItem
        {
            Header = "置顶", Icon = MakeIcon("IconPin"), Style = itemStyle,
            IsCheckable = true, IsChecked = _viewModel.IsPinned,
        };
        pinItem.Click += (_, _) => _viewModel.TogglePinCommand.Execute(null);

        // 设置提醒
        var reminderItem = new MenuItem { Header = "设置提醒", Icon = MakeIcon("IconBell"), Style = itemStyle };
        reminderItem.Click += (_, _) => OpenReminderPicker();

        // 标签（子菜单）
        var tagItem = new MenuItem { Header = "标签", Icon = MakeIcon("IconTag"), Style = itemStyle };
        BuildTagItems(tagItem, itemStyle, separatorStyle);

        // 添加图片（多选文件；图片存 BLOB，会随备份一起走，不会出现"备份成功但图片丢了"）
        var imageItem = new MenuItem { Header = "添加图片…", Icon = MakeIcon("IconImage"), Style = itemStyle };
        imageItem.Click += (_, _) => AddImagesFromDialog();

        // 折叠
        var collapseItem = new MenuItem { Header = "折叠", Icon = MakeIcon("IconMinimize"), Style = itemStyle };
        collapseItem.Click += (_, _) => _viewModel.ToggleCollapseCommand.Execute(null);

        // 极简模式（同步勾选）
        var minimalItem = new MenuItem
        {
            Header = "极简模式", Icon = MakeIcon("IconMonitor"), Style = itemStyle,
            IsCheckable = true, IsChecked = _viewModel.IsMinimal,
        };
        minimalItem.Click += (_, _) => _viewModel.ToggleMinimalCommand.Execute(null);

        // 贴边自动隐藏（同步勾选）：开启后拖到屏幕左右边缘松手会收成 5px 色条，鼠标靠近滑出
        var dockItem = new MenuItem
        {
            Header = "贴边自动隐藏", Icon = MakeIcon("IconDock"), Style = itemStyle,
            IsCheckable = true, IsChecked = _viewModel.IsDockEnabled,
        };
        dockItem.Click += (_, _) => _viewModel.ToggleDockCommand.Execute(null);

        // 卷角（同步勾选）：右下角显示一个翻折的纸角，纯外观
        var dogEarItem = new MenuItem
        {
            Header = "卷角", Icon = MakeIcon("IconDogEar"), Style = itemStyle,
            IsCheckable = true, IsChecked = _viewModel.IsDogEar,
        };
        dogEarItem.Click += (_, _) => _viewModel.ToggleDogEarCommand.Execute(null);

        // 颜色子菜单
        var colorItem = new MenuItem { Header = "颜色", Style = itemStyle };
        AddColorItems(colorItem, itemStyle);

        // 透明度子菜单
        var opacityItem = new MenuItem { Header = "透明度", Style = itemStyle };
        AddOpacityItems(opacityItem, itemStyle);

        // 删除
        var deleteItem = new MenuItem { Header = "删除", Icon = MakeIcon("IconTrash"), Style = itemStyle };
        deleteItem.Click += (_, _) => _viewModel.DeleteCommand.Execute(null);

        // 永久删除
        var permanentItem = new MenuItem { Header = "永久删除", Icon = MakeIcon("IconArchive"), Style = itemStyle };
        permanentItem.Click += (_, _) => _viewModel.PermanentDeleteCommand.Execute(null);

        menu.Items.Add(newItem);
        menu.Items.Add(pinItem);
        menu.Items.Add(reminderItem);
        menu.Items.Add(tagItem);
        menu.Items.Add(imageItem);
        menu.Items.Add(collapseItem);
        menu.Items.Add(minimalItem);
        menu.Items.Add(dockItem);
        menu.Items.Add(dogEarItem);
        menu.Items.Add(colorItem);
        menu.Items.Add(opacityItem);
        menu.Items.Add(new Separator { Style = separatorStyle });
        menu.Items.Add(deleteItem);
        menu.Items.Add(permanentItem);

        return menu;
    }

    /// <summary>内容区右键菜单：跨行感知的复制/粘贴/剪切/全选 + 便签完整操作菜单。</summary>
    private ContextMenu BuildContentContextMenu()
    {
        // 复用标题栏菜单（含全部便签操作），只在顶部插一段剪贴板项。
        var menu = BuildContextMenu();
        var itemStyle = (Style)FindResource("NoteMenuItemStyle");
        var separatorStyle = (Style)FindResource("NoteMenuSeparatorStyle");

        var cut = new MenuItem { Header = "剪切", Style = itemStyle };
        cut.Click += (_, _) => _lineEditor.CutFocused();
        var copy = new MenuItem { Header = "复制", Style = itemStyle };
        copy.Click += (_, _) => _lineEditor.CopySelection();
        var paste = new MenuItem { Header = "粘贴", Style = itemStyle };
        paste.Click += (_, _) => _lineEditor.PasteFocused();
        var selectAll = new MenuItem { Header = "全选", Style = itemStyle };
        selectAll.Click += (_, _) => _lineEditor.SelectAll();

        menu.Items.Insert(0, new Separator { Style = separatorStyle });
        menu.Items.Insert(0, selectAll);
        menu.Items.Insert(0, paste);
        menu.Items.Insert(0, copy);
        menu.Items.Insert(0, cut);

        return menu;
    }

    /// <summary>颜色子菜单：12 色（6 色相 × 深浅），当前色勾选。</summary>
    private void AddColorItems(MenuItem parent, Style itemStyle)
    {
        foreach (var color in NoteColorHelper.All)
        {
            var item = new MenuItem
            {
                Header = NoteColorHelper.GetDisplayName(color),
                Icon = MakeColorSwatch(color),
                Style = itemStyle,
                IsCheckable = true,
                IsChecked = _viewModel.Color == color,
            };
            item.Click += (_, _) => _viewModel.SetColorCommand.Execute(color);
            parent.Items.Add(item);
        }
    }

    /// <summary>透明度子菜单：顶部当前百分比 + 滑块 + 快捷预设。</summary>
    private void AddOpacityItems(MenuItem parent, Style itemStyle)
    {
        var separatorStyle = (Style)FindResource("NoteMenuSeparatorStyle");

        var labelItem = new MenuItem
        {
            Header = $"{_viewModel.Opacity}%",
            IsEnabled = false,
            Style = itemStyle,
        };

        var slider = new Slider
        {
            Minimum = 30, Maximum = 100,
            TickFrequency = 10, IsSnapToTickEnabled = true,
            Value = _viewModel.Opacity,
            Width = 160,
            Margin = new Thickness(0, 2, 0, 0),
        };

        var sliderHost = new StackPanel { Width = 200 };
        sliderHost.Children.Add(new TextBlock
        {
            Text = $"不透明度 {_viewModel.Opacity}%",
            FontSize = 10.5,
            Foreground = (Brush)FindResource("TextSecondary"),
        });
        sliderHost.Children.Add(slider);

        var sliderItem = new MenuItem
        {
            Header = sliderHost,
            Style = itemStyle,
            StaysOpenOnClick = true,
        };

        slider.ValueChanged += (_, _) =>
        {
            var value = (int)Math.Round(slider.Value);
            _viewModel.SetOpacityLive(value);
            labelItem.Header = $"{value}%";
        };
        slider.AddHandler(
            Thumb.DragCompletedEvent,
            new DragCompletedEventHandler((_, _) => _viewModel.PersistOpacity()),
            handledEventsToo: true);

        parent.Items.Add(labelItem);
        parent.Items.Add(sliderItem);
        parent.Items.Add(new Separator { Style = separatorStyle });

        foreach (var percent in new[] { 30, 50, 75, 100 })
        {
            var preset = new MenuItem { Header = $"{percent}%", Style = itemStyle };
            preset.Click += (_, _) => _viewModel.SetOpacityCommand.Execute(percent);
            parent.Items.Add(preset);
        }
    }

    /// <summary>标签子菜单：列出所有标签并勾选当前便签已含标签；无标签时显示灰显占位；底部「管理标签...」占位。</summary>
    private void BuildTagItems(MenuItem parent, Style itemStyle, Style separatorStyle)
    {
        // 每次打开菜单刷新标签列表（新建标签后即时可见）
        _viewModel.ReloadAllTags();

        if (_viewModel.AllTags.Count == 0)
        {
            parent.Items.Add(new MenuItem { Header = "暂无标签", IsEnabled = false, Style = itemStyle });
            return;
        }

        foreach (var tag in _viewModel.AllTags)
        {
            var item = new MenuItem
            {
                Header = tag.Name,
                Icon = MakeColorSwatch(tag.Color),
                Style = itemStyle,
                IsCheckable = true,
                IsChecked = _viewModel.HasTag(tag.Id),
                StaysOpenOnClick = true,
            };
            item.Click += (_, _) =>
            {
                _viewModel.ToggleTagCommand.Execute(tag.Id);
                item.IsChecked = _viewModel.HasTag(tag.Id);
            };
            parent.Items.Add(item);
        }

        parent.Items.Add(new Separator { Style = separatorStyle });
        parent.Items.Add(new MenuItem { Header = "管理标签...", IsEnabled = false, Style = itemStyle });
    }

    /// <summary>从应用资源构建 14×14 描边图标。</summary>
    private static Path MakeIcon(object geometryKey) => new()
    {
        Data = (Geometry)Application.Current.FindResource(geometryKey),
        Width = 14, Height = 14, Stretch = Stretch.Uniform,
        Stroke = (Brush)Application.Current.FindResource("TextSecondary"),
        StrokeThickness = 1.2,
        Fill = Brushes.Transparent,
    };

    /// <summary>颜色色板（14×14 圆角方块）。</summary>
    private static Border MakeColorSwatch(NoteColor color) => new()
    {
        Width = 14, Height = 14, CornerRadius = new CornerRadius(3),
        Background = (Brush)Application.Current.FindResource($"Note{color}_Bg"),
        BorderBrush = (Brush)Application.Current.FindResource($"Note{color}_Border"),
        BorderThickness = new Thickness(1),
    };
}
