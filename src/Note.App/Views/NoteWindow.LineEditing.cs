using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using NoteApp.Helpers;
using NoteApp.ViewModels;

namespace NoteApp.Views;

/// <summary>
/// <see cref="NoteWindow"/> 的行编辑接线部分（partial）。
///
/// 这里**几乎没有逻辑** —— 全部是「把 XAML 事件转发给共享的 <see cref="NoteLineEditor"/>」
/// 以及「实现 <see cref="NoteLineEditor.IHost"/> 让编辑器能回调窗口」。
///
/// 为什么值得单独一个文件：真正实现行编辑的 <see cref="NoteLineEditor"/> 是
/// 便签窗口与主页编辑区**共用**的（保证两处行为一致），
/// 因此本文件是那段共享代码在便签窗口侧的**唯一接缝**。
/// 想理解「敲键盘之后发生了什么」，看这一层就知道该往哪追。
/// </summary>
public partial class NoteWindow
{
    /// <summary>行编辑事件转发：全部逻辑在共享的 NoteLineEditor（主页编辑区使用同一实现，保证行为一致）。</summary>
    private void LineTextBox_KeyDown(object sender, KeyEventArgs e) => _lineEditor.HandleLineKeyDown(sender, e);

    private void LineTextBox_TextInput(object sender, TextCompositionEventArgs e) => _lineEditor.HandleLineTextInput(sender, e);

    private void LineTextBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _lineEditor.HandleLineMouseDown(sender, e);

    private void LineTextBox_MouseMove(object sender, MouseEventArgs e) => _lineEditor.HandleEditorMouseMove(sender, e);

    private void LineTextBox_MouseUp(object sender, MouseButtonEventArgs e) => _lineEditor.HandleEditorMouseUp(sender, e);

    private void LineTextBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
        {
            _lineEditor.AttachTextBox(box);
        }
    }

    private void ContentBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _lineEditor.HandleBackgroundMouseDown(sender, e);

    private void ContentBox_PreviewMouseMove(object sender, MouseEventArgs e) => _lineEditor.HandleEditorMouseMove(sender, e);

    private void ContentBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _lineEditor.HandleEditorMouseUp(sender, e);

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

    /// <summary>判断 source 是否位于 ancestor 的视觉子树内。</summary>
    private static bool IsDescendantOf(DependencyObject ancestor, object? source)
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
            // 🔴 事件源可能是 Run（FrameworkContentElement，非 Visual），必须走 GetParentSafe
            current = TreeWalker.GetParentSafe(current);
        }
        return false;
    }

    // ===== NoteLineEditor.IHost 适配 =====

    ObservableCollection<NoteLineViewModel> NoteLineEditor.IHost.Lines => _viewModel.Lines;

    int NoteLineEditor.IHost.IndexOf(NoteLineViewModel line) => _viewModel.GetLineIndex(line);

    void NoteLineEditor.IHost.InsertLineAfter(int index, string text) => _viewModel.InsertLineAfter(index, text);

    void NoteLineEditor.IHost.InsertLinesAfter(int index, IReadOnlyList<string> rawLines) => _viewModel.InsertLinesAfter(index, rawLines);

    void NoteLineEditor.IHost.RemoveLine(int index) => _viewModel.RemoveLine(index);

    int NoteLineEditor.IHost.MergeLineWithPrevious(int index) => _viewModel.MergeLineWithPrevious(index);

    TextBox? NoteLineEditor.IHost.FindTextBox(NoteLineViewModel line) => FindVisualChildInItem<TextBox>(ContentItemsControl, line);

    void NoteLineEditor.IHost.FocusLineByIndex(int index, int caret) => FocusEditorLine(index, caret);

    bool NoteLineEditor.IHost.IsReadOnly => _viewModel.IsReadOnly;

    bool NoteLineEditor.IHost.CanUndo => _viewModel.CanUndo;

    bool NoteLineEditor.IHost.CanRedo => _viewModel.CanRedo;

    void NoteLineEditor.IHost.Undo() => _viewModel.Undo();

    void NoteLineEditor.IHost.Redo() => _viewModel.Redo();

    /// <summary>聚焦指定行并设置光标（容器未生成时延迟重试，行集合刚重建后也能正确定位）。</summary>
    private void FocusEditorLine(int index, int caret)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (index < 0 || index >= _viewModel.Lines.Count) return;
            var box = FindVisualChildInItem<TextBox>(ContentItemsControl, _viewModel.Lines[index]);
            if (box is null)
            {
                // 容器尚未生成：再等一拍
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    if (index < 0 || index >= _viewModel.Lines.Count) return;
                    var retry = FindVisualChildInItem<TextBox>(ContentItemsControl, _viewModel.Lines[index]);
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
}
