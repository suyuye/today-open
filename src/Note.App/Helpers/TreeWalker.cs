using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace NoteApp.Helpers;

/// <summary>
/// 事件源祖先遍历的安全封装。
/// 🔴 <c>e.OriginalSource</c> 可能是 <see cref="Run"/> 这类 <see cref="FrameworkContentElement"/>：
/// TextBlock 带 Inlines（如列表摘要高亮）时，鼠标命中文字返回的就是 Run，它**不是 Visual**，
/// 直接走 <see cref="VisualTreeHelper.GetParent"/> 会抛 InvalidOperationException
/// （2026-09-27 真实踩过：用户双击列表文字 → 未处理异常，日志见 v1.5.3 体检）。
/// 约定：文本元素走逻辑树（fce.Parent），其余走视觉树；凡从事件源向上爬树一律走本类。
/// </summary>
public static class TreeWalker
{
    /// <summary>取父级：FrameworkContentElement 走逻辑树，其余走视觉树；无父级返回 null。</summary>
    public static DependencyObject? GetParentSafe(DependencyObject? current) => current switch
    {
        null => null,
        FrameworkContentElement fce => fce.Parent,
        _ => VisualTreeHelper.GetParent(current),
    };

    /// <summary>从事件源向上查找指定类型的祖先元素。</summary>
    public static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }
            current = GetParentSafe(current);
        }
        return null;
    }
}
