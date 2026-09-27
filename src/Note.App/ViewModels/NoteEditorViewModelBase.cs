using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

using CommunityToolkit.Mvvm.ComponentModel;

using NoteApp.Helpers;
using NoteApp.Models;
using NoteApp.Models.Enums;
using NoteApp.Services;

namespace NoteApp.ViewModels;

/// <summary>
/// 便签编辑器 ViewModel 的公共基类：<see cref="NoteWindowViewModel"/>（独立便签窗口）与
/// <see cref="NotePreviewViewModel"/>（主页右侧编辑区）都派生自它。
///
/// <para>
/// 存在的理由：这两个 VM 此前**各写了一份**行编辑、撤销栈、防抖自动保存、字体与配色解析，
/// 19 个同名方法里多数逐字节相同。任何「两处都要改」的改动迟早漏一处 ——
/// 真实发生过：给便签窗口 VM 加了 <see cref="NoteForeground"/> 却没给主页 VM 加，
/// 而 <c>Styles.xaml</c> 的待办复选框样式按 <c>ItemsControl</c> 祖先的 DataContext 取这个属性，
/// 主页那边绑定静默失败 → 复选框描边为 null → **复选框彻底不可见**（文字与删除线却正常，
/// 表象极具误导性）。收口到基类后，配色/字体/行编辑只有一份实现，这类漂移在结构上不可能再发生。
/// </para>
///
/// <para>
/// 差异点通过三个抽象/虚成员表达，而不是复制整段逻辑：
/// <list type="bullet">
///   <item><see cref="RawContent"/> / <see cref="SetRawContent"/>：纯文本真源是
///     <c>Content</c>（便签窗口）还是 <c>EditableContent</c>（主页编辑区）。</item>
///   <item><see cref="OnLinesMergedToContent"/>：行内容合并回纯文本后的差异补充
///     （脏标记等）。</item>
///   <item><see cref="OnAutosaveElapsed"/>：防抖计时到点后如何落库。</item>
/// </list>
/// </para>
///
/// <para>子类构造顺序提醒：基类构造函数**不会**触碰 <see cref="RawContent"/>（子类字段此时还没赋值），
/// 所以子类必须在自己的构造函数里显式调用一次 <see cref="RebuildLinesFromContent"/> 完成行集合初始化。</para>
/// </summary>
public abstract partial class NoteEditorViewModelBase : ViewModelBase, INoteLineParent
{
    /// <summary>防抖自动保存延迟：输入停顿这么久才落库，避免每个字符都写一次数据库。</summary>
    protected const int AutosaveMilliseconds = 500;

    private const int UndoCoalesceMilliseconds = 600;
    private const int MaxUndoSteps = 200;

    private readonly DispatcherTimer _autosaveTimer;
    private readonly NoteColorConverter _colorConverter = new();
    private readonly RelativeTimeConverter _relativeTimeConverter = new();

    private readonly List<string> _undoStack = new();
    private readonly List<string> _redoStack = new();

    /// <summary>撤销基线：上次快照时的内容。600ms 内的连续修改合并为一步撤销。</summary>
    private string _lastSnapshot = string.Empty;
    private DateTime _lastEditUtc;

    /// <summary>正在从行集合更新纯文本标志：防止行合并触发内容属性变更导致循环分割。</summary>
    private bool _isUpdatingFromLines;

    /// <summary>正在重建行集合标志：防止重建过程中行事件回灌 <see cref="NotifyLineContentChanged"/> 造成无限循环。</summary>
    private bool _isRebuildingLines;

    /// <summary>
    /// 行重建版本号：每次 <see cref="RebuildLinesFromContent"/> 自增。
    /// 撤销/重做后用它判断「行集合是否已经被自动重建」，从而在内容属性因值未变化
    /// 而未触发通知时补一次重建 —— 比手工推演各 VM 的 setter 早退分支可靠。
    /// </summary>
    private int _rebuildVersion;

    /// <summary>当前便签模型（两个 VM 共享同一实例，外部状态变更都写在它上面）。</summary>
    public Note Note { get; }

    /// <summary>行集合被整体重建（Clear + 重新 Add）后触发，用于让编辑器作废其行 TextBox 缓存。</summary>
    public event EventHandler? LinesRebuilt;

    /// <summary>内容区字体（系统默认 / 霞鹜文楷），由设置 <c>appearance.note_font</c> 驱动。</summary>
    [ObservableProperty]
    private FontFamily _noteFontFamily;

    /// <summary>内容区字号，跟随全局默认字号设置 <c>appearance.font_size</c>。</summary>
    [ObservableProperty]
    private double _noteFontSize;

    /// <summary>
    /// 便签内容行集合（按行渲染，支持待办事项复选框）。
    /// 内容以纯文本存储在 <c>Note.Content</c> 中，Lines 是运行时的渲染视图。
    /// </summary>
    public ObservableCollection<NoteLineViewModel> Lines { get; } = new();

    protected NoteEditorViewModelBase(Note note, ISettingsService settingsService)
    {
        Note = note;
        _noteFontFamily = ResolveNoteFontFamily(settingsService);
        _noteFontSize = settingsService.GetDouble("appearance.font_size", 16.5);

        _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AutosaveMilliseconds) };
        _autosaveTimer.Tick += (_, _) =>
        {
            _autosaveTimer.Stop();
            OnAutosaveElapsed();
        };
    }

    // ===== 子类差异点 =====

    /// <summary>纯文本真源（便签窗口为 <c>Content</c>，主页编辑区为 <c>EditableContent</c>）。</summary>
    protected abstract string RawContent { get; }

    /// <summary>把纯文本写回真源。经可观察属性 setter 走一遍，顺带触发各子类的同步逻辑。</summary>
    protected abstract void SetRawContent(string value);

    /// <summary>
    /// 防抖自动保存到点时调用（<see cref="RestartAutosave"/> 启动的计时器）。
    /// 两个 VM 的落库策略不同：便签窗口无条件保存，主页编辑区只在有改动时保存。
    /// </summary>
    protected virtual void OnAutosaveElapsed()
    {
    }

    /// <summary>
    /// 行内容合并回纯文本之后的差异补充（默认实现已覆盖两个 VM 的公共部分：
    /// 同步 <c>Note.Content</c>、刷新标题/字数派生属性、重启防抖保存）。
    /// </summary>
    protected virtual void OnLinesMergedToContent()
    {
        Note.Content = RawContent;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(WordCount));
        RestartAutosave();
    }

    /// <summary>
    /// 纯文本内容变化（可观察属性 setter 触发）后的公共处理：
    /// 同步模型、刷新派生属性、重启防抖保存，必要时重建行集合。
    /// </summary>
    protected void OnRawContentChanged(string value)
    {
        Note.Content = value;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(WordCount));
        RestartAutosave();

        // 从行集合反向写回时不能再分割，否则行合并会触发「重建 → 行事件 → 再合并」的循环
        if (!_isUpdatingFromLines)
        {
            RebuildLinesFromContent();
        }
    }

    /// <summary>把外部（非本 VM 编辑路径）的纯文本变更并入撤销基线，不产生撤销步。</summary>
    protected void SetUndoBaseline(string content) => _lastSnapshot = content;

    // ===== 派生显示属性 =====

    /// <summary>标题：为空时取内容首行前 20 字。</summary>
    public string Title
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Note.Title))
            {
                return Note.Title;
            }

            var firstLine = Note.Content.Split('\n')[0].Trim();
            return firstLine.Length <= 20 ? firstLine : firstLine[..20];
        }
    }

    public int WordCount => Note.Content.Length;

    /// <summary>标题字号：比内容字号大 2pt。</summary>
    public double NoteTitleFontSize => NoteFontSize + 2;

    /// <summary>
    /// 正文行高（1.5 倍字号）。便签内容信息密度高，默认行距（约 1.35）偏挤，
    /// 拉开后阅读节奏更松。TextBox 用 LineStackingStrategy=BlockLineHeight 按此值精确排版。
    /// 两个 VM 必须取同一个值，否则同一条便签在两处显示时行距会不一样。
    /// </summary>
    public double NoteLineHeight => Math.Round(NoteFontSize * 1.5, 2);

    /// <summary>相对修改时间（刚刚 / X分钟前 / HH:mm / MM-dd HH:mm）。</summary>
    public string RelativeUpdateTime =>
        (string)(_relativeTimeConverter.Convert(Note.UpdatedAt, typeof(string), null!, CultureInfo.CurrentCulture) ?? string.Empty);

    partial void OnNoteFontSizeChanged(double value)
    {
        OnPropertyChanged(nameof(NoteTitleFontSize));
        OnPropertyChanged(nameof(NoteLineHeight));
    }

    // ===== 便签配色（所有派生属性集中在基类，保证两个 VM 永远都有）=====

    // 主页编辑区与独立便签窗口共用 TodoCheckBoxStyle 等样式，那些样式通过
    // ItemsControl 祖先的 DataContext 读 NoteForeground。
    // 把配色属性全部收在基类，是因为「两处都要有同名属性」曾经漏过一次 ——
    // 漏的那一边绑定静默失败，复选框直接隐形，且表象像模板坏了。

    public Brush NoteBackground => GetColorBrush("Bg");

    public Brush NoteTitleBackground => GetColorBrush("Title");

    public Brush NoteBorderBrush => GetColorBrush("Border");

    /// <summary>便签文字色（正文、描边、复选框取色）。</summary>
    public Brush NoteForeground => GetColorBrush("Text");

    /// <summary>
    /// 便签文字色的原始颜色值。XAML 里需要「便签色 + 指定透明度」的场景
    /// （如折叠态待办徽标的底色、勾选框描边）用得上，
    /// 免得为了一个半透明色再去 Colors.xaml 补一堆静态资源。
    ///
    /// <para>
    /// ⚠️ 必须排除「取了色但其实是透明兜底」的情况：<see cref="NoteColorConverter"/> 在资源缺失时
    /// 返回 <c>Brushes.Transparent</c>（它是 <see cref="SolidColorBrush"/>），
    /// 只判断画刷类型会把全透明色（0x00FFFFFF）当成有效色往下传 —— 那样派生出的一切
    /// 都是看不见的，而界面上只表现为「复选框没了」，很容易误判成模板坏了。
    /// 回退纯黑只是颜色不准，比隐形安全得多。
    /// </para>
    /// </summary>
    public Color NoteForegroundColor =>
        GetColorBrush("Text") is SolidColorBrush { Color.A: > 0 } brush ? brush.Color : Colors.Black;

    /// <summary>
    /// 折叠态左侧语义色脊：取便签 Border 色（比 Bg 深、比 Text 浅），
    /// 让折叠成胶囊的便签也能一眼看出是哪张、什么颜色。
    /// </summary>
    public Brush NoteAccentBrush => GetColorBrush("Border");

    /// <summary>
    /// 便签阴影色（跟随便签色相的暖色）。
    /// 原来统一用纯黑阴影，配在彩色便签上会让边缘发脏灰；
    /// 换成同色相的低饱和深色后，阴影像是纸从桌面浮起而非糊了一圈黑边。
    /// 同样排除全透明兜底色，否则阴影会「消失」而不是退回可用色。
    /// </summary>
    public Color NoteShadowColor =>
        GetColorBrush("Shadow") is SolidColorBrush { Color.A: > 0 } brush
            ? brush.Color
            : Color.FromRgb(0x55, 0x55, 0x55);

    protected Brush GetColorBrush(string part) =>
        (Brush)(_colorConverter.Convert(Note.Color, typeof(Brush), part, CultureInfo.CurrentCulture) ?? Brushes.Transparent);

    /// <summary>便签颜色变化时刷新全部派生配色（供 <c>SetColor</c> 与外部改色两条路径共用）。</summary>
    public void NotifyColorDependentProperties()
    {
        OnPropertyChanged(nameof(NoteBackground));
        OnPropertyChanged(nameof(NoteTitleBackground));
        OnPropertyChanged(nameof(NoteBorderBrush));
        OnPropertyChanged(nameof(NoteForeground));
        OnPropertyChanged(nameof(NoteForegroundColor));
        OnPropertyChanged(nameof(NoteAccentBrush));
        OnPropertyChanged(nameof(NoteShadowColor));
    }

    // ===== 行集合 =====

    /// <summary>
    /// 从当前纯文本分割行，重建 <see cref="Lines"/> 集合（用于初始化和外部内容更新）。
    /// 子类构造函数里必须显式调用一次完成初始化。
    /// </summary>
    protected void RebuildLinesFromContent()
    {
        _isRebuildingLines = true;
        try
        {
            Lines.Clear();
            foreach (var line in RawContent.Split('\n'))
            {
                Lines.Add(NoteLineViewModel.Parse(this, line));
            }
        }
        finally
        {
            _isRebuildingLines = false;
            _rebuildVersion++;
            OnLinesRebuilt();
            LinesRebuilt?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 行集合重建后的差异补充（默认什么都不做）。便签窗口重写它刷新折叠态未完成待办徽标，
    /// 主页编辑区没有折叠形态所以不需要。
    /// </summary>
    protected virtual void OnLinesRebuilt()
    {
    }

    /// <summary>行内容变化时由 <see cref="NoteLineViewModel"/> 调用：合并行到纯文本并触发自动保存。</summary>
    public void NotifyLineContentChanged()
    {
        // 重建行集合时不处理，避免无限循环
        if (_isRebuildingLines) return;

        _isUpdatingFromLines = true;
        try
        {
            SetRawContent(MergeLinesToContent());
            RecordUndo();
            OnLinesMergedToContent();
        }
        finally
        {
            _isUpdatingFromLines = false;
        }
    }

    /// <summary>
    /// 待办勾选状态变化。默认什么都不做，供没有折叠胶囊形态的宿主（主页编辑区）直接沿用；
    /// 便签窗口重写它以刷新折叠态未完成计数徽标。
    /// 与 <see cref="NotifyLineContentChanged"/> 分开的原因：勾选待办需要刷新纯 UI 状态，
    /// 但那个计数不应触发内容保存逻辑。
    /// </summary>
    public virtual void NotifyTodoStateChanged()
    {
    }

    /// <summary>将 <see cref="Lines"/> 合并为纯文本（用于保存到数据库）。</summary>
    private string MergeLinesToContent() => string.Join('\n', Lines.Select(l => l.ToPlainText()));

    /// <summary>在指定索引行后面插入新行（用于 Enter 换行）。</summary>
    public void InsertLineAfter(int index, string text = "")
    {
        var newLine = new NoteLineViewModel(this, NoteLineType.Normal, text);
        if (index >= Lines.Count - 1)
        {
            Lines.Add(newLine);
        }
        else
        {
            Lines.Insert(index + 1, newLine);
        }
        NotifyLineContentChanged();
    }

    /// <summary>在指定行后批量插入解析后的行（用于多行粘贴，待办语法自动识别）。</summary>
    public void InsertLinesAfter(int index, IReadOnlyList<string> rawLines)
    {
        if (rawLines.Count == 0) return;
        var insertIndex = Math.Min(index + 1, Lines.Count);
        foreach (var raw in rawLines)
        {
            Lines.Insert(insertIndex++, NoteLineViewModel.Parse(this, raw));
        }
        NotifyLineContentChanged();
    }

    /// <summary>删除指定行（用于 Backspace 合并行）。</summary>
    public void RemoveLine(int index)
    {
        if (index < 0 || index >= Lines.Count) return;
        Lines.RemoveAt(index);
        NotifyLineContentChanged();
    }

    /// <summary>将指定行合并到上一行（光标在行首按 Backspace 时调用）。返回合并后上一行的文本长度，用于设置光标位置。</summary>
    public int MergeLineWithPrevious(int index)
    {
        if (index <= 0 || index >= Lines.Count) return -1;
        var currentLine = Lines[index];
        var previousLine = Lines[index - 1];
        var previousLength = previousLine.Text.Length;
        // 将当前行内容追加到上一行
        previousLine.Text = previousLine.Text + currentLine.Text;
        Lines.RemoveAt(index);
        NotifyLineContentChanged();
        return previousLength;
    }

    /// <summary>获取指定行在集合中的索引。</summary>
    public int GetLineIndex(NoteLineViewModel line) => Lines.IndexOf(line);

    // ===== 撤销 / 重做 =====

    public bool CanUndo => _undoStack.Count > 0;

    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>记录撤销快照（在内容已更新后调用）：栈顶保存本次修改前的内容。</summary>
    private void RecordUndo()
    {
        var now = DateTime.UtcNow;
        if (_undoStack.Count > 0 && (now - _lastEditUtc).TotalMilliseconds < UndoCoalesceMilliseconds)
        {
            // 同一次输入爆发：不新增撤销步，基线跟随最新内容
            _lastEditUtc = now;
            _lastSnapshot = RawContent;
            return;
        }

        _undoStack.Add(_lastSnapshot);
        if (_undoStack.Count > MaxUndoSteps)
        {
            _undoStack.RemoveAt(0);
        }
        _redoStack.Clear();
        _lastSnapshot = RawContent;
        _lastEditUtc = now;
    }

    /// <summary>撤销：恢复上一次修改前的内容（行集合随内容变化自动重建）。</summary>
    public void Undo()
    {
        if (_undoStack.Count == 0) return;
        var target = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        _redoStack.Add(RawContent);
        ApplyUndoContent(target);
    }

    /// <summary>重做：恢复最近一次撤销前的内容。</summary>
    public void Redo()
    {
        if (_redoStack.Count == 0) return;
        var target = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        _undoStack.Add(RawContent);
        ApplyUndoContent(target);
    }

    /// <summary>
    /// 应用撤销/重做内容。正常情况下写回真源会经可观察属性 setter 触发
    /// <see cref="OnRawContentChanged"/> 并自动重建行集合；但当内容与当前值相同时
    /// setter 不会触发（且子类的 setter 还可能因「模型值未变」提前返回），
    /// 这时靠 <see cref="_rebuildVersion"/> 判定没重建过，补一次。
    /// </summary>
    private void ApplyUndoContent(string target)
    {
        _lastSnapshot = target;
        var version = _rebuildVersion;
        SetRawContent(target);
        if (_rebuildVersion == version)
        {
            RebuildLinesFromContent();
        }
    }

    // ===== 防抖自动保存 =====

    /// <summary>重启防抖计时器（内容或标题变动后调用）。</summary>
    protected void RestartAutosave()
    {
        _autosaveTimer.Stop();
        _autosaveTimer.Start();
    }

    /// <summary>停止防抖计时器（清理时调用）。</summary>
    protected void StopAutosave() => _autosaveTimer.Stop();

    // ===== 字体设置 =====

    /// <summary>
    /// 设置变更：便签字体/字号切换时即时刷新内容区。
    /// 两个 VM 的处理**完全相同**，所以直接订阅本方法即可，不必各写一层转发。
    /// </summary>
    protected void HandleSettingChanged(object? sender, SettingChangedEventArgs e, ISettingsService settingsService)
    {
        if (e.Key == "appearance.note_font")
        {
            NoteFontFamily = ResolveNoteFontFamily(settingsService);
        }
        else if (e.Key == "appearance.font_size")
        {
            NoteFontSize = settingsService.GetDouble("appearance.font_size", 16.5);
        }
    }

    /// <summary>
    /// 根据设置 <c>appearance.note_font</c> 解析内容区字体（system → 系统默认；handwriting → 霞鹜文楷）。
    ///
    /// <para>
    /// 取值必须容错：<see cref="Application.Current"/> 在没有 Application 实例的宿主里是 null
    /// （单测、未来可能的非 WPF 宿主），而 <c>HandwritingFont</c> 资源键也可能缺失。
    /// 这两处以前都直接解引用／硬转 —— 一旦命中就是启动即崩，且崩在构造函数的
    /// <c>App.xaml</c> 资源加载路径上，报错信息完全指不到真正原因。字体取不到时退回系统默认
    /// 是可以接受的降级，直接抛异常不是。
    /// </para>
    /// </summary>
    protected static FontFamily ResolveNoteFontFamily(ISettingsService settingsService)
    {
        try
        {
            var value = settingsService.GetString("appearance.note_font", "handwriting");
            if (value != "handwriting")
            {
                return SystemFonts.MessageFontFamily;
            }

            if (Application.Current?.Resources["HandwritingFont"] is FontFamily font)
            {
                return font;
            }
        }
        catch (Exception ex)
        {
            LogHelper.Error("解析便签字体失败，回退系统默认字体", ex);
        }

        return SystemFonts.MessageFontFamily;
    }
}
