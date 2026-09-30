using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

using NoteApp.Helpers;

using Xunit;

namespace NoteApp.Tests;

/// <summary>
/// 用**真实视觉树**复现并钉死「便签大段文字鼠标选中后强调色错乱」。
///
/// 构造与 NoteWindow 一致的行容器（Grid + 复选框列 + 自动换行 TextBox），
/// 挂上真实的 <see cref="SelectionAdorner"/> + <see cref="SelectionGeometryCache"/>，
/// 再取实际绘制矩形做断言。
///
/// 为什么必须用真实控件：缺陷可能落在「几何算法 / 变换矩阵 / 装饰器坐标系」三者之一，
/// 纯算法替身测不出坐标与换行的真实交互 —— 事实上第一版纯算法测试正是因为
/// 人造数据把「少一个字」当成了正确行为，7 个用例全绿却掩盖了真实缺陷。
/// </summary>
public class SelectionAdornerGeometryTests
{
    /// <summary>与用户截图同源的场景：一段会自动折行的长中文 + 若干短行。</summary>
    private static readonly string[] NoteLines =
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
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw error;
    }

    private static (Window Window, ItemsControl Items, List<TextBox> Boxes) BuildNoteVisual(double width)
    {
        var items = new ItemsControl();
        var textBoxes = new List<TextBox>();

        foreach (var text in NoteLines)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 与便签一致：待办复选框占第 0 列，文本在第 1 列（非待办时折叠但列仍占位）
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
                AcceptsTab = false,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                IsInactiveSelectionHighlightEnabled = true,
            };
            tb.SetValue(TextBlock.LineHeightProperty, 24.75);
            tb.SetValue(TextBlock.LineStackingStrategyProperty, LineStackingStrategy.BlockLineHeight);
            Grid.SetColumn(tb, 1);
            grid.Children.Add(tb);
            textBoxes.Add(tb);

            items.Items.Add(grid);
        }

        var window = new Window
        {
            Content = items,
            Width = width,
            Height = 700,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -3000,
            Top = -3000,
            ShowInTaskbar = false,
        };
        window.Show();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        items.UpdateLayout();
        foreach (var tb in textBoxes) tb.UpdateLayout();

        return (window, items, textBoxes);
    }

    /// <summary>取出装饰器实际用于绘制的矩形。</summary>
    private static List<Rect> CachedRectsOf(SelectionAdorner adorner)
    {
        var field = typeof(SelectionAdorner).GetField(
            "_cachedRects",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        return (List<Rect>)field!.GetValue(adorner)!;
    }

    /// <summary>
    /// 逐视觉行算出「期望矩形」：左边缘取行首字符的前插入点，
    /// 右边缘取行尾字符的**后插入点**（<c>GetRectFromCharacterIndex(i, true)</c>），
    /// 这样跨行也不会退回到下一行行首。
    /// </summary>
    private static List<(int Start, int End, double Left, double Right, double Top, double Height)>
        ExpectedRows(TextBox box, int start, int end)
    {
        var rows = new List<(int, int, double, double, double, double)>();
        var rowStart = start;
        double positionOf(int i) => box.GetRectFromCharacterIndex(i, true).Left;
        var caret = box.GetRectFromCharacterIndex(start);

        double top = caret.Top;
        double height = caret.Height;

        for (var i = start + 1; i < end; i++)
        {
            var cur = box.GetRectFromCharacterIndex(i);
            if (cur.Top > top + 0.5)
            {
                rows.Add((rowStart, i - 1,
                    box.GetRectFromCharacterIndex(rowStart).Left, positionOf(i - 1), top, height));
                rowStart = i;
                top = cur.Top;
                height = cur.Height;
            }
        }

        rows.Add((rowStart, end - 1,
            box.GetRectFromCharacterIndex(rowStart).Left, positionOf(end - 1), top, height));
        return rows;
    }

    /// <summary>
    /// 核心回归：全选一段会自动折行的长文本，
    /// 每一视觉行的高亮都必须完整覆盖该行**最后一个字符**。
    /// </summary>
    [Fact]
    public void FullSelectionOfWrappedLine_EachVisualRowCoversItsLastCharacter()
    {
        RunOnSta(() =>
        {
            var (window, items, boxes) = BuildNoteVisual(700);
            try
            {
                var box = boxes[0];
                var len = box.Text.Length;

                var layer = AdornerLayer.GetAdornerLayer(items);
                Assert.NotNull(layer);
                var adorner = new SelectionAdorner(items, null);
                layer!.Add(adorner);
                try
                {
                    adorner.SetRanges(new List<(TextBox, int, int)> { (box, 0, len) });
                    var actual = CachedRectsOf(adorner);
                    var expected = ExpectedRows(box, 0, len);

                    // 该文本必须真的折行，否则本用例失去意义
                    Assert.True(expected.Count >= 2, $"测试文本未折行（视觉行数={expected.Count}）");
                    Assert.Equal(expected.Count, actual.Count);

                    for (var r = 0; r < expected.Count; r++)
                    {
                        var e = expected[r];
                        Assert.Equal(e.Left, actual[r].X, 0.5);
                        Assert.Equal(e.Right - e.Left, actual[r].Width, 0.5);
                        Assert.Equal(e.Top, actual[r].Y, 0.5);
                    }

                    // 更强的不变量：每一行的右边缘必须严格越过倒数第二个字符的左边缘
                    // （即真的把最后一个字包含进来了）
                    foreach (var e in expected)
                    {
                        if (e.End > e.Start)
                        {
                            var penultimate = box.GetRectFromCharacterIndex(Math.Max(e.End - 1, e.Start)).Left;
                            Assert.True(e.Right > penultimate,
                                $"行 {e.Start}..{e.End} 的右边缘 {e.Right} 未覆盖最后一个字符（前插入点 {penultimate}）");
                        }
                    }
                }
                finally
                {
                    layer.Remove(adorner);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 每一行的行尾都必须被覆盖：对每一行单独做「整行选中」，
    /// 断言其矩形右边缘 ≥ 该行最后一个字符的前插入点位置 + 一个字宽的一部分。
    /// </summary>
    [Fact]
    public void FullSelectionOfEachLine_CoversTrailingCharacter()
    {
        RunOnSta(() =>
        {
            var (window, items, boxes) = BuildNoteVisual(700);
            try
            {
                var layer = AdornerLayer.GetAdornerLayer(items);
                Assert.NotNull(layer);
                var adorner = new SelectionAdorner(items, null);
                layer!.Add(adorner);
                try
                {
                    for (var b = 0; b < boxes.Count; b++)
                    {
                        var box = boxes[b];
                        var len = box.Text!.Length;
                        adorner.SetRanges(new List<(TextBox, int, int)> { (box, 0, len) });

                        var actual = CachedRectsOf(adorner);
                        var expected = ExpectedRows(box, 0, len);
                        Assert.Equal(expected.Count, actual.Count);

                        for (var r = 0; r < expected.Count; r++)
                        {
                            Assert.Equal(expected[r].Left, actual[r].X, 0.5);
                            Assert.Equal(expected[r].Right - expected[r].Left, actual[r].Width, 0.5);
                        }
                    }
                }
                finally
                {
                    layer.Remove(adorner);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 部分选区（跨视觉行但不到行尾）也必须精确落在字符边界上。
    /// </summary>
    [Fact]
    public void PartialSelectionAcrossVisualRows_LandsOnCharacterBoundaries()
    {
        RunOnSta(() =>
        {
            var (window, items, boxes) = BuildNoteVisual(700);
            try
            {
                var box = boxes[0];
                var len = box.Text.Length;
                // 起点取第 5 个字符，终点取末尾前 3 个字符，确保跨视觉行
                const int start = 5;
                var end = len - 3;

                var layer = AdornerLayer.GetAdornerLayer(items);
                Assert.NotNull(layer);
                var adorner = new SelectionAdorner(items, null);
                layer!.Add(adorner);
                try
                {
                    adorner.SetRanges(new List<(TextBox, int, int)> { (box, start, end) });
                    var actual = CachedRectsOf(adorner);
                    var expected = ExpectedRows(box, start, end);

                    Assert.True(expected.Count >= 2, "部分选区未跨视觉行");
                    Assert.Equal(expected.Count, actual.Count);
                    for (var r = 0; r < expected.Count; r++)
                    {
                        Assert.Equal(expected[r].Left, actual[r].X, 0.5);
                        Assert.Equal(expected[r].Right - expected[r].Left, actual[r].Width, 0.5);
                        Assert.Equal(expected[r].Top, actual[r].Y, 0.5);
                    }
                }
                finally
                {
                    layer.Remove(adorner);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 🔴 长时使用后才出现的缺陷：**编辑文字之后**再选中，高亮必须按新内容算。
    ///
    /// 用户报「重启后一开始正常，用久了光标选中乱跳、强调色错乱」。根因是字符插入点表
    /// 的缓存键只有 textLength / 字号 / 宽度 —— 用户改字后若**字数恰好不变**
    /// （替换同长度文字、或删掉又补回来），缓存会继续返回**旧文字的插入点位置表**，
    /// 于是高亮画在旧位置、命中测试也按旧位置换算。缓存一旦建立就永不刷新，
    /// 所以「越用越容易踩中」；重启后缓存为空 → 又恢复正常。
    ///
    /// 本用例先把同一行的文本换成**等长的不同文本**（全角空格 vs 全角汉字，
    /// 字形宽度刻意不同），再断言高亮矩形必须与**新文本**的真实几何一致。
    /// </summary>
    [Fact]
    public void EditTextWithSameLength_ThenSelect_HighlightFollowsNewContent()
    {
        RunOnSta(() =>
        {
            var (window, items, boxes) = BuildNoteVisual(700);
            try
            {
                var box = boxes[0];
                var layer = AdornerLayer.GetAdornerLayer(items);
                Assert.NotNull(layer);
                var adorner = new SelectionAdorner(items, null);
                layer!.Add(adorner);
                try
                {
                    // 1) 先在旧内容上建立缓存（模拟「刚启动时选中过」）
                    var oldLen = box.Text!.Length;
                    adorner.SetRanges(new List<(TextBox, int, int)> { (box, 0, oldLen) });
                    box.UpdateLayout();

                    // 2) 换成等长的不同文字：西文全换成等长中文，字形宽度截然不同。
                    //    长度保持不变 → 旧缓存键全部命中 → 这正是缺陷的触发器。
                    var replacement = new string('测', oldLen);
                    Assert.Equal(oldLen, replacement.Length);
                    box.Text = replacement;
                    box.UpdateLayout();

                    // 3) 再选中整行
                    adorner.SetRanges(new List<(TextBox, int, int)> { (box, 0, oldLen) });
                    var actual = CachedRectsOf(adorner);

                    // 4) 期望值必须按**新文本**的真实几何算
                    var expected = ExpectedRows(box, 0, oldLen);
                    Assert.Equal(expected.Count, actual.Count);
                    for (var r = 0; r < expected.Count; r++)
                    {
                        Assert.Equal(expected[r].Left, actual[r].X, 0.5);
                        Assert.Equal(expected[r].Right - expected[r].Left, actual[r].Width, 0.5);
                        Assert.Equal(expected[r].Top, actual[r].Y, 0.5);
                        Assert.Equal(expected[r].Height, actual[r].Height, 0.5);
                    }
                }
                finally
                {
                    layer.Remove(adorner);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 编辑后「选区端点也变了」的组合场景：先整行选中、再改成等长新文本、
    /// 然后选**前半段**。断言新区间的高亮与新区间的真实几何一致。
    /// </summary>
    [Fact]
    public void EditTextWithSameLength_ThenSelectPrefix_HighlightFollowsNewRange()
    {
        RunOnSta(() =>
        {
            var (window, items, boxes) = BuildNoteVisual(700);
            try
            {
                var box = boxes[0];
                var layer = AdornerLayer.GetAdornerLayer(items);
                Assert.NotNull(layer);
                var adorner = new SelectionAdorner(items, null);
                layer!.Add(adorner);
                try
                {
                    var oldLen = box.Text!.Length;
                    adorner.SetRanges(new List<(TextBox, int, int)> { (box, 0, oldLen) });
                    box.UpdateLayout();

                    box.Text = new string('测', oldLen);
                    box.UpdateLayout();

                    var half = oldLen / 2;
                    adorner.SetRanges(new List<(TextBox, int, int)> { (box, 0, half) });
                    var actual = CachedRectsOf(adorner);
                    var expected = ExpectedRows(box, 0, half);

                    Assert.Equal(expected.Count, actual.Count);
                    for (var r = 0; r < expected.Count; r++)
                    {
                        Assert.Equal(expected[r].Left, actual[r].X, 0.5);
                        Assert.Equal(expected[r].Right - expected[r].Left, actual[r].Width, 0.5);
                    }
                }
                finally
                {
                    layer.Remove(adorner);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }
}
