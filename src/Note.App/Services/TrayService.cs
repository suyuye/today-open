using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

using CommunityToolkit.Mvvm.Input;

using H.NotifyIcon;
using H.NotifyIcon.Core;

using NoteApp.Helpers;
using NoteApp.Models.Enums;

namespace NoteApp.Services;

/// <summary>系统托盘服务：创建托盘图标、右键菜单、左键切换、双击打开列表与气泡通知。</summary>
public class TrayService : ITrayService
{
    private readonly IWindowService _windowService;

    private TaskbarIcon? _trayIcon;
    private bool _isInitialized;
    private Action? _notificationClick;

    public TrayService(IWindowService windowService)
    {
        _windowService = windowService;
    }

    public bool IsVisible => _trayIcon is not null && _trayIcon.Visibility == Visibility.Visible;

    public void Initialize()
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;

        _trayIcon = new TaskbarIcon
        {
            Icon = LoadAppIcon(),
            ToolTipText = "今日营业中",
            ContextMenu = BuildContextMenu(),
            MenuActivation = PopupActivationMode.RightClick,
            LeftClickCommand = new RelayCommand(ToggleAllNotes),
            DoubleClickCommand = new RelayCommand(() => OpenListRequested?.Invoke(this, EventArgs.Empty)),
        };

        _trayIcon.TrayBalloonTipClicked += OnTrayBalloonTipClicked;
        _trayIcon.ForceCreate();

        LogHelper.Info("系统托盘初始化完成");
    }

    public void Show()
    {
        if (_trayIcon is null)
        {
            return;
        }

        _trayIcon.Visibility = Visibility.Visible;
    }

    public void Hide()
    {
        if (_trayIcon is null)
        {
            return;
        }

        _trayIcon.Visibility = Visibility.Collapsed;
    }

    public void Remove()
    {
        if (_trayIcon is null)
        {
            return;
        }

        _trayIcon.TrayBalloonTipClicked -= OnTrayBalloonTipClicked;
        _trayIcon.Dispose();
        _trayIcon = null;
        _isInitialized = false;
    }

    public void SetTooltip(string text)
    {
        if (_trayIcon is null)
        {
            return;
        }

        _trayIcon.ToolTipText = text;
    }

    public void SetIcon(NoteColor color)
    {
        if (_trayIcon is null)
        {
            return;
        }

        _trayIcon.Icon = LoadAppIcon();
    }

    public void ShowNotification(string title, string message) => ShowNotification(title, message, null);

    public void ShowNotification(string title, string message, Action? onClick)
    {
        if (_trayIcon is null)
        {
            return;
        }

        _notificationClick = onClick;
        _trayIcon.ShowNotification(title, message, NotificationIcon.Info);
    }

    public event EventHandler? NewNoteRequested;
    public event EventHandler? ShowAllRequested;
    public event EventHandler? HideAllRequested;
    public event EventHandler? OpenListRequested;
    public event EventHandler? OpenSettingsRequested;
    public event EventHandler? ExitRequested;

    private void ToggleAllNotes() => _windowService.ToggleAllNoteWindows();

    private void OnTrayBalloonTipClicked(object sender, RoutedEventArgs e)
    {
        var handler = _notificationClick;
        _notificationClick = null;
        handler?.Invoke();
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu { Style = (Style)Application.Current.FindResource("NoteContextMenuStyle") };
        var itemStyle = (Style)Application.Current.FindResource("NoteMenuItemStyle");
        var separatorStyle = (Style)Application.Current.FindResource("NoteMenuSeparatorStyle");

        menu.Items.Add(CreateMenuItem("新建便签", "IconPlus", () => NewNoteRequested?.Invoke(this, EventArgs.Empty), itemStyle));
        menu.Items.Add(new Separator { Style = separatorStyle });
        menu.Items.Add(CreateMenuItem("显示全部便签", "IconExpand", () => ShowAllRequested?.Invoke(this, EventArgs.Empty), itemStyle));
        menu.Items.Add(CreateMenuItem("隐藏全部便签", "IconCollapse", () => HideAllRequested?.Invoke(this, EventArgs.Empty), itemStyle));
        menu.Items.Add(new Separator { Style = separatorStyle });
        menu.Items.Add(CreateMenuItem("打开便签列表", "IconMore", () => OpenListRequested?.Invoke(this, EventArgs.Empty), itemStyle));
        menu.Items.Add(CreateMenuItem("设置", "IconSettings", () => OpenSettingsRequested?.Invoke(this, EventArgs.Empty), itemStyle));
        menu.Items.Add(new Separator { Style = separatorStyle });
        menu.Items.Add(CreateMenuItem("退出", "IconClose", () => ExitRequested?.Invoke(this, EventArgs.Empty), itemStyle));

        return menu;
    }

    private static MenuItem CreateMenuItem(string header, string? iconKey, Action action, Style style)
    {
        var item = new MenuItem { Header = header, Style = style };
        if (iconKey is not null)
        {
            item.Icon = MakeIcon(iconKey);
        }

        item.Click += (_, _) => action();
        return item;
    }

    private static Path MakeIcon(string geometryKey) => new Path
    {
        Data = (Geometry)Application.Current.FindResource(geometryKey),
        Width = 14,
        Height = 14,
        Stretch = Stretch.Uniform,
        Stroke = (Brush)Application.Current.FindResource("TextSecondary"),
        StrokeThickness = 1.2,
        Fill = Brushes.Transparent,
    };

    /// <summary>从程序集资源加载应用图标（Assets/app.ico），作为托盘图标。</summary>
    private static System.Drawing.Icon LoadAppIcon()
    {
        var streamInfo = Application.GetResourceStream(new Uri("pack://application:,,,/Note.App;component/Assets/app.ico"));
        if (streamInfo is null)
        {
            throw new InvalidOperationException("应用图标资源未找到：Assets/app.ico");
        }

        using var stream = streamInfo.Stream;
        return new System.Drawing.Icon(stream);
    }
}
