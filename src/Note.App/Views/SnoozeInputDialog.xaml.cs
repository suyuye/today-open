using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;

namespace NoteApp.Views;

/// <summary>自定义稍后提醒输入弹窗：输入分钟数，确定后经 <see cref="Minutes"/> 返回。</summary>
public partial class SnoozeInputDialog : Window
{
    public SnoozeInputDialog(int defaultMinutes)
    {
        InitializeComponent();
        MinutesTextBox.Text = defaultMinutes.ToString();
        Loaded += (_, _) =>
        {
            MinutesTextBox.Focus();
            MinutesTextBox.SelectAll();
        };
    }

    /// <summary>用户输入的分钟数；取消或未确定时为 null。</summary>
    public int? Minutes { get; private set; }

    private void MinutesTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // 仅允许数字输入
        e.Handled = !Regex.IsMatch(e.Text, "^[0-9]+$");
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(MinutesTextBox.Text, out var minutes) && minutes >= 1)
        {
            Minutes = minutes;
            DialogResult = true;
            return;
        }

        HintText.Visibility = Visibility.Visible;
        MinutesTextBox.Focus();
        MinutesTextBox.SelectAll();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
