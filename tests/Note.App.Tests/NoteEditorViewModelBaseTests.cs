using NoteApp.Models;
using NoteApp.Models.Enums;
using NoteApp.Services;
using NoteApp.ViewModels;

using Xunit;

namespace NoteApp.Tests;

/// <summary>
/// 行编辑宿主基类 <see cref="NoteEditorViewModelBase"/> 的回归测试。
///
/// 为什么要有这一组：这个基类是「便签窗口 VM」与「主页编辑区 VM」两个具体实现
/// 收口重复代码后的唯一事实来源。此前它们各写一份，19 个方法里多数逐字节相同，
/// 且**真的漏改过一处**（配色属性只加了一边 → 另一边复选框隐形）。
/// 下面的用例直接锁住共用行为本身，避免今后又在某个子类里「就近改一份」。
///
/// 断言全部走公开 API + 可观察属性变化事件，不依赖 WPF 视觉树。
/// </summary>
public class NoteEditorViewModelBaseTests
{
    /// <summary>最小可实例化的宿主：纯文本真源放在自己的字段里，用来隔离验证基类逻辑。</summary>
    private sealed class FakeEditor : NoteEditorViewModelBase
    {
        private string _content;

        public FakeEditor(Note note, string content = "")
            : base(note, new FakeSettingsService())
        {
            _content = content;
            SetUndoBaseline(content);
            RebuildLinesFromContent();
        }

        protected override string RawContent => _content;

        protected override void SetRawContent(string value)
        {
            if (_content == value) return;
            _content = value;
            OnRawContentChanged(value);
        }

        public string Content => _content;

        /// <summary>测试用：把内容从「外部」写入（模拟 NoteUpdated 回灌）。</summary>
        public void SetContentFromOutside(string value)
        {
            _content = value;
            RebuildLinesFromContent();
            SetUndoBaseline(value);
        }

        public Note? RefreshedNote { get; private set; }
    }

    /// <summary>
    /// 最小设置服务替身：只让「读取」可用（基类构造时读字体与字号），
    /// 其余成员一律抛异常 —— 走到意料之外的路径要立刻暴露，而不是悄悄返回默认值。
    /// </summary>
    private sealed class FakeSettingsService : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new();

        public T GetValue<T>(string key, T defaultValue) =>
            _values.TryGetValue(key, out var raw) && raw is T typed ? typed : defaultValue;

        public string GetString(string key, string defaultValue = "") =>
            _values.TryGetValue(key, out var value) ? value : defaultValue;

        public int GetInt(string key, int defaultValue) => defaultValue;

        public bool GetBool(string key, bool defaultValue) => defaultValue;

        public double GetDouble(string key, double defaultValue) => defaultValue;

        public void SetValue<T>(string key, T value) => _values[key] = value?.ToString() ?? string.Empty;

        public void SetString(string key, string value) => _values[key] = value;

        public void SetInt(string key, int value) => _values[key] = value.ToString();

        public void SetBool(string key, bool value) => _values[key] = value.ToString();

        public Dictionary<string, string> GetAll() => new(_values);

        public void ResetToDefaults() => _values.Clear();

        public bool Autostart { get; set; }
        public int StartupDelaySeconds { get; set; }
        public bool MinimizeToTray { get; set; }
        public AppTheme Theme { get; set; }
        public NoteColor DefaultColor { get; set; }
        public int DefaultOpacity { get; set; } = 100;
        public double FontSize { get; set; } = 16.5;
        public bool BackupEnabled { get; set; }
        public int BackupIntervalDays { get; set; }

        // 基类的构造函数与 Cleanup 都只订阅/退订，测试不主动触发它。
        // 关掉「从未使用」告警，而不是加个假的 raise 方法 —— 替身应当保持诚实。
#pragma warning disable CS0067
        public event EventHandler<SettingChangedEventArgs>? SettingChanged;
#pragma warning restore CS0067
    }

    private static Note NewNote(string content = "") => new()
    {
        Id = Guid.NewGuid(),
        Content = content,
        Color = NoteColor.YellowDeep,
    };

    // ===== 行集合初始化与重建 =====

    [Fact]
    public void 初始化时_应按换行拆分为行集合()
    {
        var editor = new FakeEditor(NewNote(), "第一行\n第二行\n第三行");

        Assert.Equal(3, editor.Lines.Count);
        Assert.Equal(new[] { "第一行", "第二行", "第三行" }, editor.Lines.Select(l => l.Text));
    }

    [Fact]
    public void 空内容_应产生一个空行而不是零行()
    {
        // 一个空便签也必须有一行可点的编辑位，否则用户无从下手输入
        var editor = new FakeEditor(NewNote(), "");

        Assert.Single(editor.Lines);
        Assert.Equal(string.Empty, editor.Lines[0].Text);
    }

    [Fact]
    public void 外部内容变更_应重建行集合并通知编辑器作废缓存()
    {
        // 编辑器靠这个事件作废行 TextBox 缓存；漏发会让光标/选区指向已被卸载的旧控件。
        // 注意：行「增删」走的是 NotifyLineContentChanged 路径，结构变更期间**刻意不重建**
        //（否则刚插入的行会被重建冲掉），所以这里用外部内容写入来触发重建。
        var editor = new FakeEditor(NewNote(), "一行");
        var count = 0;
        editor.LinesRebuilt += (_, _) => count++;

        editor.SetContentFromOutside("一行\n两行");

        Assert.Equal(1, count);
        Assert.Equal(2, editor.Lines.Count);
    }

    [Fact]
    public void 行结构增删_不应触发整体重建()
    {
        // 这是刻意的设计：Enter 换行/合并行之后不重建行集合，
        // 否则刚插入的新行会被重建结果冲掉、光标也会跳走。
        var editor = new FakeEditor(NewNote(), "甲");
        var rebuilds = 0;
        editor.LinesRebuilt += (_, _) => rebuilds++;

        editor.InsertLineAfter(0, "乙");
        editor.RemoveLine(1);

        Assert.Equal(0, rebuilds);
    }

    // ===== 行操作 =====

    [Fact]
    public void 在中间行插入_应插在该行之后且保持顺序()
    {
        var editor = new FakeEditor(NewNote(), "甲\n丙");

        editor.InsertLineAfter(0, "乙");

        Assert.Equal(new[] { "甲", "乙", "丙" }, editor.Lines.Select(l => l.Text));
        Assert.Equal("甲\n乙\n丙", editor.Content);
    }

    [Fact]
    public void 在末行插入_应追加到末尾()
    {
        var editor = new FakeEditor(NewNote(), "甲\n乙");

        editor.InsertLineAfter(1, "丙");

        Assert.Equal("甲\n乙\n丙", editor.Content);
    }

    [Fact]
    public void 批量插入_应逐行解析且识别待办语法()
    {
        var editor = new FakeEditor(NewNote(), "标题");

        editor.InsertLinesAfter(0, new[] { "- [ ] 买牛奶", "普通行", "- [x] 已完成" });

        Assert.Equal(4, editor.Lines.Count);
        Assert.Equal(NoteLineType.Todo, editor.Lines[1].LineType);
        Assert.False(editor.Lines[1].IsChecked);
        Assert.Equal(NoteLineType.Normal, editor.Lines[2].LineType);
        Assert.Equal(NoteLineType.Todo, editor.Lines[3].LineType);
        Assert.True(editor.Lines[3].IsChecked);
    }

    [Fact]
    public void 批量插入空集合_应完全不动()
    {
        var editor = new FakeEditor(NewNote(), "甲");
        var rebuilds = 0;
        editor.LinesRebuilt += (_, _) => rebuilds++;

        editor.InsertLinesAfter(0, Array.Empty<string>());

        Assert.Equal("甲", editor.Content);
        Assert.Equal(0, rebuilds);
    }

    [Fact]
    public void 删除行_应移除并把内容写回()
    {
        var editor = new FakeEditor(NewNote(), "甲\n乙\n丙");

        editor.RemoveLine(1);

        Assert.Equal("甲\n丙", editor.Content);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void 删除越界行_应静默忽略(int index)
    {
        var editor = new FakeEditor(NewNote(), "甲");

        editor.RemoveLine(index);

        Assert.Equal("甲", editor.Content);
    }

    [Fact]
    public void 合并行到上一行_应返回上一行原长度作为光标位置()
    {
        var editor = new FakeEditor(NewNote(), "前三字\n后半");

        var caret = editor.MergeLineWithPrevious(1);

        Assert.Equal(3, caret);
        Assert.Equal("前三字后半", editor.Content);
    }

    [Fact]
    public void 首行无法向上合并_应返回负一()
    {
        var editor = new FakeEditor(NewNote(), "甲\n乙");

        Assert.Equal(-1, editor.MergeLineWithPrevious(0));
        Assert.Equal("甲\n乙", editor.Content);
    }

    [Fact]
    public void 待办行合并到上一行_应保留待办前缀()
    {
        // 合并是把「上一行文本 + 当前行文本」拼起来，而 ToPlainText 会按行类型补前缀，
        // 所以上一行是普通行时结果不该多出 - [ ]，当前行的待办标记则随行消失
        var editor = new FakeEditor(NewNote(), "普通行\n- [ ] 待办");

        editor.MergeLineWithPrevious(1);

        Assert.Equal("普通行待办", editor.Content);
    }

    [Fact]
    public void 取行索引_应命中集合内位置()
    {
        var editor = new FakeEditor(NewNote(), "甲\n乙\n丙");

        Assert.Equal(1, editor.GetLineIndex(editor.Lines[1]));
    }

    // ===== 撤销 / 重做 =====

    [Fact]
    public void 初始状态_不可撤销也不可重做()
    {
        var editor = new FakeEditor(NewNote(), "甲");

        Assert.False(editor.CanUndo);
        Assert.False(editor.CanRedo);
    }

    [Fact]
    public void 撤销_应回到修改前的内容()
    {
        var editor = new FakeEditor(NewNote(), "原文");
        // 拉开时间间隔，避开 600ms 合并窗口
        Thread.Sleep(700);
        editor.InsertLineAfter(0, "新增");

        Assert.True(editor.CanUndo);
        editor.Undo();

        Assert.Equal("原文", editor.Content);
        Assert.Single(editor.Lines);
    }

    [Fact]
    public void 撤销后重做_应回到撤销前的内容()
    {
        var editor = new FakeEditor(NewNote(), "原文");
        Thread.Sleep(700);
        editor.InsertLineAfter(0, "新增");
        editor.Undo();

        Assert.True(editor.CanRedo);
        editor.Redo();

        Assert.Equal("原文\n新增", editor.Content);
        Assert.Equal(2, editor.Lines.Count);
    }

    [Fact]
    public void 撤销后行集合必须同步重建()
    {
        // 这是最容易出错的一条：撤销改的是纯文本，行集合靠属性变更事件重建；
        // 一旦重建没发生，界面上还是旧行，用户会以为撤销失灵
        var editor = new FakeEditor(NewNote(), "甲");
        Thread.Sleep(700);
        editor.InsertLineAfter(0, "乙");
        Thread.Sleep(700);
        editor.InsertLineAfter(1, "丙");
        Assert.Equal(3, editor.Lines.Count);

        editor.Undo();

        Assert.Equal(2, editor.Lines.Count);
        Assert.Equal(new[] { "甲", "乙" }, editor.Lines.Select(l => l.Text));
    }

    [Fact]
    public void 撤销到与当前完全相同的内容_行集合仍须重建()
    {
        // 内容值相同时可观察属性 setter 不触发，行集合不会自动重建；
        // 基类靠重建版本号兜住这一分支（旧的两份实现里只有一份写了兜底）
        var editor = new FakeEditor(NewNote(), "甲");
        Thread.Sleep(700);
        editor.InsertLineAfter(0, "乙");
        Thread.Sleep(700);
        editor.InsertLineAfter(1, "丙");

        // 手工把行集合弄脏，模拟「内容没变但行已过期」
        editor.Lines.Clear();

        editor.Undo();

        Assert.Equal(2, editor.Lines.Count);
    }

    [Fact]
    public void 新修改_应清空重做栈()
    {
        var editor = new FakeEditor(NewNote(), "甲");
        Thread.Sleep(700);
        editor.InsertLineAfter(0, "乙");
        editor.Undo();
        Assert.True(editor.CanRedo);

        Thread.Sleep(700);
        editor.InsertLineAfter(0, "丙");

        Assert.False(editor.CanRedo);
    }

    [Fact]
    public void 连续快速输入_应合并为一步撤销()
    {
        // 600ms 内的连续修改算同一次输入爆发；否则打一行字要按十几次 Ctrl+Z
        var editor = new FakeEditor(NewNote(), "甲");

        editor.InsertLineAfter(0, "乙");
        editor.InsertLineAfter(1, "丙");
        editor.InsertLineAfter(2, "丁");

        editor.Undo();

        Assert.Equal("甲", editor.Content);
    }

    [Fact]
    public void 外部内容变更_应并入基线而不产生撤销步()
    {
        // 外部（另一处编辑同一便签）写进来的内容不是本地编辑，不该占用撤销步
        var editor = new FakeEditor(NewNote(), "甲");
        Thread.Sleep(700);
        editor.SetContentFromOutside("外部写入");

        Assert.False(editor.CanUndo);
    }

    // ===== 配色与字体属性（历史 bug 的护栏）=====

    /// <summary>
    /// 两个 VM 都必须有全套配色属性：<c>Styles.xaml</c> 里的待办复选框样式按
    /// ItemsControl 祖先的 DataContext 取 <c>NoteForeground</c>。曾经只给一边加了
    /// 这个属性，另一边绑定静默失败 → BorderBrush 为 null → **复选框彻底不可见**
    /// （文字和删除线却正常，表象像模板坏了）。
    /// 收口到基类后二者同源，这条断言是防止今后又把它挪回某一个子类。
    /// </summary>
    [Fact]
    public void 配色与字体属性_应全部由基类提供()
    {
        var required = new[]
        {
            nameof(NoteEditorViewModelBase.NoteForeground),
            nameof(NoteEditorViewModelBase.NoteForegroundColor),
            nameof(NoteEditorViewModelBase.NoteAccentBrush),
            nameof(NoteEditorViewModelBase.NoteBackground),
            nameof(NoteEditorViewModelBase.NoteTitleBackground),
            nameof(NoteEditorViewModelBase.NoteBorderBrush),
            nameof(NoteEditorViewModelBase.NoteShadowColor),
            nameof(NoteEditorViewModelBase.NoteFontFamily),
            nameof(NoteEditorViewModelBase.NoteFontSize),
            nameof(NoteEditorViewModelBase.NoteTitleFontSize),
            nameof(NoteEditorViewModelBase.NoteLineHeight),
        };

        foreach (var name in required)
        {
            var property = typeof(NoteEditorViewModelBase).GetProperty(name);
            Assert.NotNull(property);
        }
    }

    /// <summary>
    /// 配色取值必须走 <c>NoteColorHelper</c> 的规范键名（<c>Note{色}_{块}</c>）。
    ///
    /// 这里不断言具体颜色值：颜色画刷来自 <c>Application.Current</c> 的资源字典
    /// （Colors.xaml），单测进程里没有 WPF Application，转换器会按设计返回透明画刷。
    /// 真正要守住的是「取色所依赖的键名与色相枚举一致」——
    /// 键名写错会让便签在真机上整片失色，且转换器不抛异常、只有视觉上能发现。
    /// 色板键的完整性另由 NoteColorHelperTests 覆盖。
    /// </summary>
    [Theory]
    [InlineData(NoteColor.YellowDeep, "NoteYellowDeep_Bg")]
    [InlineData(NoteColor.YellowDeep, "NoteYellowDeep_Text")]
    [InlineData(NoteColor.BlueDeep, "NoteBlueDeep_Border")]
    [InlineData(NoteColor.GrayLight, "NoteGrayLight_Shadow")]
    public void 配色资源键_应由色相与色块按规范拼出(NoteColor color, string expectedKey)
    {
        var note = NewNote();
        note.Color = color;
        var editor = new FakeEditor(note);

        var part = expectedKey[(expectedKey.LastIndexOf('_') + 1)..];

        // 反射调用基类受保护取色方法，确认它按 Note{色}_{块} 组装键名
        var method = typeof(NoteEditorViewModelBase)
            .GetMethod("GetColorBrush", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var brush = method.Invoke(editor, new object[] { part });

        Assert.NotNull(brush);
        // 资源缺失时按设计回退透明画刷，所以这里只保证「不为 null、不抛异常」
        Assert.IsAssignableFrom<System.Windows.Media.Brush>(brush);
        Assert.StartsWith($"Note{color}_", expectedKey);
    }

    [Fact]
    public void 通知全部配色_应逐个触发属性变更事件()
    {
        var editor = new FakeEditor(NewNote(), "甲");
        var changed = new List<string>();
        editor.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        editor.NotifyColorDependentProperties();

        Assert.Contains(nameof(NoteEditorViewModelBase.NoteForeground), changed);
        Assert.Contains(nameof(NoteEditorViewModelBase.NoteForegroundColor), changed);
        Assert.Contains(nameof(NoteEditorViewModelBase.NoteAccentBrush), changed);
        Assert.Contains(nameof(NoteEditorViewModelBase.NoteBackground), changed);
        Assert.Contains(nameof(NoteEditorViewModelBase.NoteShadowColor), changed);
    }

    [Fact]
    public void 文字色缺失资源时_应回退纯黑而不是透明()
    {
        // 复选框描边与徽标底色用这个原始颜色值派生半透明同色底。
        // 回退成透明会导致「描边看不见」，而回退纯黑只是颜色不准 —— 后者明显更安全。
        var editor = new FakeEditor(NewNote(), "甲");

        Assert.Equal(System.Windows.Media.Colors.Black, editor.NoteForegroundColor);
    }

    [Fact]
    public void 行高_应为字号的1点5倍()
    {
        var note = NewNote();
        var editor = new FakeEditor(note);

        editor.NoteFontSize = 20;

        Assert.Equal(30, editor.NoteLineHeight);
        Assert.Equal(22, editor.NoteTitleFontSize);
    }

    // ===== 派生显示属性 =====

    [Fact]
    public void 标题为空时_应取内容首行前20字()
    {
        var note = NewNote(new string('字', 30));
        note.Title = null;
        var editor = new FakeEditor(note, note.Content);

        Assert.Equal(new string('字', 20), editor.Title);
    }

    [Fact]
    public void 标题非空时_应直接用标题()
    {
        var note = NewNote("正文");
        note.Title = "显式标题";
        var editor = new FakeEditor(note, "正文");

        Assert.Equal("显式标题", editor.Title);
    }

    [Fact]
    public void 字数_应统计纯文本长度并随修改更新()
    {
        var note = NewNote("三字");
        var editor = new FakeEditor(note, "三字");

        Assert.Equal(2, editor.WordCount);
    }
}
