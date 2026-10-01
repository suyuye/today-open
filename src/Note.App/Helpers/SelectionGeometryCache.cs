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
///
/// 缓存有效性由三组判据共同决定，任一失效即重算：
/// <list type="number">
/// <item>规模类：行数 / 滚动 / 编辑区尺寸；</item>
/// <item>内容类：选区端点 + 各行文本内容指纹（**不能只比长度**）；</item>
/// <item>布局类：各行 TextBox 相对装饰器的**实时变换矩阵**（**不能永久信任缓存值**）——
/// 行增删、换行回流、内边距/字号变化都会让整行位移，而引用/文本/宽度可能完全不变。</item>
/// </list>
/// </summary>
internal sealed class SelectionGeometryCache
{
    // 版本号快照：任一变化即失效
    private int _linesCount = -1;
    private double _scrollOffset = double.NaN;
    private double _editorWidth = double.NaN;
    private double _editorHeight = double.NaN;

    // 每个 TextBox 的变换矩阵（相对装饰器坐标系），按引用缓存，避免重绘时逐行 TransformToVisual。
    // 🔴 缓存不可无条件信任：行增删 / 换行回流 / 内边距变化都会让整行位移，
    //    而 TextBox 引用不变、宽度也可能不变，仅凭宽度/内容判据看不出位移。
    //    因此每次复用前都要与实时矩阵核对（见 EnsureTransformsFresh）。
    private readonly Dictionary<TextBox, Matrix> _transforms = new();

    private List<Rect>? _rects;

    /// <summary>上一次计算所用选区端点是否与本次一致。</summary>
    private bool _lastRangesEqual;
    private int _rangesFingerprint = -1;
    private int _rangesFirst = -1;
    private int _rangesLast = -1;

    /// <summary>
    /// 上次计算时各行文本的内容指纹。**端点相同也必须校验它**：
    /// 用户改字后若字数不变，区间完全一致，仅凭区间会误判「无需重算」，
    /// 于是旧矩形被一直复用（「用久了高亮/光标才错乱」的根因）。
    /// </summary>
    private int _rangesContentHash = -1;

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

        /// <summary>
        /// 建表时的文本内容。**必须参与缓存有效性判定**：
        /// 只比 TextLength/FontSize/Width 是不够的 —— 用户改字后若字数恰好不变
        /// （替换同长度文字、或删了又补回来），缓存会继续返回**旧文字的插入点位置表**，
        /// 于是高亮画在旧位置、鼠标点选也按旧位置换算字符索引，
        /// 表现为「用一阵子之后高亮错位 + 光标落点不准」，重启后缓存为空又恢复正常。
        /// </summary>
        public string Text = string.Empty;

        /// <summary>逐字符「前插入点」矩形，长度 = TextLength + 1。</summary>
        public Rect[] Leading = Array.Empty<Rect>();

        /// <summary>
        /// 逐字符「后插入点」矩形，长度同 <see cref="Leading"/>。
        /// <c>Trailing[i]</c>.Left 是第 i 个字符的右边缘，且不会跳到下一个视觉行 ——
        /// 行尾字符的右边缘只能由它得到（用 Leading[i+1] 会跨行，导致高亮少一个字）。
        /// </summary>
        public Rect[] Trailing = Array.Empty<Rect>();
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

        // 🔴 变换矩阵必须每次核对：布局位移（行增删、换行回流、内边距/字号变化、
        //    滚动、DPI 变化）会让 TextBox 相对装饰器整体平移，而它的引用、文本、
        //    宽度都可能纹丝不动 —— 此时全部旧判据都会通过，高亮却画在老位置上，
        //    表现为「越用越飘、色带整体偏移、行末超出文字」。
        //    这也是「刚启动正常、用一会儿才乱」的直接成因：缓存是随使用逐步建立的。
        var transformsFresh = EnsureTransformsFresh(ranges, adorner);

        // 几何未变 + 端点未变 + 变换矩阵未漂移 → 直接复用已算好的矩形
        if (transformsFresh &&
            IsValid(boxes, scrollOffset, editorWidth, editorHeight) &&
            SameRanges(ranges))
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
            if (charRects.Leading.Length == 0) continue;

            // 按视觉行分段：几何计算交给 LineSelectionGeometry（纯函数、可测），
            // 这里只负责把结果搬到装饰器坐标系。
            var rows = LineSelectionGeometry.BuildRowRects(
                charRects.Leading,
                charRects.Trailing,
                s,
                e,
                box.ActualHeight);

            foreach (var row in rows)
            {
                AddRow(rects, matrix, row.X, row.X + row.Width, row.Y, row.Height);
            }
        }

        _rects = rects;
        Snapshot(boxes, scrollOffset, editorWidth, editorHeight);
        MarkRanges(ranges);
        return rects;
    }

    /// <summary>
    /// 记录本次端点集合，供下次 <see cref="IsValid"/> 判断是否可直接复用 _rects。
    /// 同时记录各行文本的内容指纹 —— 端点相同但文字变过时也不可复用。
    /// </summary>
    public void MarkRanges(IReadOnlyList<(TextBox Box, int Start, int End)> ranges)
    {
        _lastRangesEqual = true;
        _rangesFingerprint = ranges.Count;
        _rangesFirst = ranges.Count > 0 ? ranges[0].Start : -1;
        _rangesLast = ranges.Count > 0 ? ranges[^1].End : -1;
        _rangesContentHash = ComputeContentHash(ranges);
    }

    /// <summary>
    /// 选区各行文本的内容指纹。**必须包含文本内容本身**（不是长度）：
    /// 只比长度时，「改字后字数不变」的编辑无法被识别，旧几何会被一直复用。
    /// </summary>
    private static int ComputeContentHash(IReadOnlyList<(TextBox Box, int Start, int End)> ranges)
    {
        var hash = 17;
        foreach (var (box, _, _) in ranges)
        {
            hash = hash * 31 + (box.Text ?? string.Empty).GetHashCode();
        }
        return hash;
    }

    /// <summary>端点指纹是否与已计算结果一致（含文本内容校验）。</summary>
    public bool SameRanges(IReadOnlyList<(TextBox Box, int Start, int End)> ranges)
    {
        if (!_lastRangesEqual) return false;
        if (ranges.Count != _rangesFingerprint) return false;
        if (ComputeContentHash(ranges) != _rangesContentHash) return false;
        if (ranges.Count == 0) return true;
        return ranges[0].Start == _rangesFirst && ranges[^1].End == _rangesLast;
    }

    /// <summary>
    /// 取（并缓存）某行的逐字符插入点矩形表，长度均为 len + 1。
    /// <c>Leading[i]</c>：第 i 个字符之前的插入点；<c>Leading[len]</c> 为行尾插入点。
    /// <c>Trailing[i]</c>：第 i 个字符之后的插入点，**停留在第 i 个字符所属的视觉行内**。
    ///
    /// 两个数组都必须走原生接口，不能手工拼接：
    /// * 少一个前插入点会让「选区起点落在行尾」时算不出可见矩形；
    /// * 少一个后插入点会让每个折行视觉行的高亮**少掉最后一个字符**
    ///   （这正是历史缺陷 «选中大段文字时强调色错乱» 的根因）——
    ///   WPF 的 <c>GetRectFromCharacterIndex(i, true)</c> 在换行处不会跳到下一行，
    ///   而 <c>GetRectFromCharacterIndex(i + 1)</c> 会，两者语义不同、不可互相替代。
    /// </summary>
    private CharRects GetCharRects(TextBox box, int len)
    {
        var text = box.Text ?? string.Empty;

        // 🔴 内容必须参与判定：长度相同的不同文字，插入点位置完全不同。
        // 只比 len/字号/宽度会命中陈旧表（用户改字后「用久了就乱」的根因）。
        if (_charRects.TryGetValue(box, out var cached) &&
            cached.TextLength == len &&
            cached.FontSize == box.FontSize &&
            cached.Width == box.ActualWidth &&
            string.Equals(cached.Text, text, StringComparison.Ordinal))
        {
            return cached;
        }

        var leading = new Rect[len + 1];
        var trailing = new Rect[len + 1];
        for (var i = 0; i < len; i++)
        {
            leading[i] = box.GetRectFromCharacterIndex(i);
            trailing[i] = box.GetRectFromCharacterIndex(i, true);
        }

        // 行尾插入点：优先用原生查询（WPF 会给出该视觉行末尾的真实插入点矩形），
        // 取不到时再退回「末字符右移」的保守近似，并保证高度非零。
        var tail = box.GetRectFromCharacterIndex(len);
        if (tail.Height <= 0 && tail.Width <= 0)
        {
            var last = len > 0 ? leading[len - 1] : new Rect(0, 0, 0, box.ActualHeight);
            var h = last.Height > 0 ? last.Height : box.ActualHeight;
            // 用一个空文本测量出的字宽作为行尾插入点宽度，避免出现零宽矩形
            var w = MeasureSpaceWidth(box);
            tail = new Rect(last.Right, last.Top, w, h);
        }
        leading[len] = tail;

        var tailTrailing = box.GetRectFromCharacterIndex(len, true);
        if (tailTrailing.Height <= 0 && tailTrailing.Width <= 0)
        {
            tailTrailing = tail;
        }
        trailing[len] = tailTrailing;

        var entry = new CharRects
        {
            TextLength = len,
            FontSize = box.FontSize,
            Width = box.ActualWidth,
            Text = text,
            Leading = leading,
            Trailing = trailing,
        };
        _charRects[box] = entry;
        return entry;
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
        _rangesContentHash = -1;
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

    /// <summary>
    /// 核对（必要时刷新）本次选区涉及的所有 TextBox 的变换矩阵。
    /// </summary>
    /// <returns>所有矩阵都与实时布局一致时为 true；发现并刷新了漂移则为 false（调用方须重算）。</returns>
    private bool EnsureTransformsFresh(
        IReadOnlyList<(TextBox Box, int Start, int End)> ranges,
        Visual adorner)
    {
        var allFresh = true;
        foreach (var (box, _, _) in ranges)
        {
            if (!TryGetTransform(box, adorner, out var cached)) continue;

            if (!TryReadLiveTransform(box, adorner, out var live))
            {
                // 取不到实时矩阵（尚未挂上视觉树）：保留旧值，但本轮视为需重算
                allFresh = false;
                continue;
            }

            // 判据覆盖平移与缩放：任意一项漂移都必须重算。
            if (!NearlyEqual(cached.OffsetX, live.OffsetX) ||
                !NearlyEqual(cached.OffsetY, live.OffsetY) ||
                !NearlyEqual(cached.M11, live.M11) ||
                !NearlyEqual(cached.M22, live.M22))
            {
                _transforms[box] = live;
                allFresh = false;
            }
        }

        return allFresh;
    }

    /// <summary>浮点容差比较：亚像素差异不必触发重算。</summary>
    private static bool NearlyEqual(double a, double b) => Math.Abs(a - b) < 0.01;

    /// <summary>读取 TextBox 相对装饰器的实时变换矩阵；未挂上视觉树时返回 false。</summary>
    private static bool TryReadLiveTransform(TextBox box, Visual adorner, out Matrix matrix)
    {
        matrix = default;
        try
        {
            if (PresentationSource.FromVisual(box) is null) return false;
            if (box.TransformToVisual(adorner) is not MatrixTransform mt) return false;
            matrix = mt.Matrix;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private bool TryGetTransform(TextBox box, Visual adorner, out Matrix matrix)
    {
        if (_transforms.TryGetValue(box, out matrix)) return true;
        if (!TryReadLiveTransform(box, adorner, out matrix)) return false;

        PruneDetachedAdornerCaches();
        _transforms[box] = matrix;
        return true;
    }

    /// <summary>
    /// 剔除已脱离视觉树的 TextBox 缓存条目。
    /// 行增删 / 容器回收会不断产生新的 TextBox 引用，若不清理，
    /// <see cref="_transforms"/> 与 <see cref="_charRects"/> 会在长时使用中无限增长
    /// （键是控件引用，字典持有它们会阻止回收）。
    /// </summary>
    private void PruneDetachedAdornerCaches()
    {
        // 值很小（选区涉及的行数）时不必付这份开销，阈值内直接跳过
        if (_transforms.Count < DetachPruneThreshold) return;

        var dead = new List<TextBox>();
        foreach (var box in _transforms.Keys)
        {
            if (PresentationSource.FromVisual(box) is null) dead.Add(box);
        }
        foreach (var box in dead)
        {
            _transforms.Remove(box);
            _charRects.Remove(box);
        }
    }

    /// <summary>触发清理的缓存规模阈值。</summary>
    private const int DetachPruneThreshold = 64;

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

