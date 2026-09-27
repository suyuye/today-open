namespace NoteApp.Models.Enums;

/// <summary>
/// 便签的呈现形态。三种形态**互斥**，同一时刻只可能处于其中一种。
///
/// 之所以要收口成枚举：早期「折叠」和「极简」是两个独立的 bool，
/// 互斥关系靠两处手写 if 维护（`ToggleCollapse` 里关掉 Minimal、
/// `ToggleMinimal` 里关掉 Collapsed）。结果是：
///   ① 「这张便签现在什么形态」要同时读两个字段，任何一处漏判都会出边界 bug
///      （v1.5.2 修的「极简下右键菜单丢失」就与这类组合判断有关）；
///   ② 新增第三种形态时，要改的地方是「所有读这两个 bool 的位置」。
///
/// 收口后，判断只看这一个值；互斥由类型保证，写不出「既折叠又极简」的非法状态。
/// ⚠️ 存储层仍沿用 <c>Notes.IsCollapsed</c> / <c>Notes.IsMinimal</c> 两列
/// （不改表结构，老库与老备份都能原样读写），映射见 <see cref="Note.DisplayMode"/>。
/// </summary>
public enum NoteDisplayMode
{
    /// <summary>常规形态：标题栏 + 内容区 + 状态栏，可拖拽缩放。</summary>
    Normal = 0,

    /// <summary>
    /// 折叠成一条「胶囊」：只留标题栏（含待办徽标）与左侧语义色脊，
    /// 内容区、状态栏、纸纹全部收起，高度固定、不可缩放。
    /// 用于「这张便签暂时不重要，但标题要有印象」。
    /// </summary>
    Collapsed = 1,

    /// <summary>
    /// 极简磁贴：隐藏标题栏，只显示内容区（保留纸纹与便签底色），
    /// 用于「只关心内容、不要任何装饰」，也用于贴桌面当展示。
    /// </summary>
    Minimal = 2,
}
