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
        _cache.Invalidate();
    }

    /// <summary>
    /// 布局变化时按需重绘：无选区直接返回（关键修复点，原实现此处无条件重绘导致死循环）。
    /// </summary>
    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_ranges.Count == 0) return;
        var signature = ComputeSignature();
        if (_hasDrawn && signature == _lastSignature) return;
        InvalidateVisual();
    }

    public void SetRanges(List<(TextBox Box, int Start, int End)>? ranges)
    {
        var next = ranges ?? new List<(TextBox Box, int Start, int End)>();

        // 与当前选区等价时不做任何事：这是拖选过程中的主要开销来源，
        // 每次鼠标移动都会调用本方法，无脑 InvalidateVisual 会引发重绘风暴。
        if (SameRanges(_ranges, next)) return;

        _ranges = next;
        _hasDrawn = false;

        // 在此就把绘制矩形算好，让 OnRender 退化为纯绘制。
        // 这样即便渲染管线多次回调 OnRender，也不会重复跑几何计算。
        _cachedRects = _ranges.Count == 0
            ? _noRects
            : ComputeRects();

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
    /// 几何签名：选区区间、行内容/字号、滚动位置、编辑区尺寸任一变化才需要重绘。
    /// 这是抑制重绘风暴的第二道闸门（第一道是「无选区直接返回」）。
    /// </summary>
    private SelectionSignature ComputeSignature()
    {
        var textHash = 17;
        foreach (var (box, start, end) in _ranges)
        {
            textHash = textHash * 31 + start;
            textHash = textHash * 31 + end;
            textHash = textHash * 31 + (box.Text?.Length ?? 0);
            textHash = textHash * 31 + box.FontSize.GetHashCode();
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

