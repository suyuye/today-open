using System.Windows;

namespace NoteApp.Helpers;

/// <summary>
/// 框选批量拖动时「哪些便签被框住」的命中判定（纯函数）。
///
/// **判定规则：相交即选中**（而不是「完全包含才选中」）。
/// 理由：便签可能比选框还大，要求完全包含就永远选不中它；
/// 而「碰到就算」符合大多数人对框选的直觉，也让大尺寸便签能被轻松选中。
///
/// **被选中便签的拖动量统一**：所有被选中的便签都按同一个位移一起移动，
/// 它们之间的相对位置保持不变 —— 这跟直接选中一堆图标拖动是一样的心智模型。
/// </summary>
public static class MarqueeGeometry
{
    /// <summary>
    /// 由拖动的起止两点构造选框矩形（自动归一化，支持任意方向拖拽）。
    /// </summary>
    public static Rect ToRect(Point start, Point current)
    {
        var left = Math.Min(start.X, current.X);
        var top = Math.Min(start.Y, current.Y);
        var width = Math.Abs(current.X - start.X);
        var height = Math.Abs(current.Y - start.Y);
        return new Rect(left, top, width, height);
    }

    /// <summary>
    /// 选框是否已经足够大，值得视为「一次框选」。
    ///
    /// 门槛的作用：避免把一次普通的桌面单击（鼠标几乎没动）也当成框选，
    /// 那样会立刻清空选择并闪一下选框，手感很差。
    /// </summary>
    public static bool IsSignificantDrag(Point start, Point current, double minSize = 4)
    {
        return Math.Abs(current.X - start.X) >= minSize
               || Math.Abs(current.Y - start.Y) >= minSize;
    }

    /// <summary>
    /// 判断某个便签矩形是否被选框命中（相交即选中）。
    /// </summary>
    /// <param name="marquee">选框矩形（DIP）。</param>
    /// <param name="note">便签的可见内容矩形（DIP，不含透明阴影边距）。</param>
    public static bool HitTest(Rect marquee, Rect note)
    {
        // Rect.IntersectsWith 对「仅边界相切」也返回 true，
        // 这正是我们要的"碰到就算"语义，无需额外处理。
        return marquee.IntersectsWith(note);
    }

    /// <summary>
    /// 从一组候选便签里挑出被选框命中的所有下标（返回下标而非对象，
    /// 方便调用方按自己的数据结构取回窗口实例）。
    /// </summary>
    public static List<int> HitTestAll(Rect marquee, IReadOnlyList<Rect> notes)
    {
        var hits = new List<int>();
        if (notes is null)
        {
            return hits;
        }

        for (var i = 0; i < notes.Count; i++)
        {
            if (HitTest(marquee, notes[i]))
            {
                hits.Add(i);
            }
        }

        return hits;
    }
}
