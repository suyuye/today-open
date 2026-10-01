using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using NoteApp.Helpers;

using Xunit;

namespace NoteApp.Tests;

/// <summary>
/// 人工验收图：模拟「用了一阵子之后」的布局位移场景，
/// 渲染高亮以确认它跟着文字走（输出 Temp/_sel_verify/after_shift.png）。
/// 不做断言，仅产出对照图与矩形数据。
/// </summary>
public class SelectionShiftVisualTests
{
    private static readonly string[] Lines =
    {
        "解决一下问题/功能添加，每次只解决一个问题，如果有问题描述不清晰，可向我进一步核实，" +
        "每完成一个问题，都添加并运行对应的有效测试，测试通过后创建一次git提交，全部完成后运行所有测试，让我验收，问题如下：",
        "8、添加自动保存数据和手动保存的按钮",
        "9、因为歌曲面板和歌词面板的部分功能重叠，歌词面板只用来做歌词编辑，不再显示多余选项。",
        "10、优化开发工作页面，以项目为最高级，比如一个项目创建后，然后往下bug记录，开发进程什么。",
    };

    private static void RunOnSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join();
        if (error is not null) throw error;
    }

    [Fact]
    public void RenderAfterLayoutShift() => RunOnSta(Render);

    private static TextBox NewBox(string text) => new()
    {
        Text = text,
        FontFamily = new FontFamily("Microsoft YaHei"),
        FontSize = 16.5,
        TextWrapping = TextWrapping.Wrap,
        AcceptsReturn = false,
        AcceptsTab = false,
        VerticalContentAlignment = VerticalAlignment.Center,
        Padding = new Thickness(0),
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
    };

    private static void Render()
    {
        var items = new ItemsControl();
        var boxes = new List<TextBox>();
        foreach (var text in Lines)
        {
            var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var check = new CheckBox { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 6, 0) };
            Grid.SetColumn(check, 0);
            grid.Children.Add(check);

            var tb = NewBox(text);
            tb.SetValue(TextBlock.LineHeightProperty, 24.75);
            tb.SetValue(TextBlock.LineStackingStrategyProperty, LineStackingStrategy.BlockLineHeight);
            Grid.SetColumn(tb, 1);
            grid.Children.Add(tb);
            boxes.Add(tb);
            items.Items.Add(grid);
        }

        var decorator = new AdornerDecorator { Child = items };
        var host = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xF6, 0xEC)),
            Padding = new Thickness(12),
            Child = decorator,
        };
        var window = new Window
        {
            Content = host,
            Width = 660,
            Height = 430,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000,
            Top = -4000,
            ShowInTaskbar = false,
        };
        window.Show();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        host.UpdateLayout();
        items.UpdateLayout();
        foreach (var tb in boxes) tb.UpdateLayout();

        var layer = AdornerLayer.GetAdornerLayer(items)!;
        var adorner = new SelectionAdorner(items, null);
        layer.Add(adorner);

        // 先在原位建立缓存
        adorner.SetRanges(boxes.Select(b => (b, 0, b.Text!.Length)).ToList());
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

        // 模拟「用了一阵子」：在最前面插入一行（后续行整体下移）
        var inserted = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        inserted.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inserted.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var insCheck = new CheckBox { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 6, 0) };
        Grid.SetColumn(insCheck, 0);
        inserted.Children.Add(insCheck);
        var insBox = NewBox("（新插入的一行，用于制造整体下移）");
        insBox.SetValue(TextBlock.LineHeightProperty, 24.75);
        insBox.SetValue(TextBlock.LineStackingStrategyProperty, LineStackingStrategy.BlockLineHeight);
        Grid.SetColumn(insBox, 1);
        inserted.Children.Add(insBox);
        items.Items.Insert(0, inserted);

        items.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        foreach (var tb in boxes) tb.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

        // 重选同一区间（端点/文本/宽度全未变 —— 旧实现会在这里用旧位置画）
        adorner.SetRanges(boxes.Select(b => (b, 0, b.Text!.Length)).ToList());
        host.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

        var dir = @"D:\PJ\Note\Temp\_sel_verify";
        System.IO.Directory.CreateDirectory(dir);
        Dump(host, System.IO.Path.Combine(dir, "after_shift.png"));

        // 数据核对：每个矩形 Y 必须等于对应 TextBox 位移后的真实原点 Y + 行内偏移
        var field = typeof(SelectionAdorner).GetField(
            "_cachedRects", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var rects = (List<Rect>)field!.GetValue(adorner)!;
        var sb = new StringBuilder();
        sb.AppendLine($"rectCount={rects.Count}");
        foreach (var r in rects)
            sb.AppendLine($"X={r.X:F2} Y={r.Y:F2} W={r.Width:F2} right={r.X + r.Width:F2}");
        sb.AppendLine();
        sb.AppendLine("=== 与各 TextBox 实际原点核对 ===");
        for (var i = 0; i < boxes.Count; i++)
        {
            var o = ((MatrixTransform)boxes[i].TransformToVisual(adorner)).Transform(new Point(0, 0));
            var inBox = boxes[i].GetRectFromCharacterIndex(0);
            sb.AppendLine($"box{i}: origin=({o.X:F2},{o.Y:F2}) 首行内 Top={inBox.Top:F2} -> 期望Y={o.Y + inBox.Top:F2}");
        }
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "after_shift_rects.txt"), sb.ToString());

        layer.Remove(adorner);
        window.Close();
    }

    private static void Dump(Visual visual, string path)
    {
        const double dpi = 2.0;
        var size = (FrameworkElement)visual;
        var w = (int)Math.Ceiling(size.ActualWidth * dpi);
        var h = (int)Math.Ceiling(size.ActualHeight * dpi);
        var rtb = new RenderTargetBitmap(w, h, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        rtb.Render(visual);
        using var fs = System.IO.File.Create(path);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        enc.Save(fs);
    }
}
