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
/// 生成「选区高亮」的可视化对照图（人工验收用，不做断言）。
/// 输出到 Temp/_sel_verify/。
/// </summary>
public class SelectionVisualRenderTests
{
    private static readonly string[] Lines =
    {
        "解决一下问题/功能添加，每次只解决一个问题，如果有问题描述不清晰，可向我进一步核实，" +
        "每完成一个问题，都添加并运行对应的有效测试，测试通过后创建一次git提交，全部完成后运行所有测试，让我验收，问题如下：",
        "8、添加自动保存数据和手动保存的按钮",
        "9、因为歌曲面板和歌词面板的部分功能重叠，歌词面板只用来做歌词编辑，不再显示多余选项。",
        "10、优化开发工作页面，以项目为最高级，比如一个项目创建后，然后往下bug记录，开发进程什么。",
    };

    [Fact]
    public void RenderSelectionHighlightToPng()
    {
        RunOnSta(Render);
    }

    private static void RunOnSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join();
        if (error is not null) throw error;
    }

    private static void Render()
    {
        var items = new ItemsControl();
        var boxes = new List<TextBox>();
        foreach (var text in Lines)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var check = new CheckBox { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 6, 0) };
            Grid.SetColumn(check, 0);
            grid.Children.Add(check);

            var tb = new TextBox
            {
                Text = text,
                FontFamily = new FontFamily("Microsoft YaHei"),
                FontSize = 16.5,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = false,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            };
            tb.SetValue(TextBlock.LineHeightProperty, 24.75);
            tb.SetValue(TextBlock.LineStackingStrategyProperty, LineStackingStrategy.BlockLineHeight);
            Grid.SetColumn(tb, 1);
            grid.Children.Add(tb);
            boxes.Add(tb);
            items.Items.Add(grid);
        }

        // AdornerDecorator 同时承载内容与 AdornerLayer，整体渲染即可拿到高亮
        var decorator = new AdornerDecorator
        {
            Child = items,
        };
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

        var layer = AdornerLayer.GetAdornerLayer(items);
        var adorner = new SelectionAdorner(items, null);
        layer!.Add(adorner);
        adorner.SetRanges(boxes.Select(b => (b, 0, b.Text!.Length)).ToList());

        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        host.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

        var dir = @"D:\PJ\Note\Temp\_sel_verify";
        System.IO.Directory.CreateDirectory(dir);

        Dump(host, System.IO.Path.Combine(dir, "after_fixed.png"));

        // 真实矩形数据
        var field = typeof(SelectionAdorner).GetField(
            "_cachedRects", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var rects = (List<Rect>)field!.GetValue(adorner)!;
        var txt = new System.Text.StringBuilder();
        txt.AppendLine($"rectCount={rects.Count}");
        foreach (var r in rects) txt.AppendLine($"X={r.X:F2} Y={r.Y:F2} W={r.Width:F2} H={r.Height:F2} right={r.X + r.Width:F2}");
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "after_rects.txt"), txt.ToString());

        // ===== 场景二：编辑后（等长替换）高亮必须跟着新文字走 =====
        // 先记录旧几何，再换成等长新文本，最后重选整行并重新渲染。
        var oldRects = rects.ToList();

        var replaced = boxes.Select(b => new string('测', b.Text!.Length)).ToList();
        for (var i = 0; i < boxes.Count; i++)
        {
            boxes[i].Text = replaced[i];
        }

        host.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        foreach (var tb in boxes) tb.UpdateLayout();

        // 重新设置同样区间（整数端点完全一致）——旧实现会在这里错误复用旧矩形
        adorner.SetRanges(boxes.Select(b => (b, 0, b.Text!.Length)).ToList());

        host.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

        Dump(host, System.IO.Path.Combine(dir, "after_edit_same_length.png"));

        var newRects = (List<Rect>)field!.GetValue(adorner)!;
        var txt2 = new System.Text.StringBuilder();
        txt2.AppendLine($"rectCount={newRects.Count}");
        foreach (var r in newRects) txt2.AppendLine($"X={r.X:F2} Y={r.Y:F2} W={r.Width:F2} H={r.Height:F2} right={r.X + r.Width:F2}");
        txt2.AppendLine();
        txt2.AppendLine("=== 与编辑前的差异（非零表示高亮已随新内容更新） ===");
        var maxDiff = 0.0;
        for (var i = 0; i < Math.Min(oldRects.Count, newRects.Count); i++)
        {
            var d = Math.Abs(oldRects[i].Width - newRects[i].Width);
            maxDiff = Math.Max(maxDiff, d);
            txt2.AppendLine($"row{i}: oldW={oldRects[i].Width:F2} newW={newRects[i].Width:F2} diff={d:F2}");
        }
        txt2.AppendLine($"maxWidthDiff={maxDiff:F2}");
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "after_edit_rects.txt"), txt2.ToString());

        layer.Remove(adorner);
        window.Close();
    }

    private static void Dump(Visual visual, string path)
    {
        var dpi = 2.0;
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
