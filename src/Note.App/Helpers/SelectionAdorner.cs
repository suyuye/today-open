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
/// 跨行选区高亮装饰器：直接在行容器上绘制选区矩形（按视觉行分段，支持换行行），
/// 不依赖 TextBox 的非焦点选区渲染，任意行、任意焦点状态都保证显示。
///
/// 性能要点（v1.1.1 修复）：
/// 1) 原实现在 LayoutUpdated 里无条件 InvalidateVisual()。LayoutUpdated 每个布局周期
///    （含纯渲染周期）都会触发，而 InvalidateVisual 会再次引发布局 → 形成自激循环，
///    导致 UI 线程持续 100% 占用、界面卡顿无响应。现改为：无选区时完全不重绘，
///    有选区时仅当几何签名变化才重绘。
/// 2) 原实现每次重绘都跑 GetRectFromCharacterIndex 逐字符循环 + 逐行 TransformToVisual。
///    现由 SelectionGeometryCache 缓存矩形结果，重绘只做画矩形。
/// </summary>
internal sealed class SelectionAdorner : Adorner
{
    private List<(TextBox Box, int Start, int End)> _ranges = new();
    private readonly Brush _brush;
    private readonly ItemsControl _adorned;
    private readonly ScrollViewer? _scrollViewer;
    private readonly SelectionGeometryCache _cache = new();

    // 上一次绘制所用的几何签名：只有签名变化才重绘，避免无意义的重绘风暴
    private SelectionSignature _lastSignature;
    private bool _hasDrawn;

    /// <summary>当前选区对应的绘制矩形，在 SetRanges 时算好，OnRender 只贴图。</summary>
    private List<Rect> _cachedRects = _noRects;

    /// <summary>
    /// 上次计算时各选区行的文本内容（按 TextBox 引用记录）。
    /// 用于识别「区间没变但文字变了」——这类变化同样必须让高亮重算。
    /// </summary>
    private readonly Dictionary<TextBox, string> _lastRangeText = new();

    /// <summary>
    /// 上次计算时各选区行相对本装饰器的原点。
    /// **必须参与提前返回判定**：行增删 / 换行回流 / 内边距或字号变化会让整行平移，
    /// 而 TextBox 引用、文本、宽度可能一丝未变 —— 只比区间与文字就会误判「无需重算」，
    /// 于是高亮停在旧位置（用户看到的「用久了越飘、色带整体偏移、行末超出文字」）。
    /// </summary>
    private readonly Dictionary<TextBox, Point> _lastRangeOrigin = new();

    private static readonly List<Rect> _noRects = new();

    public SelectionAdorner(ItemsControl adornedElement, ScrollViewer? scrollViewer) : base(adornedElement)
    {
        _brush = new SolidColorBrush(Color.FromRgb(0xE8, 0x74, 0x3B)) { Opacity = 0.25 };
        _brush.Freeze(); // 冻结后可跨线程/被渲染层直接复用，减少开销
        IsHitTestVisible = false;
        _adorned = adornedElement;
        _scrollViewer = scrollViewer;

        adornedElement.LayoutUpdated += OnLayoutUpdated;
    }

    /// <summary>
    /// 退订布局事件。编辑器宿主窗口关闭时调用，避免装饰器把宿主
    /// ItemsControl 通过事件引用长期钉住，也避免窗口销毁后仍收到布局回调。
    /// </summary>
    public void Detach()
    {
        _adorned.LayoutUpdated -= OnLayoutUpdated;
        _ranges.Clear();
        _cachedRects = _noRects;
        _lastRangeText.Clear();
        _lastRangeOrigin.Clear();
        _cache.Invalidate();
    }

    /// <summary>
    /// 行集合发生结构性变化（增删行 / 容器重建）后调用：
    /// 行的垂直位置整体位移，缓存的变换矩阵与字符插入点表全部作废。
    /// 只清几何缓存，不动选区状态（拖选中途换行不应丢选区）。
    /// </summary>
    public void InvalidateGeometry()
    {
        _cache.Invalidate();
        _lastRangeText.Clear();
        _lastRangeOrigin.Clear();
        _cachedRects = _noRects;
        _hasDrawn = false;
        _lastSignature = default;
    }

    /// <summary>
    /// 布局变化时按需重绘：无选区直接返回（关键修复点，原实现此处无条件重绘导致死循环）。
    ///
    /// 🔴 若布局位移导致选区各行位置改变，必须先**重算矩形**再重绘 ——
    /// 否则 OnRender 只是把旧矩形画一遍，位置依旧是错的。
    /// 这正是「不移动鼠标、只在上面增删一行，高亮就飘掉」的成因。
    /// </summary>
    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_ranges.Count == 0) return;

        if (!RangeOriginUnchanged())
        {
            _cachedRects = ComputeRects();
            SnapshotRangeText();
            SnapshotRangeOrigin();
            _hasDrawn = false;
        }

        var signature = ComputeSignature();
        if (_hasDrawn && signature == _lastSignature) return;
        InvalidateVisual();
    }

    public void SetRanges(List<(TextBox Box, int Start, int End)>? ranges)
    {
        var next = ranges ?? new List<(TextBox Box, int Start, int End)>();

        // 与当前选区等价时不做任何事：这是拖选过程中的主要开销来源，
        // 每次鼠标移动都会调用本方法，无脑 InvalidateVisual 会引发重绘风暴。
        //
        // 🔴 但「区间相同」不等于「几何相同」，有两种情形都会让几何变化而区间不变：
        //    ① 用户改字后字数不变 —— 插入点位置已全变（靠 ContentUnchanged 识别）；
        //    ② 行在装饰器中的位置发生位移（顶部增删行、换行回流、内边距/字号变化）——
        //       文字与区间都丝毫未动（靠 RangeOriginUnchanged 识别）。
        //    缺任一判据都会复用旧矩形，表现为「刚启动正常、用一阵子高亮就飘了」。
        if (SameRanges(_ranges, next) && ContentUnchanged() && RangeOriginUnchanged())
        {
            return;
        }

        _ranges = next;
        _hasDrawn = false;

        // 在此就把绘制矩形算好，让 OnRender 退化为纯绘制。
        // 这样即便渲染管线多次回调 OnRender，也不会重复跑几何计算。
        _cachedRects = _ranges.Count == 0
            ? _noRects
            : ComputeRects();

        SnapshotRangeText();
        SnapshotRangeOrigin();

        // 空集合时重绘一次以擦除上次高亮；有选区时只画已算好的矩形
        InvalidateVisual();
    }

    /// <summary>按当前选区计算绘制矩形（仅在选区实际变化时调用）。</summary>
    private List<Rect> ComputeRects()
    {
        var boxes = new List<TextBox>(_ranges.Count);
        foreach (var (box, _, _) in _ranges)
        {
            boxes.Add(box);
        }

        return _cache.GetRects(
            _ranges,
            boxes,
            this,
            _scrollViewer?.VerticalOffset ?? 0,
            _adorned.ActualWidth,
            _adorned.ActualHeight);
    }

    /// <summary>行区间与字符区间完全一致时视为同一选区。</summary>
    private static bool SameRanges(
        List<(TextBox Box, int Start, int End)> a,
        List<(TextBox Box, int Start, int End)> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!ReferenceEquals(a[i].Box, b[i].Box) ||
                a[i].Start != b[i].Start ||
                a[i].End != b[i].End)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 内容是否与上次计算时一致。**区间相同但文字变了**（用户改字后字数恰好不变）
    /// 时，<see cref="SetRanges"/> 不可提前返回 —— 否则旧的高亮矩形会被一直复用，
    /// 表现为「用久了之后高亮错位、光标落点不准」（重启后缓存为空所以又好一阵子）。
    /// </summary>
    private bool ContentUnchanged()
    {
        foreach (var (box, _, _) in _ranges)
        {
            if (!_lastRangeText.TryGetValue(box, out var prev)) return false;
            if (!string.Equals(prev, box.Text ?? string.Empty, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>记录本次各行的文本内容，供 <see cref="ContentUnchanged"/> 比对。</summary>
    private void SnapshotRangeText()
    {
        _lastRangeText.Clear();
        foreach (var (box, _, _) in _ranges)
        {
            _lastRangeText[box] = box.Text ?? string.Empty;
        }
    }

    /// <summary>
    /// 各行相对本装饰器的原点是否与上次计算时一致。
    /// 布局位移（顶部增删行 / 换行回流 / 内边距或字号变化 / DPI 切换）只改位置、
    /// 不改引用与文本，因此必须单独核对，否则高亮会停在旧位置。
    /// </summary>
    private bool RangeOriginUnchanged()
    {
        foreach (var (box, _, _) in _ranges)
        {
            if (!_lastRangeOrigin.TryGetValue(box, out var prev)) return false;
            if (!TryReadOrigin(box, out var now)) return false;
            if (Math.Abs(prev.X - now.X) > 0.01 || Math.Abs(prev.Y - now.Y) > 0.01) return false;
        }
        return true;
    }

    /// <summary>记录本次各行相对本装饰器的原点，供 <see cref="RangeOriginUnchanged"/> 比对。</summary>
    private void SnapshotRangeOrigin()
    {
        _lastRangeOrigin.Clear();
        foreach (var (box, _, _) in _ranges)
        {
            if (TryReadOrigin(box, out var origin))
            {
                _lastRangeOrigin[box] = origin;
            }
        }
    }

    /// <summary>读取 TextBox 相对本装饰器的原点；尚未挂上视觉树时返回 false。</summary>
    private bool TryReadOrigin(TextBox box, out Point origin)
    {
        origin = default;
        try
        {
            if (PresentationSource.FromVisual(box) is null) return false;
            if (box.TransformToVisual(this) is not MatrixTransform mt) return false;
            origin = new Point(mt.Matrix.OffsetX, mt.Matrix.OffsetY);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_cachedRects.Count == 0)
        {
            _lastSignature = default;
            _hasDrawn = false;
            return;
        }

        // 矩形已在 SetRanges 算好，这里只负责画
        foreach (var rect in _cachedRects)
        {
            drawingContext.DrawRectangle(_brush, null, rect);
        }

        _lastSignature = ComputeSignature();
        _hasDrawn = true;
    }

    /// <summary>
    /// 几何签名：选区区间、行内容/字号、**各行位置**、滚动位置、编辑区尺寸任一变化才需要重绘。
    /// 这是抑制重绘风暴的第二道闸门（第一道是「无选区直接返回」）。
    /// </summary>
    private SelectionSignature ComputeSignature()
    {
        var textHash = 17;
        foreach (var (box, start, end) in _ranges)
        {
            textHash = textHash * 31 + start;
            textHash = textHash * 31 + end;
            // 🔴 必须用文本**内容**参与哈希，不能只用长度：
            // 用长度时，「改字后字数不变」的编辑不会改变签名 →
            // OnLayoutUpdated 判定「无需重绘」→ 高亮停留在旧几何。
            textHash = textHash * 31 + (box.Text ?? string.Empty).GetHashCode();
            textHash = textHash * 31 + box.FontSize.GetHashCode();
            // 🔴 行位置也要入签名：顶部增删行 / 换行回流会让整行平移，
            // 而区间、文本、字号全都没变 —— 不入签名就判定「无需重绘」。
            if (TryReadOrigin(box, out var origin))
            {
                textHash = textHash * 31 + origin.X.GetHashCode();
                textHash = textHash * 31 + origin.Y.GetHashCode();
            }
        }

        return new SelectionSignature(
            _ranges.Count,
            _scrollViewer?.VerticalOffset ?? 0,
            _adorned.ActualWidth,
            _adorned.ActualHeight,
            textHash);
    }

    /// <summary>重绘判据：值语义比较，任一字段变化即需要重绘。</summary>
    private readonly record struct SelectionSignature(
        int RangeCount,
        double Scroll,
        double Width,
        double Height,
        int TextHash);
}

