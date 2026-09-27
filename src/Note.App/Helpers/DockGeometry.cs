using System.Windows;

using NoteApp.Models.Enums;

namespace NoteApp.Helpers;

/// <summary>
/// 一块显示器工作区的四条边中，哪些是**整个虚拟桌面的真实外边界**。
///
/// 为什么需要这个概念：多屏拼接时，相邻两块屏之间会形成一条「接缝」（screenshot 里的中间那条线）。
/// 接缝**不是**可以贴边隐藏的边缘 —— 把便签收进接缝，等于把它藏到「两块屏之间那条看不见的线」里，
/// 用户既看不到、也不好找回来，而且视觉上它就"跳"到了桌面中间。
///
/// 所以贴边判定必须额外知道：这条边外面还有没有别的显示器。
/// 本枚举由 <see cref="MonitorHelper"/> 通过比较「该屏显示区」与「所有显示器并集」算出，
/// 再由 <see cref="DockGeometry.DetectEdge(double,double,Rect,double,double,DesktopSides)"/> 用作门控。
/// 之所以做成标志位而不是内联布尔，是因为将来若支持「上下贴边」，
/// 只需在 <see cref="DetectEdge"/> 里多用两个位，接口形状不用再改。
/// </summary>
[Flags]
public enum DesktopSides
{
    /// <summary>四条边都不是真外边界（例如被四块屏围在中间的屏）。</summary>
    None = 0,

    /// <summary>本屏左外侧没有别的显示器 → 左贴边有效。</summary>
    Left = 1,

    /// <summary>本屏右外侧没有别的显示器 → 右贴边有效。</summary>
    Right = 2,

    /// <summary>本屏上外侧没有别的显示器。当前贴边只做左右，留作将来扩展。</summary>
    Top = 4,

    /// <summary>本屏下外侧没有别的显示器。当前贴边只做左右，留作将来扩展。</summary>
    Bottom = 8,

    /// <summary>单屏机器：四条边都是真外边界。</summary>
    All = Left | Right | Top | Bottom,
}

/// <summary>
/// 贴边自动隐藏的几何计算（纯函数，不含任何 WPF 窗口依赖）。
///
/// 特意把这段逻辑做成静态纯函数而不是塞进窗口代码里，有两个原因：
/// 1. **可单测**。贴边收起的坐标算错，表现是「便签贴边后整条消失、再也点不到」，
///    这是最容易让用户以为数据丢了的一类 bug；而它完全由坐标算术决定，
///    正好适合用断言把各种边界（超宽便签、屏幕负坐标、DPI 缩放）钉死。
/// 2. **两处规则必须一致**。便签窗口是唯一的调用方，但将来若给「贴边便签」
///    做统一的唤出热键，也需要同一套坐标规则；集中一处就不会分叉。
///
/// 坐标系约定：全部使用 WPF 的设备无关单位（DIP），与 <see cref="Window.Left"/> /
/// <see cref="Window.Top"/> 一致 —— 由调用方负责把 Win32 的物理像素换算好。
/// </summary>
public static class DockGeometry
{
    /// <summary>
    /// 判定「贴边」的距离阈值：松手时便签距屏幕边缘不超过此值就算贴上去。
    ///
    /// ⚠️ 这个值**必须明显大于"看起来已经贴住"的距离**，否则手感极差。
    /// 原因：窗口外框含 <c>edgeMargin</c>(12) 的透明阴影，用户眼里的"贴住屏幕左边"
    /// 对应的是 <c>left = -edgeMargin</c>，而一旦继续往左拖就会拖出屏幕
    /// （可见内容被裁，用户不敢再拖）。所以**用户可感知的拖动范围**其实只有
    /// 「可见内容距边缘 0 ~ 十几像素」这一小段，阈值若贴着这段的上沿，
    /// 用户就会觉得"怎么都找不到那个刚刚好的距离"。
    ///
    /// 早期取值 16（对应可见内容距边缘 0~16px）实测偏窄，现放宽到 28。
    /// </summary>
    public const double SnapDistance = 28;

    /// <summary>
    /// 拖动过程中的「吸附预览」阈值：比 <see cref="SnapDistance"/> 更宽，
    /// 用于在用户**还在拖**的时候就告诉他「松手会贴上去」。
    ///
    /// 为什么预览带要更宽：预览只是给反馈、不改变最终行为，放宽没有风险；
    /// 而用户需要在"快到了"的早期就得到反馈，才能放心把便签拖到边缘。
    /// </summary>
    public const double SnapPreviewDistance = SnapDistance + 18;

    /// <summary>收起后留在屏幕上的可见宽度（唤醒条）。太窄会点不到，太宽则失去「隐藏」的意义。</summary>
    public const double VisibleStripWidth = 5;

    /// <summary>
    /// 判断松手位置是否应当贴边。
    /// </summary>
    /// <param name="windowLeft">窗口左边界（DIP）。</param>
    /// <param name="windowWidth">窗口宽度（DIP，含阴影边距）。</param>
    /// <param name="workArea">当前显示器工作区（已排除任务栏）。</param>
    /// <param name="edgeMargin">阴影边距：窗口外框比可见内容多出的部分，需从可见位置中扣掉。</param>
    public static DockEdge DetectEdge(double windowLeft, double windowWidth, Rect workArea, double edgeMargin = 0)
        => DetectEdge(windowLeft, windowWidth, workArea, edgeMargin, SnapDistance, DesktopSides.All);

    /// <summary>
    /// 与 <see cref="DetectEdge(double,double,Rect,double)"/> 同逻辑，但可指定阈值。
    ///
    /// 拖动过程中的「吸附预览」调用它并传 <see cref="SnapPreviewDistance"/>，
    /// **复用同一段判断代码**，避免预览说"能吸"、松手后却真的没吸这种自相矛盾。
    /// </summary>
    public static DockEdge DetectEdge(
        double windowLeft, double windowWidth, Rect workArea, double edgeMargin, double threshold)
        => DetectEdge(windowLeft, windowWidth, workArea, edgeMargin, threshold, DesktopSides.All);

    /// <summary>
    /// 完整的贴边判定：在阈值判断之外，还要求该边是**整个桌面的真实外边界**。
    ///
    /// <paramref name="outerSides"/> 描述本屏工作区的四条边里哪些外面没有别的显示器。
    /// 接缝上的边（外面还连着别的屏）**不允许贴边**：
    /// 在那种地方收起便签，等于把它藏进两块屏之间那条看不见的缝里 ——
    /// 用户看到的现象就是"便签贴过去之后跳到了桌面中间"，而且从此找不回来。
    ///
    /// 单屏机器传 <see cref="DesktopSides.All"/>，行为与早期版本完全一致。
    /// </summary>
    public static DockEdge DetectEdge(
        double windowLeft, double windowWidth, Rect workArea, double edgeMargin, double threshold,
        DesktopSides outerSides)
    {
        // 换算成「可见内容」的左右边界：阴影是透明的，用户眼里贴边的其实是内容边缘
        double visibleLeft = windowLeft + edgeMargin;
        double visibleRight = windowLeft + windowWidth - edgeMargin;

        // 两侧都在阈值内时取**更近**的一侧。
        // 早期实现是先判左再判右，窗口比工作区还宽时会在中间位置被判成左贴边，
        // 而它其实离右边更近 —— 表现为"想贴右边结果跳到了左边"。
        double distLeft = visibleLeft - workArea.Left;
        double distRight = workArea.Right - visibleRight;

        // 只有「真外边界」的一侧才参与贴边；接缝一侧直接无视，
        // 于是拖到接缝附近会被判为 None（便签停在原地，不会被收进缝里）。
        bool leftOk = (outerSides & DesktopSides.Left) != 0;
        bool rightOk = (outerSides & DesktopSides.Right) != 0;

        if (leftOk && distLeft <= threshold && (!rightOk || distLeft <= distRight))
        {
            return DockEdge.Left;
        }

        if (rightOk && distRight <= threshold)
        {
            return DockEdge.Right;
        }

        return DockEdge.None;
    }

    /// <summary>
    /// 贴边后「完全展开」时窗口左边界应当落在哪里。
    /// 左贴边时左边界对齐工作区左侧；右贴边时右边界对齐工作区右侧。
    /// </summary>
    public static double ExpandedLeft(DockEdge edge, double windowWidth, Rect workArea, double edgeMargin = 0)
    {
        return edge switch
        {
            DockEdge.Left => workArea.Left - edgeMargin,
            DockEdge.Right => workArea.Right - windowWidth + edgeMargin,
            _ => workArea.Left - edgeMargin,
        };
    }

    /// <summary>
    /// 贴边后「收起」时窗口左边界。
    ///
    /// 关键约束：**必须始终留下 <see cref="VisibleStripWidth"/> 的可见宽度**，
    /// 绝不能把窗口整体推到屏幕外。一旦完全移出，鼠标就再也碰不到它，
    /// 用户会认为「便签被我弄丢了」——而这类 bug 从界面上完全看不出原因。
    /// </summary>
    public static double CollapsedLeft(DockEdge edge, double windowWidth, Rect workArea, double edgeMargin = 0)
    {
        return edge switch
        {
            // 左贴边：把窗口往左推，只留一条细边在屏幕内
            DockEdge.Left => workArea.Left - windowWidth + edgeMargin + VisibleStripWidth,

            // 右贴边：把窗口往右推，只留一条细边在屏幕内
            DockEdge.Right => workArea.Right - VisibleStripWidth - edgeMargin,

            _ => workArea.Left - edgeMargin,
        };
    }

    /// <summary>
    /// 指针的横向位置是否落在「唤醒带」内 —— 收起态下用于判断该不该滑出。
    ///
    /// 唤醒带比可见条本身**宽一些**，这是刻意的：可见条只有 5px，
    /// 要求用户精确地把鼠标停在那 5px 上是很糟的手感，所以向外扩出一个容错区。
    /// 容错区只朝屏幕内侧扩 —— 朝屏幕外的一侧没有像素可移动，扩了也没用。
    ///
    /// 纵向判定不在这里做：调用方才知道窗口的 Top/Height，由它补一句纵向范围检查即可。
    /// 这样这个函数只负责「横向」这一件容易算错的事。
    /// </summary>
    public static bool IsInAwakenZone(DockEdge edge, Rect workArea, double pointerX)
    {
        return edge switch
        {
            DockEdge.Left => pointerX <= workArea.Left + VisibleStripWidth + AwakenTolerance,
            DockEdge.Right => pointerX >= workArea.Right - VisibleStripWidth - AwakenTolerance,
            _ => false,
        };
    }

    /// <summary>
    /// 唤醒带朝屏幕内侧额外扩展的宽度（DIP）。
    ///
    /// 5px 的可见条 + 36px 容错。早期取 20（即鼠标要横向落在 25px 内），
    /// 实测在 150% 缩放下只有约 17 物理像素，用户反馈"要很精准地怼上去"。
    /// 放宽到 36 后横向容错区约 41 DIP / 27 物理像素，甩鼠标过去就能唤出，
    /// 且仍在"屏幕边缘一窄条"的直觉范围内。
    /// </summary>
    public const double AwakenTolerance = 36;

    /// <summary>
    /// 把贴边位置校正到工作区内，避免因为换了显示器、改了分辨率或任务栏位置变化，
    /// 导致便签停留在屏幕外（用户完全看不到，也拖不回来）。
    ///
    /// 注意下限/上限要分别用「窗口外框」坐标和「可见内容」坐标来算 —— 这两个概念差着
    /// 一个 <paramref name="edgeMargin"/>。若统一按可见宽度算下限，会把左贴边收起态的
    /// 窗口再往里推 <c>edgeMargin</c> 那么多；表现是**每次关闭再打开便签都往屏幕内挪一点**，
    /// 开几次就自己滑出来了，而且看起来毫无规律。
    /// </summary>
    public static (double Left, double Top) ClampIntoWorkArea(
        double left, double top, double width, double height, Rect workArea, double edgeMargin,
        double minVisibleWidth = 0)
    {
        // 可见区域（扣掉透明阴影）
        double visibleWidth = Math.Max(1, width - edgeMargin * 2);
        double visibleHeight = Math.Max(1, height - edgeMargin * 2);

        // 屏幕上至少要露出这一点宽度/高度，否则用户抓不住它。
        // 默认要求 VisibleStripWidth*2（比贴边可见条宽一倍），这样"正常摆位"的便签
        // 不会有一半贴在屏幕外；但**贴边收起态**必须传 VisibleStripWidth，
        // 否则每次启动会被往里推 5px，反复开关就自己滑出来了。
        double wanted = minVisibleWidth > 0 ? minVisibleWidth : VisibleStripWidth * 2;
        double minVisible = Math.Min(wanted, visibleWidth);

        // 左下限：窗口外框可以伸到屏幕外，但**可见右边界**至少要露 minVisible。
        // 可见右边界 = left + width - edgeMargin，令它 >= workArea.Left + minVisible
        //          → left >= workArea.Left + minVisible - width + edgeMargin
        double minLeft = workArea.Left + minVisible - width + edgeMargin;

        // 右上限：**可见左边界**（left + edgeMargin）至多到 workArea.Right - minVisible
        double maxLeft = workArea.Right - minVisible - edgeMargin;

        // 纵向：窗口外框可向上超出 edgeMargin（阴影），向下同样按可见高度算
        double minTop = workArea.Top - edgeMargin;
        double maxTop = workArea.Bottom - minVisible - edgeMargin;

        // 极窄窗口下 minLeft 可能反而大于 maxLeft，此时以「不越界」为优先
        if (minLeft > maxLeft)
        {
            minLeft = maxLeft = (minLeft + maxLeft) / 2;
        }

        if (minTop > maxTop)
        {
            minTop = maxTop = (minTop + maxTop) / 2;
        }

        return (
            Math.Clamp(left, minLeft, maxLeft),
            Math.Clamp(top, minTop, maxTop));
    }
}
