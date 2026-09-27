using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using NoteApp.Models.Enums;
using NoteApp.ViewModels;

namespace NoteApp.Helpers;

/// <summary>
/// 便签行编辑器控制器：独立便签窗口与主页编辑区共用，保证两处编辑体验一致。
/// 鼠标选区完全接管：按下时捕获鼠标，行内与跨行（含跨段落、全选）选区由装饰器
/// <see cref="SelectionAdorner"/> 自绘高亮，不依赖 TextBox 非焦点选区渲染。
/// 另提供 Enter 换行、Backspace/Delete 合并行、跨行复制/剪切/删除、
/// 多行粘贴（待办语法还原复选框）、Ctrl+Z 撤销 / Ctrl+Y 重做。
/// </summary>
public sealed class NoteLineEditor
{
    /// <summary>宿主适配接口：由 View 实现，桥接 ViewModel 行操作与视觉树 TextBox 查找。</summary>
    public interface IHost
    {
        ObservableCollection<NoteLineViewModel> Lines { get; }
        int IndexOf(NoteLineViewModel line);
        void InsertLineAfter(int index, string text);
        void InsertLinesAfter(int index, IReadOnlyList<string> rawLines);
        void RemoveLine(int index);
        int MergeLineWithPrevious(int index);
        TextBox? FindTextBox(NoteLineViewModel line);
        void FocusLineByIndex(int index, int caret);
        bool IsReadOnly { get; }
        bool CanUndo { get; }
        bool CanRedo { get; }
        void Undo();
        void Redo();
    }

    /// <summary>
    /// 剪贴板里是图片时，Ctrl+V 会调用它；返回 true 表示宿主已处理（不再走文本粘贴）。
    /// 由宿主窗口在构造后挂上；为 null 时按普通文本粘贴处理。
    /// </summary>
    public Func<bool>? ImagePasteHandler { get; set; }

    private const double AutoScrollEdge = 24;
    private const double AutoScrollStep = 5;

    private readonly IHost _host;
    private readonly ItemsControl _itemsControl;
    private readonly ScrollViewer? _scrollViewer;

    // 拖选状态：锚点（按下位置）与焦点端（当前拖动位置）
    private (int Line, int Caret)? _anchor;
    private (int Line, int Caret)? _focus;
    private bool _isDragging;

    /// <summary>选区高亮装饰器（懒创建：需视觉树就绪后才有 AdornerLayer）。</summary>
    private SelectionAdorner? _adorner;

    /// <summary>拖选按住边缘时的持续滚动定时器。</summary>
    private DispatcherTimer? _autoScrollTimer;

    /// <summary>已挂接命令重载的 TextBox（Paste/Copy/Cut/SelectAll 经 PreviewExecuted 拦截）。</summary>
    private readonly HashSet<TextBox> _attachedBoxes = new();

    /// <summary>
    /// 行索引 → TextBox 缓存。行容器一旦生成，其行内 TextBox 实例在
    /// <see cref="InvalidateBoxCache"/> 之前不会变化，因此可直接命中，避免
    /// 每次调用都遍历 ItemsControl 的全部容器并做一次深度视觉树 DFS。
    /// </summary>
    private readonly Dictionary<NoteLineViewModel, TextBox> _boxCache = new();

    /// <summary>缓存的 IHost.Lines 引用；行集合被重建（Clear + 重新 Add）时会变成新实例。</summary>
    private ObservableCollection<NoteLineViewModel>? _cachedLines;

    /// <summary>逐行 TextBox 清单，索引与 <see cref="IHost.Lines"/> 对齐；由 <see cref="GetTargets"/> 维护。</summary>
    private readonly List<TextBox?> _targets = new();


    public NoteLineEditor(IHost host, ItemsControl itemsControl, ScrollViewer? scrollViewer)
    {
        _host = host;
        _itemsControl = itemsControl;
        _scrollViewer = scrollViewer;
    }

    /// <summary>
    /// 行集合变更的结构性跳变（重建 / 增删行）后必须调用：缓存里的 TextBox 已随容器卸载失效。
    /// 只清缓存，不触碰选区状态（拖选中途换行、粘贴多行不应丢失选区）。
    /// </summary>
    public void InvalidateBoxCache()
    {
        _boxCache.Clear();
        _cachedLines = null;
    }

    /// <summary>
    /// 取某行的 TextBox：优先查缓存，未命中才回落到宿主的视觉树查找。
    /// 这是选区拖拽路径上调用最密集的方法（每次鼠标移动都会对多行各调一次），
    /// 命中缓存后单次开销从「遍历 N 个容器 + 深度 DFS」降为一次字典查找。
    /// </summary>
    private TextBox? FindBox(NoteLineViewModel line)
    {
        if (_boxCache.TryGetValue(line, out var cached))
        {
            // 容器可能已被重建导致旧 TextBox 脱离视觉树，此时缓存不可信
            if (PresentationSource.FromVisual(cached) is not null) return cached;
            _boxCache.Remove(line);
        }

        var box = _host.FindTextBox(line);
        if (box is not null)
        {
            _boxCache[line] = box;
        }
        return box;
    }

    // ===== 外部调用入口 =====

    /// <summary>行 TextBox Loaded 时调用：挂接命令重载（多行粘贴、跨行复制/剪切/全选）。</summary>
    public void AttachTextBox(TextBox box)
    {
        if (!_attachedBoxes.Add(box)) return;
        CommandManager.AddPreviewExecutedHandler(box, OnLinePreviewExecuted);
    }

    /// <summary>行 TextBox 的 PreviewKeyDown：换行、合并行、撤销/重做、选区整体操作。</summary>
    public void HandleLineKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not NoteLineViewModel line) return;
        var index = _host.IndexOf(line);
        if (index < 0) return;

        var modifiers = Keyboard.Modifiers;
        var isCtrl = modifiers.HasFlag(ModifierKeys.Control);

        // Ctrl+Z 撤销 / Ctrl+Y、Ctrl+Shift+Z 重做（内容按行集合维护，原生单行撤销栈跨行不可靠，统一接管）
        if (isCtrl && e.Key is Key.Z or Key.Y)
        {
            e.Handled = true;
            if (_host.IsReadOnly) return;
            var redo = e.Key == Key.Y || modifiers.HasFlag(ModifierKeys.Shift);
            if (redo ? _host.CanRedo : _host.CanUndo)
            {
                ClearCrossSelection();
                if (redo)
                {
                    _host.Redo();
                }
                else
                {
                    _host.Undo();
                }

                // 行集合已重建，等容器重新生成后聚焦首行
                _host.FocusLineByIndex(0, 0);
            }
            return;
        }

        // Ctrl+C / Ctrl+X / Ctrl+A / Ctrl+V：直接在本层处理，不依赖命令路由。
        //
        // ⚠️ 这里必须拦截，不能只靠 PreviewExecuted 的命令重载。原因（已实测确认）：
        // TextBox 的 Copy/Cut 命令在 CanExecute 为 false 时，CommandManager
        // **根本不会派发 Executed**，于是 PreviewExecuted 拦截器永远不会被调用。
        // 而 CanExecute 只认 TextBox 自身的 SelectionLength —— 跨行选区是存在
        // _anchor/_focus 里的、由装饰器自绘的，各行 TextBox 的 SelectionLength 全是 0，
        // 所以按 Ctrl+C / Ctrl+X 时命令压根不派发，表现为「无反应」。
        // （Ctrl+A 之所以正常，是因为 SelectAll 不考虑选区，CanExecute 恒为 true。
        //   实测日志：Ctrl+A 有 PreviewExecuted 记录，Ctrl+C/Ctrl+X 完全没有。）
        if (isCtrl && !e.IsRepeat)
        {
            switch (e.Key)
            {
                case Key.C:
                {
                    e.Handled = true;
                    if (HasSelection())
                    {
                        CopySelection();
                    }
                    else if (box.SelectionLength > 0)
                    {
                        box.Copy();
                    }
                    return;
                }
                case Key.X:
                {
                    e.Handled = true;
                    if (_host.IsReadOnly) return;
                    if (HasSelection())
                    {
                        CopySelection();
                        DeleteSelection();
                    }
                    else if (box.SelectionLength > 0)
                    {
                        box.Cut();
                    }
                    return;
                }
                case Key.A:
                {
                    e.Handled = true;
                    SelectAll();
                    return;
                }
                case Key.V:
                {
                    e.Handled = true;
                    if (_host.IsReadOnly) return;

                    // 剪贴板里是图片时交给宿主处理（作为附件），不走文本粘贴。
                    // 这里只调用宿主挂上来的处理器、不直接碰附件服务：
                    // 编辑器不该知道便签之外的事情，而且这个编辑器被
                    // 「便签窗口」与「主页编辑区」两处共用 ——
                    // 挂一个处理器比扩展 INoteLineParent 接口更不容易漏改。
                    if (Clipboard.ContainsImage() && ImagePasteHandler?.Invoke() == true)
                    {
                        return;
                    }

                    PasteInto(box);
                    return;
                }
            }
        }

        // 选区整体操作
        if (HasSelection())
        {
            switch (e.Key)
            {
                case Key.Back or Key.Delete:
                    e.Handled = true;
                    if (!_host.IsReadOnly) DeleteSelection();
                    return;
                case Key.Enter:
                    e.Handled = true;
                    if (!_host.IsReadOnly) ReplaceSelectionWithNewline();
                    return;
                case Key.Escape:
                    e.Handled = true;
                    ClearCrossSelection();
                    return;
                case Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown:
                    // 光标移动键：清除选区，交由原生处理
                    ClearCrossSelection();
                    return;
            }
        }

        if (_host.IsReadOnly) return;

        // Enter：在光标位置换行，光标后的文字移动到新行
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            var caretIndex = box.CaretIndex;
            var currentText = box.Text ?? string.Empty;
            var beforeCursor = caretIndex < currentText.Length ? currentText[..caretIndex] : currentText;
            var afterCursor = caretIndex < currentText.Length ? currentText[caretIndex..] : string.Empty;

            line.Text = beforeCursor;
            _host.InsertLineAfter(index, afterCursor);
            _host.FocusLineByIndex(index + 1, 0);
        }
        // Backspace：光标在行首时合并到上一行（空行则删除，非空行则追加内容）
        else if (e.Key == Key.Back && box.CaretIndex == 0 && index > 0)
        {
            e.Handled = true;
            if (string.IsNullOrEmpty(box.Text))
            {
                // 当前行为空：删除当前行，聚焦上一行末尾
                _host.RemoveLine(index);
                _host.FocusLineByIndex(index - 1, _host.Lines[index - 1].Text?.Length ?? 0);
            }
            else
            {
                // 当前行不为空：内容追加到上一行，聚焦上一行原末尾位置
                var mergePosition = _host.MergeLineWithPrevious(index);
                _host.FocusLineByIndex(index - 1, Math.Max(mergePosition, 0));
            }
        }
        // Delete：光标在行尾时把下一行合并进来
        else if (e.Key == Key.Delete && box.CaretIndex >= (box.Text?.Length ?? 0) && index < _host.Lines.Count - 1)
        {
            e.Handled = true;
            var next = _host.Lines[index + 1];
            var caret = line.Text?.Length ?? 0;
            line.Text = (line.Text ?? string.Empty) + (next.Text ?? string.Empty);
            _host.RemoveLine(index + 1);
            _host.FocusLineByIndex(index, caret);
        }
    }

    /// <summary>行 TextBox 的 PreviewTextInput：选区存在时用输入字符替换整个选区。</summary>
    public void HandleLineTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not TextBox || !HasSelection()) return;
        e.Handled = true;
        if (_host.IsReadOnly) return;

        var (s, _) = OrderedSelection();
        DeleteSelection();
        if (s.Line >= _host.Lines.Count) return;

        var line = _host.Lines[s.Line];
        var text = line.Text ?? string.Empty;
        var caret = Math.Min(s.Caret, text.Length);
        var insert = e.Text ?? string.Empty;
        line.Text = text.Insert(caret, insert);
        _host.FocusLineByIndex(s.Line, caret + insert.Length);
    }

    /// <summary>行 TextBox 的 PreviewMouseLeftButtonDown：接管按下（捕获鼠标），自绘行内/跨行拖选。
    /// 双击/三击（词选/行选）与只读便签交给原生处理。</summary>
    public void HandleLineMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not NoteLineViewModel line) return;
        var index = _host.IndexOf(line);
        if (index < 0) return;

        ClearCrossSelection();

        if (e.ChangedButton != MouseButton.Left) return;

        // 双击（词选）/三击（行选）交给原生 TextBox
        if (e.ClickCount >= 2) return;

        // 只读便签保持原生选择行为（无需编辑语义）
        if (_host.IsReadOnly) return;

        var caret = CaretUnderMouse(box, e.GetPosition(box));

        // Shift+点击：从现有选区锚点扩展选区
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _anchor is { } shiftAnchor)
        {
            e.Handled = true;
            box.Focus();
            _focus = (index, caret);
            if (shiftAnchor.Line == index)
            {
                ClearCrossSelection();
            }
            ApplySelection();
            return;
        }

        // 完全接管：阻止原生 TextEditor 捕获鼠标并启动它自己的行内拖选
        e.Handled = true;
        box.Focus();
        box.Select(caret, 0);
        _anchor = (index, caret);
        _focus = _anchor;
        _isDragging = true;
        _itemsControl.CaptureMouse();
    }

    /// <summary>行 TextBox 与内容区 ScrollViewer 共用的 PreviewMouseMove：拖选更新 + 边缘持续滚动。</summary>
    public void HandleEditorMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
            return;
        }

        if (_anchor is null) return;

        e.Handled = true;

        var ptInItems = e.GetPosition(_itemsControl);
        UpdateSelectionTo(ptInItems);
        UpdateAutoScroll(e.GetPosition(_scrollViewer ?? (IInputElement)_itemsControl));
    }

    /// <summary>鼠标抬起：结束拖选（选区保留，供复制/剪切/删除使用）。</summary>
    public void HandleEditorMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        e.Handled = true;
        EndDrag();
    }

    /// <summary>结束拖选：释放鼠标捕获、停止边缘滚动。</summary>
    private void EndDrag()
    {
        _isDragging = false;
        StopAutoScroll();
        if (_itemsControl.IsMouseCaptured)
        {
            _itemsControl.ReleaseMouseCapture();
        }
    }

    /// <summary>内容区空白处（行 TextBox 之外）的 PreviewMouseLeftButtonDown：光标落至末行末尾，并可向上拖选。</summary>
    public void HandleBackgroundMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 行内、待办复选框、按钮、滚动条上的点击不在此处理
        if (e.OriginalSource is DependencyObject source &&
            (FindAncestor<TextBox>(source) is not null ||
             FindAncestor<CheckBox>(source) is not null ||
             FindAncestor<ButtonBase>(source) is not null ||
             FindAncestor<ScrollBar>(source) is not null))
        {
            return;
        }

        if (e.ChangedButton != MouseButton.Left) return;
        var lines = _host.Lines;
        if (lines.Count == 0) return;

        ClearCrossSelection();
        if (_host.IsReadOnly) return;

        var targets = GetTargets();
        var lastIndex = targets.Count - 1;
        var caret = targets[lastIndex]?.Text?.Length ?? lines[lastIndex].Text?.Length ?? 0;

        e.Handled = true;
        _anchor = (lastIndex, caret);
        _focus = _anchor;
        _isDragging = true;
        _host.FocusLineByIndex(lastIndex, caret);
        _itemsControl.CaptureMouse();
    }

    /// <summary>全选（Ctrl+A 与右键菜单共用）：装饰器高亮所有行。</summary>
    public void SelectAll()
    {
        // 以行集合为准，不要用 target 数量：两者本该一致，
        // 但容器尚未全部生成时 targets 里会有 null 项，用 Count 作索引可能越界或选不满。
        var lines = _host.Lines;
        if (lines.Count == 0) return;

        var lastIndex = lines.Count - 1;
        var lastLen = lines[lastIndex].Text?.Length ?? 0;
        _anchor = (0, 0);
        _focus = (lastIndex, lastLen);
        ApplySelection();
    }

    /// <summary>
    /// 逐行的 TextBox 清单缓存（与 <see cref="IHost.Lines"/> 索引一一对应，未生成的行留 null）。
    /// 拖选过程中行集合不变，因此一次鼠标移动只解析一次，随后
    /// <see cref="ApplySelection"/> 与 <see cref="ResolveTargetBox"/> 直接复用。
    /// </summary>
    private List<TextBox?> GetTargets()
    {
        var lines = _host.Lines;

        // 行集合被重建（Clear + 重新 Add）会换成新实例；行数变化则索引不再对齐
        if (!ReferenceEquals(_cachedLines, lines) || _targets.Count != lines.Count)
        {
            _cachedLines = lines;
            RebuildTargets(lines);
        }
        else
        {
            // 实例与行数都未变，但容器仍可能被重建过，抽查第一个非空项是否还挂在树上
            for (var i = 0; i < _targets.Count; i++)
            {
                var box = _targets[i];
                if (box is null) continue;
                if (PresentationSource.FromVisual(box) is null)
                {
                    RebuildTargets(lines);
                }
                break;
            }
        }

        return _targets;
    }

    /// <summary>重新解析逐行 TextBox，并刷新两处缓存。</summary>
    private void RebuildTargets(ObservableCollection<NoteLineViewModel> lines)
    {
        _boxCache.Clear();
        _targets.Clear();

        for (var i = 0; i < lines.Count; i++)
        {
            var box = _host.FindTextBox(lines[i]);
            _targets.Add(box);
            if (box is not null)
            {
                _boxCache[lines[i]] = box;
            }
        }
    }

    /// <summary>复制：选区复制完整内容（待办行保留 Markdown 前缀）；无选区时复制整篇。</summary>
    public void CopySelection()
    {
        if (HasSelection())
        {
            var text = GetSelectedText();
            if (text.Length > 0)
            {
                Clipboard.SetText(text);
            }
            return;
        }

        var box = FocusedLineBox();
        if (box is not null && box.SelectionLength > 0)
        {
            box.Copy();
            return;
        }

        // 无选区：复制整篇内容
        var all = string.Join('\n', _host.Lines.Select(l => l.ToPlainText()));
        if (all.Length > 0)
        {
            Clipboard.SetText(all);
        }
    }

    /// <summary>剪切：选区剪切；无选区时不动作。</summary>
    public void CutFocused()
    {
        if (_host.IsReadOnly) return;
        if (HasSelection())
        {
            CopySelection();
            DeleteSelection();
            return;
        }

        var box = FocusedLineBox();
        if (box is not null && box.SelectionLength > 0)
        {
            box.Cut();
        }
    }

    /// <summary>粘贴到聚焦行（无聚焦时取首行）；支持多行文本按行拆分。</summary>
    public void PasteFocused()
    {
        if (_host.IsReadOnly) return;
        var box = FocusedLineBox() ?? FirstLineBox();
        if (box is null) return;
        PasteInto(box);
    }

    /// <summary>重置编辑器状态（切换便签时调用）。</summary>
    public void Reset()
    {
        EndDrag();
        ClearCrossSelection();
        InvalidateBoxCache();
    }

    /// <summary>宿主窗口关闭时调用：退订装饰器的布局事件并清空缓存。</summary>
    public void Detach()
    {
        EndDrag();
        ClearCrossSelection();
        InvalidateBoxCache();
        _adorner?.Detach();
        _adorner = null;
    }

    /// <summary>多行粘贴：首段并入目标行，其余段落作为新行插入；待办语法还原为复选框行。</summary>
    public void PasteInto(TextBox target)
    {
        if (_host.IsReadOnly || target is null || !Clipboard.ContainsText()) return;
        var raw = Clipboard.GetText();
        if (raw.Length == 0) return;

        var line = target.DataContext as NoteLineViewModel;
        var index = line is null ? -1 : _host.IndexOf(line);
        if (line is null || index < 0)
        {
            if (_host.Lines.Count == 0) return;
            index = _host.Lines.Count - 1;
            line = _host.Lines[index];
            target = _host.FindTextBox(line) ?? target;
        }

        var caret = target.CaretIndex;

        // 选区：先删除选区，粘贴点为选区起点
        if (HasSelection())
        {
            var (s, _) = OrderedSelection();
            DeleteSelection();
            if (_host.Lines.Count == 0) return;
            index = Math.Min(s.Line, _host.Lines.Count - 1);
            line = _host.Lines[index];
            caret = Math.Min(s.Caret, line.Text?.Length ?? 0);
            target = _host.FindTextBox(line) ?? target;
        }

        var current = line.Text ?? string.Empty;

        // 行内原生选区（如双击词选）：粘贴替换选中文本
        if (target.SelectionLength > 0)
        {
            var selStart = Math.Min(target.SelectionStart, current.Length);
            var selLen = Math.Min(target.SelectionLength, current.Length - selStart);
            current = current.Remove(selStart, Math.Max(selLen, 0));
            caret = selStart;
        }

        caret = Math.Clamp(caret, 0, current.Length);
        var text = raw.Replace("\r\n", "\n").Replace("\r", "\n");
        var parts = text.Split('\n');

        // 单行文本：直接插入
        if (parts.Length == 1)
        {
            line.Text = current.Insert(caret, parts[0]);
            _host.FocusLineByIndex(index, caret + parts[0].Length);
            return;
        }

        // 目标行为空且首段为待办语法：目标行直接转为待办行
        if (current.Length == 0 && caret == 0 && TryParseTodo(parts[0], out var todoChecked, out var todoText))
        {
            line.LineType = NoteLineType.Todo;
            line.IsChecked = todoChecked;
            line.Text = todoText;
            var todoRest = parts.Skip(1).ToList();
            if (todoRest.Count > 0)
            {
                _host.InsertLinesAfter(index, todoRest);
            }
            _host.FocusLineByIndex(index + todoRest.Count, todoRest.Count > 0 ? todoRest[^1].Length : todoText.Length);
            return;
        }

        // 多行文本：首段并入当前行，其余段落作为新行插入，末段拼接光标后内容
        var before = current[..caret];
        var after = current[caret..];
        line.Text = before + parts[0];

        var rest = parts.Skip(1).ToList();
        rest[^1] += after;
        _host.InsertLinesAfter(index, rest);
        _host.FocusLineByIndex(index + rest.Count, rest[^1].Length);
    }

    // ===== 命令重载 =====

    /// <summary>
    /// 经 PreviewExecuted 拦截 TextBox 命令。
    /// ⚠️ 注意：这里**兜不到** Ctrl+C / Ctrl+X。TextBox 的 Copy/Cut 命令在
    /// CanExecute 为 false 时不派发 Executed，而它只认自身 SelectionLength，
    /// 跨行选区（存在 _anchor/_focus）对它不可见。这两条路径已在
    /// <see cref="HandleLineKeyDown"/> 里直接处理，此处仅兜住
    /// Paste / SelectAll / Delete（它们的 CanExecute 不受选区长度限制）。
    /// 保留 Copy/Cut 分支是为了兼容右键菜单等不经过 PreviewKeyDown 的入口。
    /// </summary>
    private void OnLinePreviewExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (sender is not TextBox box) return;

        if (e.Command == ApplicationCommands.Paste)
        {
            e.Handled = true;
            PasteInto(box);
        }
        else if (e.Command == ApplicationCommands.SelectAll)
        {
            e.Handled = true;
            SelectAll();
        }
        else if (e.Command == ApplicationCommands.Copy && HasSelection())
        {
            e.Handled = true;
            CopySelection();
        }
        else if (e.Command == ApplicationCommands.Cut && HasSelection())
        {
            e.Handled = true;
            CutFocused();
        }
        else if (e.Command == ApplicationCommands.Delete && HasSelection())
        {
            e.Handled = true;
            if (!_host.IsReadOnly) DeleteSelection();
        }
    }

    // ===== 选区状态 =====

    /// <summary>是否存在选区（含单行选区）。</summary>
    private bool HasSelection() => _anchor is not null && _focus is not null && !Equals(_anchor, _focus);

    /// <summary>是否存在跨行选区（决定复制/删除走行集合合并逻辑）。</summary>
    private bool HasCrossSelection() =>
        _anchor is not null && _focus is not null && _anchor.Value.Line != _focus.Value.Line;

    /// <summary>按阅读顺序排列选区两端（起点在前）。</summary>
    private ((int Line, int Caret) S, (int Line, int Caret) T) OrderedSelection()
    {
        var a = _anchor!.Value;
        var f = _focus!.Value;
        return a.Line < f.Line || (a.Line == f.Line && a.Caret <= f.Caret) ? (a, f) : (f, a);
    }

    /// <summary>
    /// 应用选区：把各行区间交给装饰器绘制（不依赖 TextBox 焦点态渲染）。
    /// 行 TextBox 经 <see cref="GetTargets"/> 一次性解析，避免每行一次视觉树遍历。
    /// </summary>
    private void ApplySelection()
    {
        if (_anchor is null || _focus is null) return;
        var (s, t) = OrderedSelection();

        var targets = GetTargets();
        var ranges = new List<(TextBox Box, int Start, int End)>();

        for (var i = s.Line; i <= t.Line && i < targets.Count; i++)
        {
            var box = targets[i];
            if (box is null) continue;
            var len = box.Text?.Length ?? 0;

            int start, end;
            if (s.Line == t.Line)
            {
                start = Math.Min(s.Caret, len);
                end = Math.Min(t.Caret, len);
            }
            else if (i == s.Line)
            {
                start = Math.Min(s.Caret, len);
                end = len;
            }
            else if (i == t.Line)
            {
                start = 0;
                end = Math.Min(t.Caret, len);
            }
            else
            {
                start = 0;
                end = len;
            }

            if (start < end)
            {
                ranges.Add((box, start, end));
            }
        }

        EnsureAdorner()?.SetRanges(ranges);
    }

    /// <summary>删除选区：首尾行残余合并到起点行，中间行移除。</summary>
    private void DeleteSelection()
    {
        if (!HasSelection()) return;
        var (s, t) = OrderedSelection();
        var startLine = _host.Lines[s.Line];
        var endLine = _host.Lines[t.Line];
        var startText = startLine.Text ?? string.Empty;
        var endText = endLine.Text ?? string.Empty;
        var sCaret = Math.Min(s.Caret, startText.Length);
        var tCaret = Math.Min(t.Caret, endText.Length);
        var merged = startText[..sCaret] + endText[tCaret..];

        for (var i = t.Line; i > s.Line; i--)
        {
            _host.RemoveLine(i);
        }
        startLine.Text = merged;
        ClearCrossSelection();
        _host.FocusLineByIndex(s.Line, sCaret);
    }

    /// <summary>回车替换选区：删除选区后在起点处换行。</summary>
    private void ReplaceSelectionWithNewline()
    {
        var (s, _) = OrderedSelection();
        DeleteSelection();
        if (s.Line >= _host.Lines.Count) return;

        var line = _host.Lines[s.Line];
        var text = line.Text ?? string.Empty;
        var caret = Math.Min(s.Caret, text.Length);
        line.Text = text[..caret];
        _host.InsertLineAfter(s.Line, text[caret..]);
        _host.FocusLineByIndex(s.Line + 1, 0);
    }

    /// <summary>选区的完整文本（待办行整行选中时保留 Markdown 前缀，粘贴时可还原复选框）。</summary>
    private string GetSelectedText()
    {
        var (s, t) = OrderedSelection();
        var sb = new StringBuilder();
        for (var i = s.Line; i <= t.Line && i < _host.Lines.Count; i++)
        {
            var line = _host.Lines[i];
            var text = line.Text ?? string.Empty;
            var sliceStart = i == s.Line ? Math.Min(s.Caret, text.Length) : 0;
            var sliceEnd = i == t.Line ? Math.Min(t.Caret, text.Length) : text.Length;
            var part = sliceEnd > sliceStart ? text[sliceStart..sliceEnd] : string.Empty;

            if (line.LineType == NoteLineType.Todo && sliceStart == 0 && sliceEnd >= text.Length)
            {
                part = (line.IsChecked ? "- [x] " : "- [ ] ") + part;
            }

            sb.Append(part);
            if (i < t.Line)
            {
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    /// <summary>清除选区状态与高亮，并收起聚焦行的原生选区（双击词选残留）。</summary>
    private void ClearCrossSelection()
    {
        _isDragging = false;
        var hadCrossSelection = _anchor is not null || _focus is not null;
        _anchor = null;
        _focus = null;

        // 无选区时不要触碰装饰器：SetRanges(null) 会强制 InvalidateVisual，
        // 而本方法在每次鼠标按下（包括普通单击）都会走到这里。
        if (hadCrossSelection)
        {
            _adorner?.SetRanges(null);
        }

        if (Keyboard.FocusedElement is TextBox focusedBox)
        {
            focusedBox.Select(focusedBox.CaretIndex, 0);
        }
        StopAutoScroll();
    }

    /// <summary>把选区焦点端更新到编辑区坐标下的字符位置（拖选移动与边缘滚动共用）。</summary>
    private void UpdateSelectionTo(Point ptInItems)
    {
        var target = ResolveTargetBox(ptInItems);
        if (target is null || target.DataContext is not NoteLineViewModel targetLine) return;

        // 在已解析好的行清单里定位该 TextBox 的索引，避免再一次 O(n) 的行集合查找
        var targetIndex = GetTargets().IndexOf(target);
        if (targetIndex < 0)
        {
            targetIndex = _host.IndexOf(targetLine);
        }
        if (targetIndex < 0) return;

        var caret = CaretUnderMouse(target, PointInBox(target, ptInItems));
        if (_focus == (targetIndex, caret)) return;

        _focus = (targetIndex, caret);
        ApplySelection();
    }

    // ===== 边缘持续滚动 =====

    private void UpdateAutoScroll(Point ptInViewer)
    {
        var step = AutoScrollStepFor(ptInViewer);
        if (step == 0)
        {
            StopAutoScroll();
            return;
        }

        ScrollBy(step);
        if (_autoScrollTimer is null)
        {
            _autoScrollTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(25),
            };
            _autoScrollTimer.Tick += (_, _) => OnAutoScrollTick();
        }
        _autoScrollTimer.Start();
    }

    private void OnAutoScrollTick()
    {
        if (!_isDragging || _scrollViewer is null)
        {
            StopAutoScroll();
            return;
        }

        var ptInViewer = Mouse.GetPosition(_scrollViewer);
        var step = AutoScrollStepFor(ptInViewer);
        if (step == 0)
        {
            StopAutoScroll();
            return;
        }

        ScrollBy(step);

        // 滚动后按当前指针位置重新解析目标行，选区跟随内容移动
        UpdateSelectionTo(Mouse.GetPosition(_itemsControl));
    }

    private double AutoScrollStepFor(Point ptInViewer)
    {
        if (_scrollViewer is null || _scrollViewer.ScrollableHeight <= 0) return 0;
        if (ptInViewer.Y < AutoScrollEdge) return -AutoScrollStep;
        if (ptInViewer.Y > _scrollViewer.ViewportHeight - AutoScrollEdge) return AutoScrollStep;
        return 0;
    }

    private void ScrollBy(double step) =>
        _scrollViewer?.ScrollToVerticalOffset(_scrollViewer.VerticalOffset + step);

    private void StopAutoScroll() => _autoScrollTimer?.Stop();

    // ===== 几何与辅助 =====

    /// <summary>把编辑区坐标换算到行 TextBox 内部坐标。</summary>
    private Point PointInBox(TextBox box, Point ptInItems)
    {
        try
        {
            return box.TransformToVisual(_itemsControl).Inverse.Transform(ptInItems);
        }
        catch (InvalidOperationException)
        {
            return new Point(0, 0);
        }
    }

    /// <summary>按几何位置解析指针所在行的 TextBox（空白处取最近行，支持换行行高）。</summary>
    private TextBox? ResolveTargetBox(Point ptInEditor)
    {
        TextBox? nearest = null;
        double best = double.MaxValue;
        var targets = GetTargets();

        for (var i = 0; i < targets.Count; i++)
        {
            var box = targets[i];
            if (box is null) continue;

            double top, bottom;
            try
            {
                var topLeft = box.TransformToVisual(_itemsControl).Transform(new Point(0, 0));
                top = topLeft.Y;
                bottom = topLeft.Y + box.ActualHeight;
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            // 行自上而下排列：一旦指针已在本行上方，后面的行只会更远
            if (ptInEditor.Y < top)
            {
                var dist = top - ptInEditor.Y;
                if (dist < best)
                {
                    best = dist;
                    nearest = box;
                }
                break;
            }

            if (ptInEditor.Y <= bottom)
            {
                return box;
            }

            var gap = ptInEditor.Y - bottom;
            if (gap < best)
            {
                best = gap;
                nearest = box;
            }
        }
        return nearest;
    }

    /// <summary>指针在 TextBox 内对应的字符位置（上方取行首、下方取行尾、右侧空白取行尾）。</summary>
    private static int CaretUnderMouse(TextBox box, Point ptInBox)
    {
        var len = box.Text?.Length ?? 0;
        if (len == 0) return 0;
        if (ptInBox.Y < 0) return 0;

        // 行尾插入点矩形：其左边缘即「最后一字之后」的 X 坐标。
        // 关键：GetCharacterIndexFromPoint 在文本右侧空白处**永远不会返回 len**
        // （最多给出 len - 1），所以必须单独判断指针是否越过行尾插入点，
        // 否则从后往前拖选时，上一行的最后一个字永远选不中。
        var endRect = box.GetRectFromCharacterIndex(len);
        var lineHeight = endRect.Height > 0 ? endRect.Height : box.ActualHeight;

        if (endRect.Height > 0)
        {
            // 指针在最后一条视觉行之下 → 行尾
            if (ptInBox.Y > endRect.Bottom) return len;

            // 指针在同一视觉行的行尾插入点右侧 → 行尾
            var sameVisualLine = ptInBox.Y >= endRect.Top - lineHeight && ptInBox.Y <= endRect.Bottom;
            if (sameVisualLine && ptInBox.X > endRect.Left) return len;
        }
        else if (ptInBox.Y > box.ActualHeight)
        {
            return len;
        }

        var caret = box.GetCharacterIndexFromPoint(ptInBox, true);
        if (caret < 0)
        {
            caret = ptInBox.X <= 0 ? 0 : len;
        }
        return Math.Min(Math.Max(caret, 0), len);
    }

    private TextBox? FocusedLineBox() =>
        Keyboard.FocusedElement is TextBox { DataContext: NoteLineViewModel } box ? box : null;

    private TextBox? FirstLineBox() =>
        _host.Lines.Count > 0 ? _host.FindTextBox(_host.Lines[0]) : null;

    private static bool TryParseTodo(string raw, out bool isChecked, out string text)
    {
        if (raw.StartsWith("- [ ] ", StringComparison.Ordinal))
        {
            isChecked = false;
            text = raw[6..];
            return true;
        }
        if (raw.StartsWith("- [x] ", StringComparison.Ordinal) || raw.StartsWith("- [X] ", StringComparison.Ordinal))
        {
            isChecked = true;
            text = raw[6..];
            return true;
        }
        if (raw == "- [ ]")
        {
            isChecked = false;
            text = string.Empty;
            return true;
        }
        if (raw == "- [x]" || raw == "- [X]")
        {
            isChecked = true;
            text = string.Empty;
            return true;
        }
        isChecked = false;
        text = raw;
        return false;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }
            // 🔴 事件源可能是 Run（FrameworkContentElement，非 Visual），必须走 GetParentSafe
            current = TreeWalker.GetParentSafe(current);
        }
        return null;
    }

    // ===== 选区高亮装饰器 =====

    private SelectionAdorner? EnsureAdorner()
    {
        if (_adorner is not null) return _adorner;
        var layer = AdornerLayer.GetAdornerLayer(_itemsControl);
        if (layer is null) return null;
        _adorner = new SelectionAdorner(_itemsControl, _scrollViewer);
        layer.Add(_adorner);
        return _adorner;
    }
}
