using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

using NoteApp.Helpers;
using NoteApp.Models.Enums;
using NoteApp.Services;
using NoteApp.ViewModels;

namespace NoteApp.Views;

/// <summary>
/// <see cref="NoteWindow"/> 的「提醒 / 图片附件 / 待办快捷按钮」部分（partial）。
///
/// 这三件事放一起是因为它们都是**由标题栏按钮触发的单次动作**
/// （点按钮 → 弹窗/加附件/切待办状态），不涉及持续交互状态。
///
/// **提醒到期闪烁**（<see cref="StartFlash"/>）也在本文件：
/// 它虽然是被服务事件驱动的，但只作用于标题栏那一圈橙色边框，与提醒功能同属一个闭环。
/// </summary>
public partial class NoteWindow
{
    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = BuildContextMenu();
        menu.PlacementTarget = sender as UIElement;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ReminderButton_Click(object sender, RoutedEventArgs e) => OpenReminderPicker();

    /// <summary>打开提醒设置弹窗（模态，Owner 为本便签窗口，中心偏上 50px）；已有提醒则进入编辑模式。</summary>
    private void OpenReminderPicker()
    {
        var reminderService = ServiceLocator.GetRequiredService<IReminderService>();
        var existing = reminderService.GetRemindersForNote(_viewModel.Note.Id)
            .FirstOrDefault(r => !r.IsCompleted);

        var picker = new ReminderPicker(_viewModel.Note.Id, existing)
        {
            Owner = this,
        };
        PositionReminderPicker(picker);
        picker.ShowDialog();
    }

    /// <summary>「添加图片…」：多选图片文件加入当前便签，成功后缩略图条自动出现。</summary>
    private void AddImagesFromDialog()
    {
        // 锁定的便签不允许改内容，图片也算内容
        if (_viewModel.IsLocked)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择图片",
            Multiselect = true,
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|所有文件|*.*",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var added = _viewModel.Attachments.AddFromFiles(dialog.FileNames);
        if (added == 0)
        {
            ToastWindow.Show("没有添加任何图片");
        }
    }

    /// <summary>待办按钮点击：将当前聚焦行转换为待办事项（或取消待办）。</summary>
    private void TodoButton_Click(object sender, RoutedEventArgs e)
    {
        // 获取当前聚焦的 TextBox
        var focused = Keyboard.FocusedElement as TextBox;
        if (focused is null || focused.DataContext is not NoteLineViewModel line)
        {
            // 如果没有聚焦的行，聚焦到第一行
            FocusFirstTextBox();
            focused = Keyboard.FocusedElement as TextBox;
            if (focused is null || focused.DataContext is not NoteLineViewModel firstLine) return;
            line = firstLine;
        }

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
        _viewModel.NotifyLineContentChanged();

        // 重新聚焦到当前行的 TextBox
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            var textBox = FindVisualChildInItem<TextBox>(ContentItemsControl, line);
            textBox?.Focus();
        }));
    }

    /// <summary>计算弹窗位置：Owner 中心偏上 50px，并修正到屏幕工作区内。</summary>
    private void PositionReminderPicker(Window picker)
    {
        // 同样要用本窗口所在屏的工作区，否则副屏上的便签弹出选择器会被拽到主屏
        var workArea = MonitorHelper.WorkAreaForWindow(this);
        var left = Left + (ActualWidth - picker.Width) / 2;
        var top = Top + (ActualHeight - picker.Height) / 2 - 50;

        if (left < workArea.Left) left = workArea.Left;
        if (top < workArea.Top) top = workArea.Top;
        if (left + picker.Width > workArea.Right) left = workArea.Right - picker.Width;
        if (top + picker.Height > workArea.Bottom) top = workArea.Bottom - picker.Height;

        picker.Left = left;
        picker.Top = top;
    }

    /// <summary>提醒到期：若属于本便签，闪烁标题栏橙色边框。</summary>
    private void OnReminderTriggered(object? sender, ReminderEventArgs e)
    {
        if (e.Reminder.NoteId != _viewModel.Note.Id)
        {
            return;
        }

        StartFlash();
    }

    /// <summary>标题栏橙色边框闪烁 3 次（每次 500ms 显示 + 500ms 隐藏）。</summary>
    private void StartFlash()
    {
        FlashBorder.Visibility = Visibility.Visible;
        FlashBorder.Opacity = 1;

        var storyboard = new Storyboard();
        var animation = new DoubleAnimationUsingKeyFrames();
        Storyboard.SetTarget(animation, FlashBorder);
        Storyboard.SetTargetProperty(animation, new PropertyPath(UIElement.OpacityProperty));

        var time = TimeSpan.Zero;
        for (var i = 0; i < 3; i++)
        {
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(time)));
            time += TimeSpan.FromMilliseconds(500);
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(time)));
            time += TimeSpan.FromMilliseconds(500);
        }

        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            FlashBorder.Visibility = Visibility.Collapsed;
            FlashBorder.Opacity = 1;
        };
        storyboard.Begin();
    }
}
