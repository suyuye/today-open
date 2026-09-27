using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

using NoteApp.Helpers;
using NoteApp.Models;
using NoteApp.Services;
using NoteApp.ViewModels;

namespace NoteApp.Views;

/// <summary>提醒通知弹窗：右下角滑入、10 秒自动滑出、不抢焦点，业务逻辑在 ReminderNotificationViewModel。</summary>
public partial class ReminderNotification : Window
{
    private readonly ReminderNotificationViewModel _viewModel;
    private readonly DispatcherTimer _timer;
    private bool _closing;

    public ReminderNotification(Reminder reminder, Note note)
    {
        InitializeComponent();

        var reminderService = ServiceLocator.GetRequiredService<IReminderService>();
        var windowService = ServiceLocator.GetRequiredService<IWindowService>();
        var settingsService = ServiceLocator.GetRequiredService<ISettingsService>();
        _viewModel = new ReminderNotificationViewModel(reminderService, windowService, settingsService, reminder, note);
        DataContext = _viewModel;
        _viewModel.CloseRequested += (_, _) => SlideOutAndClose();
        MarkDefaultSnooze();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _timer.Tick += (_, _) => SlideOutAndClose();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 右下角，距底部/右侧各 20px
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - ActualWidth - 20;
        Top = workArea.Bottom - ActualHeight - 20;

        SlideTransform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        _timer.Start();
    }

    private void SlideOutAndClose()
    {
        if (_closing)
        {
            return;
        }
        _closing = true;
        _timer.Stop();

        SlideTransform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(24, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            });

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>在默认间隔对应的菜单项后追加「（默认）」标记。</summary>
    private void MarkDefaultSnooze()
    {
        if (SnoozeButton.ContextMenu is not { } menu)
        {
            return;
        }

        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            if (item.Tag is string tag && int.TryParse(tag, out var minutes) && minutes == _viewModel.DefaultSnoozeMinutes)
            {
                item.Header = $"{item.Header}（默认）";
            }
        }
    }

    /// <summary>点击「稍后」：暂停自动关闭计时，向上弹出分钟数选择菜单。</summary>
    private void SnoozeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu })
        {
            return;
        }

        _timer.Stop();
        menu.Closed += OnSnoozeMenuClosed;
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    /// <summary>分钟数菜单关闭：若尚未开始滑出，则恢复自动关闭计时。</summary>
    private void OnSnoozeMenuClosed(object? sender, EventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            menu.Closed -= OnSnoozeMenuClosed;
        }

        if (!_closing)
        {
            _timer.Start();
        }
    }

    private void SnoozeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && int.TryParse(tag, out var minutes))
        {
            _viewModel.SnoozeCommand.Execute(minutes);
        }
    }

    /// <summary>「自定义...」：弹出输入弹窗，确定后按输入分钟数稍后提醒。</summary>
    private void CustomSnoozeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();

        var dialog = new SnoozeInputDialog(_viewModel.DefaultSnoozeMinutes) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Minutes is int minutes)
        {
            _viewModel.SnoozeCommand.Execute(minutes);
            return;
        }

        if (!_closing)
        {
            _timer.Start();
        }
    }
}
