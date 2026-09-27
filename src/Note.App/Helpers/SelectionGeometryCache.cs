using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using NoteApp.Models.Enums;
using NoteApp.ViewModels;

namespace NoteApp.Helpers;

/// <summary>
/// 选区几何缓存：把「字符区间 → 绘制矩形」的结果算一次存下来，直到失效前反复复用。
/// 避免每次重绘都跑一遍 <see cref="TextBox.GetRectFromCharacterIndex"/> 逐字符循环
/// （跨行全选时是上千次文本布局计算，是选区拖拽卡顿的主因）。
/// 缓存由版本号驱动：内容/选中项/滚动/尺寸/字号任一变化才失效重算。
/// </summary>
internal sealed class SelectionGeometryCache
{
    // 版本号快照：任一变化即失效
    private int _linesCount = -1;
    private double _scrollOffset = double.NaN;
    private double _editorWidth = double.NaN;
    private double _editorHeight = double.NaN;

    // 每个 TextBox 的变换矩阵（相对装饰器坐标系），按引用缓存，避免重绘时逐行 TransformToVisual
    private readonly Dictionary<TextBox, Matrix> _transforms = new();

    private List<Rect>? _rects;

    /// <summary>上一次计算所用选区端点是否与本次一致。</summary>
    private bool _lastRangesEqual;
    private int _rangesFingerprint = -1;
    private int _rangesFirst = -1;
    private int _rangesLast = -1;

    /// <summary>
    /// 每个 TextBox 的字符矩形表：字符索引 → GetRectFromCharacterIndex 的结果。
    /// 同一行内容不变时，这张表与选区端点无关，可反复复用 —— 这样拖动选区
    /// 只重算「受影响的少数几行」，而不是每次都把整个选区逐字符重算一遍。
    /// </summary>
    private readonly Dictionary<TextBox, CharRects> _charRects = new();

    private sealed class CharRects
    {
        public int TextLength = -1;
        public double FontSize = double.NaN;
        public double Width = double.NaN;
        public Rect[] Rects = Array.Empty<Rect>();
    }

    /// <summary>获取绘制矩形；逐字符矩形表按行缓存，只有行内容/字号/宽度变化才重算该行。</summary>
    public List<Rect> GetRects(
        IReadOnlyList<(TextBox Box, int Start, int End)> ranges,
        IReadOnlyList<TextBox> boxes,
        Visual adorner,
        double scrollOffset,
        double editorWidth,
        double editorHeight)
    {
        if (ranges.Count == 0)
        {
            _rects = null;
            _lastRangesEqual = false;
            return _empty;
        }

        // 几何未变 + 端点未变 → 直接复用已算好的矩形
        if (IsValid(boxes, scrollOffset, editorWidth, editorHeight) && SameRanges(ranges))
        {
            return _rects!;
        }

        var rects = new List<Rect>();
        foreach (var (box, start, end) in ranges)
        {
            var len = box.Text?.Length ?? 0;
            if (len == 0) continue;
            var s = Math.Min(start, len);
            var e = Math.Min(end, len);
            if (s >= e) continue;

            if (!TryGetTransform(box, adorner, out var matrix)) continue;

            var charRects = GetCharRects(box, len);
            if (charRects.Length == 0) continue;

            // 按视觉行分段（支持自动换行）：行与行之间用 Y 判断换行。
            // 选区 [s, e) 覆盖字符 s..e-1；右端取「e 位置插入点矩形的左边缘」，
            // 当 e == len 时那就是最后一字的右边缘，因此行尾字符也能被完整选中高亮。
            var first = CharRectAt(charRects, s);
            var top = first.Top;
            var height = first.Height > 0 ? first.Height : box.ActualHeight;
            var x0 = first.Left;
            double x1 = first.Right;

            for (var i = s + 1; i <= e; i++)
            {
                var r = CharRectAt(charRects, i);
                if (r.Top > top + 0.5)
                {
                    // 换行：先把上一视觉行收尾，右端为上一字符的右边缘
                    AddRow(rects, matrix, x0, CharRectAt(charRects, i - 1).Right, top, height);
                    top = r.Top;
                    height = r.Height > 0 ? r.Height : height;
                    x0 = r.Left;
                }
                x1 = r.Left;
            }
            if (x1 <= x0) x1 = CharRectAt(charRects, e).Right;
            AddRow(rects, matrix, x0, x1, top, height);
        }

        _rects = rects;
        Snapshot(boxes, scrollOffset, editorWidth, editorHeight);
        MarkRanges(ranges);
        return rects;
    }

    /// <summary>记录本次端点集合，供下次 IsValid 判断是否可直接复用 _rects。</summary>
    public void MarkRanges(IReadOnlyList<(TextBox Box, int Start, int End)> ranges)
    {
        _lastRangesEqual = true;
        _rangesFingerprint = ranges.Count;
        _rangesFirst = ranges.Count > 0 ? ranges[0].Start : -1;
        _rangesLast = ranges.Count > 0 ? ranges[^1].End : -1;
    }

    /// <summary>端点指纹是否与已计算结果一致。</summary>
    public bool SameRanges(IReadOnlyList<(TextBox Box, int Start, int End)> ranges)
    {
        if (!_lastRangesEqual) return false;
        if (ranges.Count != _rangesFingerprint) return false;
        if (ranges.Count == 0) return true;
        return ranges[0].Start == _rangesFirst && ranges[^1].End == _rangesLast;
    }

    /// <summary>
    /// 取（并缓存）某行的逐字符矩形表，长度 len + 1，索引 i 表示「第 i 个字符的矩形」，
    /// 索引 len 表示行尾（最后一个字符之后）的插入点矩形。
    /// 注意：索引 len 处必须交给原生接口取，不能用末字符矩形手工拼接 ——
    /// 手工拼接出来的零宽矩形会让选区终点落在行尾时算不出可见矩形
    /// （表现为「每段最后一个字选不上」）。
    /// </summary>
    private Rect[] GetCharRects(TextBox box, int len)
    {
        if (_charRects.TryGetValue(box, out var cached) &&
            cached.TextLength == len &&
            cached.FontSize == box.FontSize &&
            cached.Width == box.ActualWidth)
        {
            return cached.Rects;
        }

        var arr = new Rect[len + 1];
        for (var i = 0; i < len; i++)
        {
            arr[i] = box.GetRectFromCharacterIndex(i);
        }
        // 行尾插入点：优先用原生查询（WPF 会给出该视觉行末尾的真实插入点矩形），
        // 取不到时再退回「末字符右移」的保守近似，并保证高度非零。
        var tail = box.GetRectFromCharacterIndex(len);
        if (tail.Height <= 0 && tail.Width <= 0)
        {
            var last = len > 0 ? arr[len - 1] : new Rect(0, 0, 0, box.ActualHeight);
            var h = last.Height > 0 ? last.Height : box.ActualHeight;
            // 用一个空文本测量出的字宽作为行尾插入点宽度，避免出现零宽矩形
            var w = MeasureSpaceWidth(box);
            tail = new Rect(last.Right, last.Top, w, h);
        }
        arr[len] = tail;

        _charRects[box] = new CharRects
        {
            TextLength = len,
            FontSize = box.FontSize,
            Width = box.ActualWidth,
            Rects = arr,
        };
        return arr;
    }

    /// <summary>行尾插入点的兜底宽度：用同字体测量一个空格，失败时退回 1 像素。</summary>
    private static double MeasureSpaceWidth(TextBox box)
    {
        try
        {
            var typeface = new Typeface(
                box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch);
            if (typeface.TryGetGlyphTypeface(out var glyph))
            {
                var advance = glyph.AdvanceWidths.TryGetValue(glyph.CharacterToGlyphMap[' '], out var v)
                    ? v
                    : 0d;
                if (advance > 0)
                {
                    return advance * box.FontSize;
                }
            }
        }
        catch (Exception)
        {
            // 忽略：退回到固定宽度
        }
        return box.FontSize * 0.5;
    }

    private static Rect CharRectAt(Rect[] rects, int index) =>
        index >= 0 && index < rects.Length ? rects[index] : default;

    /// <summary>失效缓存（切换便签、行集合重建时调用）。</summary>
    public void Invalidate()
    {
        _rects = null;
        _transforms.Clear();
        _charRects.Clear();
        _linesCount = -1;
        _scrollOffset = double.NaN;
        _editorWidth = double.NaN;
        _editorHeight = double.NaN;
        _lastRangesEqual = false;
    }

    private static void AddRow(List<Rect> rects, Matrix matrix, double x0, double x1, double top, double height)
    {
        // 右端必须严格大于左端才可见；这里用极小容差，避免浮点舍入把行尾一个像素的行吞掉
        if (x1 - x0 <= 0.01 || height <= 0) return;
        // 变换矩阵在本场景下是「平移 + 等比缩放」，宽度缩放等于对角线缩放 m11
        var scale = matrix.M11;
        var rect = new Rect(x0, top, x1 - x0, height);
        var transformed = new Rect(
            rect.X * matrix.M11 + rect.Y * matrix.M21 + matrix.OffsetX,
            rect.X * matrix.M12 + rect.Y * matrix.M22 + matrix.OffsetY,
            rect.Width * scale,
            rect.Height * scale);
        rects.Add(transformed);
    }

    private bool TryGetTransform(TextBox box, Visual adorner, out Matrix matrix)
    {
        if (_transforms.TryGetValue(box, out matrix)) return true;
        try
        {
            if (box.TransformToVisual(adorner) is not MatrixTransform mt) return false;
            matrix = mt.Matrix;
            _transforms[box] = matrix;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private bool IsValid(
        IReadOnlyList<TextBox> boxes,
        double scrollOffset,
        double editorWidth,
        double editorHeight)
    {
        if (_rects is null) return false;
        if (boxes.Count != _linesCount) return false;
        if (_scrollOffset != scrollOffset) return false;
        if (_editorWidth != editorWidth || _editorHeight != editorHeight) return false;
        if (_transforms.Count == 0) return false;

        // 端点区间没变时 _rects 即为当前选区结果，直接复用；
        // 端点变化时仍需重算（但底层逐字符矩形表已按行缓存，代价很小）。
        return _lastRangesEqual;
    }

    private void Snapshot(
        IReadOnlyList<TextBox> boxes,
        double scrollOffset,
        double editorWidth,
        double editorHeight)
    {
        _linesCount = boxes.Count;
        _scrollOffset = scrollOffset;
        _editorWidth = editorWidth;
        _editorHeight = editorHeight;
    }

    private static readonly List<Rect> _empty = new();
}

