using NoteApp.Models.Enums;

namespace NoteApp.Models;

/// <summary>便签数据模型（纯 POCO，不引用任何其他层）。</summary>
public class Note
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>标题，可空（为空时取内容首行前 20 字）。</summary>
    public string? Title { get; set; }

    /// <summary>便签内容，纯文本。</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>便签颜色（莫兰迪 6 色）。</summary>
    public NoteColor Color { get; set; } = NoteColor.YellowDeep;

    public int PositionX { get; set; } = 100;
    public int PositionY { get; set; } = 100;
    public int Width { get; set; } = 220;
    public int Height { get; set; } = 200;

    /// <summary>不透明度，30-100。</summary>
    public int Opacity { get; set; } = 100;

    public bool IsPinned { get; set; }
    public bool IsLocked { get; set; }
    public bool IsArchived { get; set; }
    public bool IsCollapsed { get; set; }

    /// <summary>极简模式（桌面磁贴）：隐藏标题栏，只显示内容区。</summary>
    public bool IsMinimal { get; set; }

    /// <summary>
    /// 当前呈现形态（<see cref="NoteDisplayMode"/>）—— **判断形态的唯一入口**。
    ///
    /// 这是 <see cref="IsCollapsed"/> 与 <see cref="IsMinimal"/> 的派生视图，不是新字段：
    /// 存储层仍是那两列（不改表结构，老库/老备份原样可用），
    /// 但**读写形态一律走这里**，避免各处手写 `IsCollapsed` / `IsMinimal` 的组合判断。
    ///
    /// 两者理论上互斥（切换命令会互相排斥），但**外部数据不一定守规矩** ——
    /// 手改的 JSON、旧版本写下的库都可能同时为 true。
    /// 此时按 <see cref="NoteDisplayMode.Collapsed"/> 处理：折叠的视觉约束更强
    /// （窗口高度是固定的），若当成极简会得到一个高度错误的窗口。
    /// </summary>
    public NoteDisplayMode DisplayMode
    {
        get => IsCollapsed ? NoteDisplayMode.Collapsed
             : IsMinimal ? NoteDisplayMode.Minimal
             : NoteDisplayMode.Normal;
        set
        {
            // 写回时同步两个 bool，保证存储层与派生视图永不脱节。
            IsCollapsed = value == NoteDisplayMode.Collapsed;
            IsMinimal = value == NoteDisplayMode.Minimal;
        }
    }

    /// <summary>是否处于某个非默认形态（折叠或极简）。多处需要「形态受限」判断，收口到这里。</summary>
    public bool IsCompactDisplay => DisplayMode != NoteDisplayMode.Normal;

    /// <summary>
    /// 是否开启「贴边自动隐藏」。默认关闭 —— 这是个会改变窗口行为的特性，
    /// 只对用户明确开启的便签生效，不打扰既有使用习惯。
    /// </summary>
    public bool IsDockEnabled { get; set; }

    /// <summary>当前贴靠的边（未贴边为 None）。窗口收起时靠它决定往哪边缩。</summary>
    public DockEdge DockEdge { get; set; } = DockEdge.None;

    /// <summary>
    /// 是否显示右下角"卷角"（翻折的纸角）。纯外观开关，默认关闭 ——
    /// 老便签保持原样，只有用户主动开启的便签才带这个细节。
    /// </summary>
    public bool IsDogEar { get; set; }

    /// <summary>自定义字号，可空（为空时用全局默认）。</summary>
    public double? FontSize { get; set; }

    /// <summary>窗口层级（置顶便签之间的排序）。</summary>
    public int ZIndex { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>删除时间，null 表示未删除（软删除）。</summary>
    public DateTime? DeletedAt { get; set; }
}
