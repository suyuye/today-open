using System.Windows;
using System.Windows.Controls;

using NoteApp.Helpers;
using NoteApp.Models.Enums;

namespace NoteApp.Views;

/// <summary>新建标签输入弹窗：输入名称并选择颜色，确定后经 <see cref="TagName"/>/<see cref="TagColor"/> 返回。</summary>
public partial class TagCreateDialog : Window
{
    private NoteColor _selectedColor = NoteColor.GrayDeep;

    public TagCreateDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => NameTextBox.Focus();
    }

    /// <summary>输入的标签名称；取消或未确定时为 null。</summary>
    public string? TagName { get; private set; }

    /// <summary>选择的标签颜色，默认高级灰。</summary>
    public NoteColor TagColor { get; private set; }

    private void ColorRadio_Checked(object sender, RoutedEventArgs e)
    {
        // 走统一的解析入口：既能处理新枚举名，也兼容万一残留的旧名（如 "Yellow"）
        if (sender is RadioButton { Tag: string colorName })
        {
            _selectedColor = NoteColorHelper.Parse(colorName);
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameTextBox.Text.Trim();
        if (name.Length == 0)
        {
            HintText.Visibility = Visibility.Visible;
            NameTextBox.Focus();
            return;
        }

        TagName = name;
        TagColor = _selectedColor;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
