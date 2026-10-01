using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

using NoteApp.Helpers;

using Xunit;

namespace NoteApp.Tests;

/// <summary>
/// 🔴 回归：**布局位移之后**高亮必须跟着走。
///
/// 用户报「重启后一开始正常，用久了光标选中乱跳、强调色错乱，行末还会超出文字」。
/// 根因是 <see cref="SelectionGeometryCache"/> 把每行 TextBox 相对装饰器的变换矩阵
/// **按引用永久缓存**，只在 <c>Invalidate()</c> 时清 —— 而布局位移（行增删 / 换行回流 /
/// 内边距变化）会让整行平移，此时 TextBox 的引用、文本内容、宽度可能全都没变，
/// 于是所有旧判据（行数 / 滚动 / 编辑区尺寸 / 端点 / 文本哈希）统统通过，
/// 缓存的旧矩阵被继续使用，高亮画在老位置上。
///
/// 缓存是随使用逐步建立的，所以「刚启动没事、用一阵子才飘」。
/// 本用例构造这个位移，断言重算后的矩形必须落在**位移之后**的真实位置。
/// </summary>
public class SelectionTransformDriftTests
{
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
    };

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
    /// 在选区所在行的**上方**插入一行，让这些行整体下移。
    /// 位移之后重选同一区间，高亮的 Y 必须等于该 TextBox 位移后的真实原点 Y。
    /// </summary>
    [Fact]
    public void LayoutShiftAboveSelection_HighlightFollowsNewPosition()
    {
        RunOnSta(() =>
        {
            var items = new ItemsControl();
            var boxes = new List<TextBox>();
            foreach (var text in new[] { "第一行内容", "第二行内容", "第三行内容" })
            {
                var tb = NewBox(text);
                boxes.Add(tb);
                items.Items.Add(tb);
            }

            var decorator = new AdornerDecorator { Child = items };
            var window = new Window
            {
                Content = decorator,
                Width = 660,
                Height = 400,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -4000,
                Top = -4000,
                ShowInTaskbar = false,
            };
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            items.UpdateLayout();
            foreach (var tb in boxes) tb.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

            var layer = AdornerLayer.GetAdornerLayer(items);
            Assert.NotNull(layer);
            var adorner = new SelectionAdorner(items, null);
            layer!.Add(adorner);
            try
            {
                var target = boxes[1];

                // 1) 先在原位建立缓存（模拟「刚启动时选中过一次」）
                adorner.SetRanges(new List<(TextBox, int, int)> { (target, 0, target.Text!.Length) });
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                var before = CachedRectsOf(adorner).Select(r => r.Y).ToList();
                Assert.NotEmpty(before);

                // 2) 在顶部插入一行 → target 整体下移
                items.Items.Insert(0, NewBox("新插入的第一行"));
                items.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                foreach (var tb in boxes) tb.UpdateLayout();

                // 3) 重选同一区间（端点、文本、宽度全都没变 —— 旧实现会直接复用旧矩形）
                adorner.SetRanges(new List<(TextBox, int, int)> { (target, 0, target.Text!.Length) });
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

                var actual = CachedRectsOf(adorner);
                Assert.NotEmpty(actual);

                // 4) 期望：矩形 X/Y 必须加上 target 位移后的真实原点
                var boxOrigin = ((MatrixTransform)target.TransformToVisual(adorner))
                    .Transform(new Point(0, 0));

                // 该行是单视觉行：矩形 Y 应等于 TextBox 原点 Y + 行内偏移
                var firstRectInBox = target.GetRectFromCharacterIndex(0);
                var expectedY = boxOrigin.Y + firstRectInBox.Top;
                var expectedX = boxOrigin.X + firstRectInBox.Left;

                Assert.Equal(expectedX, actual[0].X, 0.5);
                Assert.Equal(expectedY, actual[0].Y, 0.5);

                // 同时确认「下移」真的发生了，否则本用例失去意义
                Assert.True(actual[0].Y > before[0] + 5,
                    $"target 未发生下移（before={before[0]:F2}, after={actual[0].Y:F2}），用例无效");
            }
            finally
            {
                layer.Remove(adorner);
                window.Close();
            }
        });
    }

    /// <summary>
    /// 位移后**未重设选区**（区间完全没变）也不得复用旧几何 ——
    /// 装饰器在 LayoutUpdated 里重绘时同样要拿到新位置。
    /// </summary>
    [Fact]
    public void LayoutShiftAboveSelection_RedrawUsesFreshGeometry()
    {
        RunOnSta(() =>
        {
            var items = new ItemsControl();
            var boxes = new List<TextBox>();
            foreach (var text in new[] { "第一行内容", "第二行内容", "第三行内容" })
            {
                var tb = NewBox(text);
                boxes.Add(tb);
                items.Items.Add(tb);
            }

            var decorator = new AdornerDecorator { Child = items };
            var window = new Window
            {
                Content = decorator,
                Width = 660,
                Height = 400,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -4000,
                Top = -4000,
                ShowInTaskbar = false,
            };
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            items.UpdateLayout();
            foreach (var tb in boxes) tb.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

            var layer = AdornerLayer.GetAdornerLayer(items);
            Assert.NotNull(layer);
            var adorner = new SelectionAdorner(items, null);
            layer!.Add(adorner);
            try
            {
                var target = boxes[2];
                adorner.SetRanges(new List<(TextBox, int, int)> { (target, 0, target.Text!.Length) });
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                var beforeY = CachedRectsOf(adorner)[0].Y;

                // 顶部连插两行，强制位移，但**不重设选区**
                items.Items.Insert(0, NewBox("新行 A"));
                items.Items.Insert(0, NewBox("新行 B"));
                items.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                foreach (var tb in boxes) tb.UpdateLayout();

                // 触发一次 SetRanges（内容/端点完全一致）——旧实现会命中提前返回
                adorner.SetRanges(new List<(TextBox, int, int)> { (target, 0, target.Text!.Length) });
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

                var rects = CachedRectsOf(adorner);
                var boxOrigin = ((MatrixTransform)target.TransformToVisual(adorner))
                    .Transform(new Point(0, 0));
                var inBox = target.GetRectFromCharacterIndex(0);
                Assert.Equal(boxOrigin.Y + inBox.Top, rects[0].Y, 0.5);
                Assert.True(rects[0].Y > beforeY + 5,
                    $"target 未发生下移（before={beforeY:F2}, after={rects[0].Y:F2}），用例无效");
            }
            finally
            {
                layer.Remove(adorner);
                window.Close();
            }
        });
    }
}
