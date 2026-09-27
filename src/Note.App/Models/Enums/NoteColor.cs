namespace NoteApp.Models.Enums;

/// <summary>
/// 便签颜色：莫兰迪 12 色，按色相分为 6 组，每组一深一浅。
/// 深浅两档用于表达「轻重」——同色相下浅档更退让（适合背景/次要便签），
/// 深档更有存在感（适合需要被注意的便签）。
/// 命名规则：色相名 + Deep / Light，例如 YellowDeep / YellowLight。
/// </summary>
public enum NoteColor
{
    // —— 米黄 ——
    YellowDeep,
    YellowLight,

    // —— 薄荷绿 ——
    GreenDeep,
    GreenLight,

    // —— 雾霾蓝 ——
    BlueDeep,
    BlueLight,

    // —— 豆沙粉 ——
    PinkDeep,
    PinkLight,

    // —— 紫罗兰 ——
    PurpleDeep,
    PurpleLight,

    // —— 高级灰 ——
    GrayDeep,
    GrayLight,
}
