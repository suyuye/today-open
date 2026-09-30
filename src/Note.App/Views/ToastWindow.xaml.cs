using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace NoteApp.Views;

/// <summary>轻量通知 Toast：右下角、5 秒自动消失、淡入淡出、可选动作按钮。</summary>
public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly Action? _onAction;

    private ToastWindow(string message, string? actionText, Action? onAction)
    {
        InitializeComponent();

        _onAction = onAction;
        MessageText.Text = message;

        if (string.IsNullOrEmpty(actionText))
        {
            ActionButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            ActionButton.Content = actionText;
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => FadeOutAndClose();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// 实际显示 Toast 的动作，默认真弹窗。
    /// 测试里替换为记录器，避免在无消息循环的测试线程上创建窗口。
    /// </summary>
    public static Action<string, string?, Action?> Displayer { get; set; } = ShowCore;

    /// <summary>显示一条 Toast 通知（动作按钮可选）。</summary>
    public static void Show(string message, string? actionText = null, Action? onAction = null) =>
        Displayer(message, actionText, onAction);

    /// <summary>恢复默认显示实现（供测试清理使用）。</summary>
    public static void ResetDisplayer() => Displayer = ShowCore;

    private static void ShowCore(string message, string? actionText, Action? onAction) =>
        new ToastWindow(message, actionText, onAction).Show();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - ActualWidth - 16;
        Top = workArea.Bottom - ActualHeight - 16;

        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
        _timer.Start();
    }

    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        _onAction?.Invoke();
        FadeOutAndClose();
    }

    private void FadeOutAndClose()
    {
        _timer.Stop();
        var animation = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
        animation.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, animation);
    }
}
