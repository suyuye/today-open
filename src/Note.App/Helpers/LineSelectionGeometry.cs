using System.Windows;

namespace NoteApp.Helpers;

/// <summary>
/// 视觉行分段：把「一段字符区间」折算成若干条绘制矩形（每条对应一个视觉行）。
///
/// 为什么把它抽成独立纯函数：这段几何原先埋在 <c>SelectionGeometryCache</c> 里，
/// 与 TextBox 视觉查找、变换矩阵、缓存搅在一起，既测不了也看不清，
/// 而它出错的形态极其隐蔽 —— 界面上表现为「一条条长短不一的色带」，
/// 用户根本判断不出自己选中了哪些字，却看不出是几何算错了。
///
/// ## 关键约定（这是本文件存在的全部理由）
///
/// WPF 的 <see cref="System.Windows.Controls.TextBox.GetRectFromCharacterIndex(int)"/>
/// 返回的是**零宽插入点矩形**（<c>Left == Right</c>），不是字符占位矩形。
/// 因此要拿到「第 i 个字符的右边缘」，只能靠相邻插入点，而这里有个坑：
///
/// * 取 <c>leading[i + 1].Left</c> —— 遇到自动换行时会**跳到下一个视觉行的行首**，
///   于是行尾字符的右边缘被算成下一行的行首 x，高亮少掉整整一个字符。
/// * 取 <c>trailing[i].Left</c>（即 <c>GetRectFromCharacterIndex(i, true)</c>）——
///   它规规矩矩停在第 i 个字符所属视觉行的字符右侧，跨行也正确。
///
/// 历史缺陷正是前者：每个折行视觉行的高亮都比实际文字**短一个字**，
/// 文字与高亮边缘永远对不齐，这正是用户反馈「强调色错乱、不知道选中了什么」的根因。
/// </summary>
public static class LineSelectionGeometry
{
    /// <summary>视觉行 Top 变化超过此值即视为换行。</summary>
    private const double WrapTopEpsilon = 0.5;

    /// <summary>宽度小于此值的高亮条视为不可见，直接丢弃。</summary>
    private const double MinVisibleWidth = 0.01;

    /// <summary>
    /// 后插入点完全退化（连字符推进量都拿不到）时给出的兜底字宽。
    /// 用一个明确可见的小值，避免「整行高亮被当成不可见而消失」——
    /// 用户看到一条过窄的高亮，远好过以为没选中。
    /// </summary>
    private const double DegenerateCharWidth = 1.0;

    /// <summary>
    /// 计算字符区间 <c>[start, end)</c> 的绘制矩形，已按视觉行拆分为多条。
    /// </summary>
    /// <param name="leading">
    /// 逐字符「前插入点」矩形，长度 = 文本长度 + 1；<c>leading[i]</c> 是第 i 个字符之前的插入点，
    /// <c>leading[Length]</c> 是行尾插入点。用于定位字符左边缘与判断换行。
    /// </param>
    /// <param name="trailing">
    /// 逐字符「后插入点」矩形，长度与 <paramref name="leading"/> 相同；
    /// <c>trailing[i]</c>.Left 是第 i 个字符的右边缘，且**始终停留在该字符所属的视觉行内**。
    /// </param>
    /// <param name="start">选区起点（含）。</param>
    /// <param name="end">选区终点（不含）。</param>
    /// <param name="fallbackHeight">插入点矩形高度为 0 时使用的兜底行高。</param>
    public static List<Rect> BuildRowRects(
        IReadOnlyList<Rect> leading,
        IReadOnlyList<Rect> trailing,
        int start,
        int end,
        double fallbackHeight)
    {
        var result = new List<Rect>();
        if (leading.Count == 0) return result;

        // leading 长度为 Length + 1，因此合法字符下标是 [0, Length)
        var charCount = leading.Count - 1;
        var s = Math.Clamp(start, 0, charCount);
        var e = Math.Clamp(end, 0, charCount);
        if (s >= e) return result;

        var rowStart = s;
        var top = leading[s].Top;
        var height = ResolveHeight(leading[s], fallbackHeight);

        // 逐字符推进：字符 i 与当前视觉行的 Top 不同，说明 i 已经是新的一行。
        for (var i = s + 1; i < e; i++)
        {
            var cur = leading[i];
            if (cur.Top > top + WrapTopEpsilon)
            {
                // 本视觉行覆盖字符 [rowStart, i)，右边缘取 i-1 的后插入点
                AddRow(result, leading[rowStart].Left, RightEdgeOfChar(leading, trailing, i - 1), top, height);
                rowStart = i;
                top = cur.Top;
                height = ResolveHeight(cur, fallbackHeight);
            }
        }

        // 收尾最后一个视觉行：覆盖字符 [rowStart, e)，至少有一个字符（s < e 已保证）
        AddRow(
            result,
            leading[rowStart].Left,
            RightEdgeOfChar(leading, trailing, e - 1),
            top,
            height);

        return result;
    }

    /// <summary>
    /// 第 <paramref name="index"/> 个字符的右边缘。
    /// 优先用后插入点（跨行也正确）；后插入点缺失时退回「同视觉行的下一个前插入点」；
    /// 仍是零宽（行尾最后一个字符且两个接口都退化）时，给出一个保守的最小可见宽度，
    /// 避免整条高亮被当成不可见而丢掉。
    /// </summary>
    private static double RightEdgeOfChar(
        IReadOnlyList<Rect> leading,
        IReadOnlyList<Rect> trailing,
        int index)
    {
        if (index < 0) return 0;

        if (index < trailing.Count)
        {
            var t = trailing[index];
            if (t.Left > leading[index].Left + MinVisibleWidth)
            {
                return t.Left;
            }

            // 后插入点退化（零宽）：补一个明确可见的兜底字宽
            return leading[index].Left + DegenerateCharWidth;
        }

        // 没有后插入点数据：用同视觉行的下一个前插入点
        var next = index + 1;
        if (next < leading.Count && leading[next].Top <= leading[index].Top + WrapTopEpsilon)
        {
            return leading[next].Left;
        }

        return leading[index].Left + DegenerateCharWidth;
    }

    private static double ResolveHeight(Rect caret, double fallbackHeight) =>
        caret.Height > 0 ? caret.Height : fallbackHeight;

    private static void AddRow(List<Rect> rects, double x0, double x1, double top, double height)
    {
        if (x1 - x0 <= MinVisibleWidth || height <= 0) return;
        rects.Add(new Rect(x0, top, x1 - x0, height));
    }
}
