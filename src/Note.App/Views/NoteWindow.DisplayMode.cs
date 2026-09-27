using System.Windows;
using System.Windows.Media.Animation;

using NoteApp.Models.Enums;

namespace NoteApp.Views;

/// <summary>
/// <see cref="NoteWindow"/> 的形态视觉部分（partial）—— 把 <see cref="NoteDisplayMode"/>
/// 翻译成窗口级的约束（高度 / 最小高度 / 是否可缩放）。
///
/// **为什么单独一个文件**：形态是便签最容易出错的一维状态（曾长期由两个 bool 各自表达，
/// 二者不一致就产出怪状态），窗口层对它的响应又必须与 VM 保持同一条真相源。
/// 单独放一处，便于一眼看全「三种形态分别对窗口做了什么」。
///
/// **核心规则（改这里前必读）**：
/// - 🔴 **窗口高度由 <c>_expandedLogicalHeight</c>（便签记录高度）决定，形态只改
///   「标题栏可不可见 / 边框多厚 / 内边距多少」这类 XAML 层的东西**。
///   极简与常规的**窗口外框尺寸相同**是正确行为 —— 极简省下的标题栏空间被内容区吃掉。
/// - 🔴 **只有折叠态不可缩放**（且高度被压成胶囊）。极简与常规都必须是
///   `CanResizeWithGrip` —— 收口前极简会继承折叠留下的 `NoResize`，表现为"磁贴缩不动"。
/// - 启动路径用 <see cref="ApplyDisplayModeVisualImmediate"/>，**不要**在启动阶段播动画。
/// </summary>
public partial class NoteWindow
{
    /// <summary>
    /// 形态切换驱动窗口高度动画与 ResizeMode。
    ///
    /// 收口前这里是 `ApplyCollapsedVisual(bool collapsed)`，只有「折叠 / 非折叠」两态，
    /// 于是切到极简时窗口**仍带着折叠态留下的固定高度与 NoResize** —— 极简磁贴本该可缩放，
    /// 却缩不动；切回来也可能停在错误高度。现在三种形态各给一套明确的窗口约束。
    /// </summary>
    private void ApplyDisplayModeVisual(NoteDisplayMode mode)
    {
        switch (mode)
        {
            case NoteDisplayMode.Collapsed:
                // 折叠胶囊：固定高度、不可缩放。
                // 先把当前高度记成"展开高度"，这样用户从折叠切回时能回到原来的尺寸
                // （用 Math.Max 兜住"本来就已经是折叠高度"的情况，避免记成 32 而丢高度）。
                _expandedLogicalHeight = Math.Max(Height - ShadowMargin * 2, MinLogicalHeight);
                MinHeight = CollapsedLogicalHeight + ShadowMargin * 2;
                ResizeMode = ResizeMode.NoResize;
                AnimateHeight(CollapsedLogicalHeight + ShadowMargin * 2);
                break;

            default:
                // 常规 / 极简：恢复记录高度与可缩放
                MinHeight = MinLogicalHeight + ShadowMargin * 2;
                ResizeMode = ResizeMode.CanResizeWithGrip;
                AnimateHeight(_expandedLogicalHeight + ShadowMargin * 2);
                break;
        }
    }

    /// <summary>
    /// 不做动画地直接落到目标形态（仅启动时用）。
    ///
    /// 启动阶段不能用 <see cref="AnimateHeight"/>：`AnimationClock` 在窗口尚未
    /// 完成首次布局时行为不可靠，而且用户启动应用时不该看到便签"从错误高度滑过来"。
    /// 直接把 Height 赋值，窗口第一帧就是正确尺寸。
    /// </summary>
    private void ApplyDisplayModeVisualImmediate(NoteDisplayMode mode)
    {
        var target = mode == NoteDisplayMode.Collapsed
            ? CollapsedLogicalHeight
            : _expandedLogicalHeight;
        MinHeight = (mode == NoteDisplayMode.Collapsed ? CollapsedLogicalHeight : MinLogicalHeight)
                    + ShadowMargin * 2;
        ResizeMode = mode == NoteDisplayMode.Collapsed
            ? ResizeMode.NoResize
            : ResizeMode.CanResizeWithGrip;
        Height = target + ShadowMargin * 2;
    }

    /// <summary>200ms ease-in-out 高度动画（设计规范 6.1）。</summary>
    private void AnimateHeight(double target)
    {
        var animation = new DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        BeginAnimation(HeightProperty, animation);
    }
}
