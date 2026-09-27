using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

using NoteApp.Helpers;

namespace NoteApp.Views.Controls;

/// <summary>
/// 支持关键词高亮的 TextBlock。
///
/// 为什么不用普通 TextBlock + 转换器：WPF 的 <see cref="TextBlock.Inlines"/> 不能直接绑定，
/// 只能由代码重建 Run 集合。这里把「切分逻辑」放在纯函数
/// <see cref="HighlightSegmenter"/> 里（可单测），控件只负责把片段铺成 Run。
///
/// 用法：绑定 <see cref="SourceText"/>（要显示的文本）与 <see cref="Highlight"/>（关键词）。
/// 不要同时再设 <c>Text</c>，那会清掉 Inlines。
/// </summary>
public class HighlightTextBlock : TextBlock
{
    public static readonly DependencyProperty SourceTextProperty = DependencyProperty.Register(
        nameof(SourceText),
        typeof(string),
        typeof(HighlightTextBlock),
        new PropertyMetadata(string.Empty, OnContentChanged));

    public static readonly DependencyProperty HighlightProperty = DependencyProperty.Register(
        nameof(Highlight),
        typeof(string),
        typeof(HighlightTextBlock),
        new PropertyMetadata(string.Empty, OnContentChanged));

    /// <summary>命中片段的高亮底色。</summary>
    public static readonly DependencyProperty HighlightBackgroundProperty = DependencyProperty.Register(
        nameof(HighlightBackground),
        typeof(Brush),
        typeof(HighlightTextBlock),
        new PropertyMetadata(null, OnContentChanged));

    /// <summary>命中片段的文字色（为空则沿用继承的前景色）。</summary>
    public static readonly DependencyProperty HighlightForegroundProperty = DependencyProperty.Register(
        nameof(HighlightForeground),
        typeof(Brush),
        typeof(HighlightTextBlock),
        new PropertyMetadata(null, OnContentChanged));

    public string SourceText
    {
        get => (string)GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    public string Highlight
    {
        get => (string)GetValue(HighlightProperty);
        set => SetValue(HighlightProperty, value);
    }

    public Brush? HighlightBackground
    {
        get => (Brush?)GetValue(HighlightBackgroundProperty);
        set => SetValue(HighlightBackgroundProperty, value);
    }

    public Brush? HighlightForeground
    {
        get => (Brush?)GetValue(HighlightForegroundProperty);
        set => SetValue(HighlightForegroundProperty, value);
    }

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((HighlightTextBlock)d).RebuildInlines();

    private void RebuildInlines()
    {
        Inlines.Clear();

        var segments = HighlightSegmenter.Split(SourceText, Highlight);
        if (segments.Count == 0)
        {
            return;
        }

        foreach (var segment in segments)
        {
            var run = new Run(segment.Text);

            if (segment.IsMatch)
            {
                run.Background = HighlightBackground;
                if (HighlightForeground is not null)
                {
                    run.Foreground = HighlightForeground;
                }
            }

            Inlines.Add(run);
        }
    }
}
