using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;

using NoteApp.Helpers;
using NoteApp.Models.Enums;
using NoteApp.Services;
using NoteApp.ViewModels;

namespace NoteApp.Views;

/// <summary>
/// 设置窗口：自绘标题栏（WindowChrome）+ 左侧导航 + 右侧内容。
/// 业务逻辑全部在 SettingsViewModel，此处仅负责依赖注入、标题栏按钮、
/// 快捷键录制窗口级按键捕获与关闭清理。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        PreviewKeyDown += OnPreviewKeyDown;
        Closed += OnClosed;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>窗口关闭时释放 ViewModel 的事件订阅。</summary>
    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.Cleanup();
    }

    /// <summary>快捷键录制：录制状态下捕获修饰键 + 主键，组合完成后交给 ViewModel 应用。</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var hotkey = _viewModel.HotkeySettings;
        if (hotkey.RecordingHotkey is null)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            hotkey.CancelRecording();
            e.Handled = true;
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (HotkeyHelper.IsModifierKey(key))
        {
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.None)
        {
            return;
        }

        hotkey.ApplyHotkey(hotkey.RecordingHotkey, HotkeyHelper.FormatStorage(Keyboard.Modifiers, key));
        e.Handled = true;
    }

    /// <summary>关于页「MIT License」链接点击：用系统默认浏览器打开。</summary>
    private void OnLicenseRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
