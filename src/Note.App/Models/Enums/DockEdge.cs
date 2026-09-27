namespace NoteApp.Models.Enums;

/// <summary>
/// 便签贴边自动隐藏的贴靠边。
///
/// 只支持左右两条竖边：桌面便签贴在屏幕左右两侧最符合使用习惯
/// （上下边缘会和任务栏、窗口标题栏打架），因此不提供 Top/Bottom。
/// </summary>
public enum DockEdge
{
    /// <summary>未贴边。</summary>
    None = 0,

    /// <summary>贴靠屏幕左边缘。</summary>
    Left = 1,

    /// <summary>贴靠屏幕右边缘。</summary>
    Right = 2,
}
