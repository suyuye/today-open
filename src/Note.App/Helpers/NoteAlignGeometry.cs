using System.Windows;

namespace NoteApp.Helpers;

/// <summary>
/// 便签之间的「边缘对齐吸附」几何计算（纯函数，无任何 WPF 窗口依赖）。
///
/// **要解决的问题**：桌面上摆了几张便签，手动把它们摆整齐几乎不可能 ——
/// 左右差 3px、上下差 2px，肉眼看得出来但抓不住。于是当两张便签的边
/// 接近齐平时，把被拖动的这张「拉」到完全对齐的位置，手感上表现为明显的停顿感。
///
/// **为什么做成静态纯函数**：
/// 1. **可单测**。吸附算错的表现是「便签拖着拖着突然跳到奇怪的位置」，
///    而它完全由坐标算术决定 —— 正好适合用断言把各种边界钉死。
/// 2. **形态无关**。常规便签与折叠胶囊只是矩形尺寸不同，
///    对齐本身只看矩形，不需要知道对方是什么形态。
///
/// **坐标系约定**：全部是 WPF 的设备无关单位（DIP），与 <see cref="Window.Left"/> /
/// <see cref="Window.Top"/> 一致。调用方负责把窗口外框换算成「可见内容矩形」再传进来
/// —— 阴影是透明的，拿着含阴影的外框去对齐，两张便签的**可见边**之间会永远差一个边距。
/// </summary>
public static class NoteAlignGeometry
{
    /// <summary>
    /// 边缘对齐的吸附距离阈值（DIP）。
    ///
    /// 取 8：比贴边的 <see cref="DockGeometry.SnapDistance"/>(28) 小得多，
    /// 因为这是「便签与便签」之间的对齐 —— 桌面上便签可能挨得很密，
    /// 阈值太大就会在还没靠近时就"抢"过去，反而失去精细摆放的自由度。
    /// 实测 8px 大约对应 150% DPI 下的 12 物理像素，眼睛能看出"快齐了"的距离。
    /// </summary>
    public const double AlignSnapDistance = 8;

    /// <summary>
    /// 一次边缘对齐的吸附结果。
    /// </summary>
    /// <param name="Dx">横向吸附偏移量：把被拖便签的左边界加上它即可对齐（0 = 不吸附）。</param>
    /// <param name="Dy">纵向吸附偏移量。</param>
    /// <param name="AlignedX">是否发生了横向对齐（用于决定要不要显示对齐提示线）。</param>
    /// <param name="AlignedY">是否发生了纵向对齐。</param>
    /// <param name="GuideLeft">横向对齐提示线的 X 坐标（仅当 <paramref name="AlignedX"/> 为真时有意义）。</param>
    /// <param name="GuideTop">纵向对齐提示线的 Y 坐标。</param>
    public readonly record struct AlignResult(
        double Dx,
        double Dy,
        bool AlignedX,
        bool AlignedY,
        double GuideLeft,
        double GuideTop)
    {
        /// <summary>没有任何对齐。</summary>
        public static AlignResult None => new(0, 0, false, false, 0, 0);
    }

    /// <summary>
    /// 计算被拖便签与其它便签之间的边缘对齐吸附。
    ///
    /// **只吸附最近的一个**：X 轴与 Y 轴各自独立地挑出「偏移量绝对值最小」的候选。
    /// 这样既保证排列规整，又不会出现多张便签同时拉扯、让人感觉粘住的怪异手感。
    ///
    /// **对齐的关系是「边对边」**：左对左、右对右、上对上、下对下 ——
    /// 不做中线对齐，也不做「紧挨着拼合」（那两种是另外的语义，混在一起判定会打架）。
    ///
    /// </summary>
    /// <param name="dragged">被拖动便签的可见内容矩形（不含透明阴影边距）。</param>
    /// <param name="others">桌面上其它便签的可见内容矩形（不含透明阴影边距）。</param>
    /// <param name="threshold">吸附阈值，默认 <see cref="AlignSnapDistance"/>。</param>
    public static AlignResult Compute(Rect dragged, IReadOnlyList<Rect> others, double threshold = AlignSnapDistance)
    {
        if (others is null || others.Count == 0)
        {
            return AlignResult.None;
        }

        // 各自记录「当前找到的最近候选」：偏移量绝对值 + 对齐后的坐标（画提示线用）
        double bestDx = 0, bestDxAbs = double.MaxValue, guideLeft = 0;
        double bestDy = 0, bestDyAbs = double.MaxValue, guideTop = 0;

        foreach (var other in others)
        {
            // ---- 横向：左对左、右对右 ----
            // 左对左：把 dragged.Left 拉到 other.Left
            ConsiderAlign(dragged.Left, other.Left, ref bestDx, ref bestDxAbs, ref guideLeft);
            // 右对右：把 dragged.Right 拉到 other.Right
            ConsiderAlign(dragged.Right, other.Right, ref bestDx, ref bestDxAbs, ref guideLeft);

            // ---- 纵向：上对上、下对下 ----
            ConsiderAlign(dragged.Top, other.Top, ref bestDy, ref bestDyAbs, ref guideTop);
            ConsiderAlign(dragged.Bottom, other.Bottom, ref bestDy, ref bestDyAbs, ref guideTop);
        }

        var alignedX = bestDxAbs <= threshold;
        var alignedY = bestDyAbs <= threshold;

        return new AlignResult(
            alignedX ? bestDx : 0,
            alignedY ? bestDy : 0,
            alignedX,
            alignedY,
            guideLeft,
            guideTop);
    }

    /// <summary>
    /// 拿「被拖边」与「目标边」比一次：把偏移量 (target - current) 与当前最优比较，
    /// 更近就替换。抽出来是因为横向两条边、纵向两条边共四次，逻辑完全一样。
    ///
    /// ⚠️ 只在**未超过阈值**时才接受候选会让提示线与吸附判定各算一套；
    /// 这里一律记录全局最优，最后由调用方用阈值一次性裁定 —— 保证
    /// 「提示线出现的位置」与「实际吸附的位置」永远一致。
    /// </summary>
    private static void ConsiderAlign(
        double current, double target,
        ref double bestDelta, ref double bestAbs, ref double guide)
    {
        var delta = target - current;
        var abs = Math.Abs(delta);
        if (abs < bestAbs)
        {
            bestAbs = abs;
            bestDelta = delta;
            guide = target;
        }
    }
}
