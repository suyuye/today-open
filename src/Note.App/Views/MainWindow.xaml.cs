using System.Windows;

using NoteApp.Helpers;
using NoteApp.Models.Enums;
using NoteApp.Services;

namespace NoteApp.Views;

/// <summary>资源字典测试窗口（T0-5 临时窗口，后续任务会替换为真正的列表面板）。</summary>
public partial class MainWindow : Window
{
    private int _testIndex;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void OpenTestNote_Click(object sender, RoutedEventArgs e)
    {
        var colorName = ((FrameworkElement)sender).Tag?.ToString() ?? "Yellow";
        var color = Enum.Parse<NoteColor>(colorName, ignoreCase: true);

        var noteService = ServiceLocator.GetRequiredService<INoteService>();
        var note = noteService.CreateNote(
            $"测试便签 - {color}",
            "这是一条测试便签内容。\n用于验证颜色渲染与标题栏、内容区的颜色差异。",
            color);

        note.PositionX = 100 + (_testIndex % 5) * 30;
        note.PositionY = 100 + (_testIndex % 5) * 30;
        noteService.UpdateNote(note);
        _testIndex++;

        new NoteWindow(note.Id).Show();
    }
}
