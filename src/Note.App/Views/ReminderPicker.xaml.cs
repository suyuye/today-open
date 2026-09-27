using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using NoteApp.Helpers;
using NoteApp.Models;
using NoteApp.Services;
using NoteApp.ViewModels;

namespace NoteApp.Views;

/// <summary>提醒设置弹窗：无边框、可拖拽移动，业务逻辑在 ReminderPickerViewModel。</summary>
public partial class ReminderPicker : Window
{
    private readonly ReminderPickerViewModel _viewModel;

    public ReminderPicker(Guid noteId, Reminder? existingReminder)
    {
        InitializeComponent();

        var reminderService = ServiceLocator.GetRequiredService<IReminderService>();
        _viewModel = new ReminderPickerViewModel(reminderService, noteId, existingReminder);
        DataContext = _viewModel;
        _viewModel.CloseRequested += (_, _) => Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (IsInsideButton(e.OriginalSource)) return;

        DragMove();
    }

    /// <summary>判断点击源是否位于按钮内（关闭按钮），是则不触发拖拽。</summary>
    private static bool IsInsideButton(object source)
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is Button) return true;
            // 🔴 事件源可能是 Run（FrameworkContentElement，非 Visual），必须走 GetParentSafe
            current = TreeWalker.GetParentSafe(current);
        }
        return false;
    }
}
