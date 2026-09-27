using CommunityToolkit.Mvvm.ComponentModel;

namespace NoteApp.ViewModels;

/// <summary>便签内容行类型。</summary>
public enum NoteLineType
{
    /// <summary>普通文本行。</summary>
    Normal,
    /// <summary>待办事项行（- [ ] / - [x]）。</summary>
    Todo
}

/// <summary>
/// 便签内容行视图模型。
/// 支持普通文本行和待办事项行（复选框 + 完成删除线）。
/// 待办事项以 Markdown 语法 - [ ] / - [x] 存储在纯文本中，无需改数据库。
/// </summary>
public partial class NoteLineViewModel : ViewModelBase
{
    /// <summary>父视图模型引用，用于行内容变化时通知合并保存。</summary>
    private readonly INoteLineParent _parent;

    /// <summary>行类型。</summary>
    [ObservableProperty]
    private NoteLineType _lineType;

    /// <summary>行文本内容（待办事项行不包含 - [ ] / - [x] 前缀）。</summary>
    [ObservableProperty]
    private string _text = string.Empty;

    /// <summary>待办事项是否完成（仅 Todo 类型有效）。</summary>
    [ObservableProperty]
    private bool _isChecked;

    public NoteLineViewModel(INoteLineParent parent, NoteLineType lineType, string text, bool isChecked = false)
    {
        _parent = parent;
        _lineType = lineType;
        _text = text;
        _isChecked = isChecked;
    }

    /// <summary>行文本变化时通知父视图模型合并保存。</summary>
    partial void OnTextChanged(string value)
    {
        _parent.NotifyLineContentChanged();
    }

    /// <summary>待办事项完成状态变化时通知父视图模型合并保存。</summary>
    partial void OnIsCheckedChanged(bool value)
    {
        if (LineType == NoteLineType.Todo)
        {
            _parent.NotifyTodoStateChanged();
            _parent.NotifyLineContentChanged();
        }
    }

    /// <summary>
    /// 从纯文本行解析为 NoteLineViewModel。
    /// 识别 - [ ]（未完成待办）和 - [x]（已完成待办）前缀。
    /// </summary>
    public static NoteLineViewModel Parse(INoteLineParent parent, string line)
    {
        if (line.StartsWith("- [ ] ", StringComparison.Ordinal))
        {
            return new NoteLineViewModel(parent, NoteLineType.Todo, line.Substring(6), isChecked: false);
        }
        if (line.StartsWith("- [x] ", StringComparison.Ordinal) || line.StartsWith("- [X] ", StringComparison.Ordinal))
        {
            return new NoteLineViewModel(parent, NoteLineType.Todo, line.Substring(6), isChecked: true);
        }
        // 兼容只有 - [ ] 没有空格的情况
        if (line == "- [ ]")
        {
            return new NoteLineViewModel(parent, NoteLineType.Todo, string.Empty, isChecked: false);
        }
        if (line == "- [x]" || line == "- [X]")
        {
            return new NoteLineViewModel(parent, NoteLineType.Todo, string.Empty, isChecked: true);
        }
        return new NoteLineViewModel(parent, NoteLineType.Normal, line);
    }

    /// <summary>将行转换为纯文本（用于保存到数据库）。</summary>
    public string ToPlainText()
    {
        if (LineType == NoteLineType.Todo)
        {
            return IsChecked ? $"- [x] {Text}" : $"- [ ] {Text}";
        }
        return Text;
    }
}
