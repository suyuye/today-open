using NoteApp.Models.Enums;

namespace NoteApp.Helpers;

/// <summary>
/// 便签颜色枚举的解析与展示辅助。
/// 之所以单独抽出来：色板从 6 色扩到 12 色时枚举名改了（Yellow → YellowDeep），
/// 但数据库里存的是旧枚举名的字符串，散落在多个 Repository 里的
/// <c>Enum.Parse</c> 会直接抛异常导致所有旧便签打不开。
/// 这里统一做新旧名映射，任何读取颜色的地方都必须走 <see cref="Parse"/>。
/// </summary>
public static class NoteColorHelper
{
    /// <summary>旧版 6 色枚举名 → 新版 12 色枚举名。用于读取历史数据。</summary>
    private static readonly Dictionary<string, NoteColor> LegacyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Yellow"] = NoteColor.YellowDeep,
        ["Green"] = NoteColor.GreenDeep,
        ["Blue"] = NoteColor.BlueDeep,
        ["Pink"] = NoteColor.PinkDeep,
        ["Purple"] = NoteColor.PurpleDeep,
        ["Gray"] = NoteColor.GrayDeep,
    };

    /// <summary>
    /// 把数据库中的颜色字符串解析为枚举。
    /// 依次尝试：新枚举名 → 旧枚举名映射 → 兜底默认色。
    /// 永不抛异常：一条颜色字段异常的便签不应该导致整个列表加载失败。
    /// </summary>
    public static NoteColor Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Default;
        }

        var text = raw.Trim();

        if (Enum.TryParse<NoteColor>(text, ignoreCase: true, out var parsed) &&
            Enum.IsDefined(parsed))
        {
            return parsed;
        }

        return LegacyNames.TryGetValue(text, out var legacy) ? legacy : Default;
    }

    /// <summary>默认便签色（新的米黄深档）。</summary>
    public const NoteColor Default = NoteColor.YellowDeep;

    /// <summary>
    /// 全部可选颜色，按色相分组、每组先深后浅排列。
    /// 颜色选择菜单与设置页色板都从这里取，避免多处各写一份列表导致漏色。
    /// </summary>
    public static IReadOnlyList<NoteColor> All { get; } = new[]
    {
        NoteColor.YellowDeep, NoteColor.YellowLight,
        NoteColor.GreenDeep, NoteColor.GreenLight,
        NoteColor.BlueDeep, NoteColor.BlueLight,
        NoteColor.PinkDeep, NoteColor.PinkLight,
        NoteColor.PurpleDeep, NoteColor.PurpleLight,
        NoteColor.GrayDeep, NoteColor.GrayLight,
    };

    /// <summary>存库用的字符串（枚举名小写，与历史格式一致）。</summary>
    public static string ToStorage(NoteColor color) => color.ToString().ToLowerInvariant();

    /// <summary>
    /// 颜色中文名。12 色按色相命名，深浅用「深/浅」区分，
    /// 便于在颜色选择菜单里一眼看出同色相的两档关系。
    /// </summary>
    public static string GetDisplayName(NoteColor color) => color switch
    {
        NoteColor.YellowDeep => "米黄・深",
        NoteColor.YellowLight => "米黄・浅",
        NoteColor.GreenDeep => "薄荷・深",
        NoteColor.GreenLight => "薄荷・浅",
        NoteColor.BlueDeep => "雾蓝・深",
        NoteColor.BlueLight => "雾蓝・浅",
        NoteColor.PinkDeep => "豆沙・深",
        NoteColor.PinkLight => "豆沙・浅",
        NoteColor.PurpleDeep => "紫罗兰・深",
        NoteColor.PurpleLight => "紫罗兰・浅",
        NoteColor.GrayDeep => "高级灰・深",
        NoteColor.GrayLight => "高级灰・浅",
        _ => color.ToString(),
    };

    /// <summary>
    /// 色相键（去掉深浅后缀，如 "Yellow"），用于把同色相的两档归到一组。
    /// </summary>
    public static string GetHueKey(NoteColor color) => color switch
    {
        NoteColor.YellowDeep or NoteColor.YellowLight => "Yellow",
        NoteColor.GreenDeep or NoteColor.GreenLight => "Green",
        NoteColor.BlueDeep or NoteColor.BlueLight => "Blue",
        NoteColor.PinkDeep or NoteColor.PinkLight => "Pink",
        NoteColor.PurpleDeep or NoteColor.PurpleLight => "Purple",
        NoteColor.GrayDeep or NoteColor.GrayLight => "Gray",
        _ => color.ToString(),
    };

    /// <summary>色相中文名（不含深浅）。</summary>
    public static string GetHueDisplayName(NoteColor color) =>
        GetHueKey(color) switch
        {
            "Yellow" => "米黄",
            "Green" => "薄荷绿",
            "Blue" => "雾霾蓝",
            "Pink" => "豆沙粉",
            "Purple" => "紫罗兰",
            "Gray" => "高级灰",
            var other => other,
        };

    /// <summary>是否浅档（浅档配色对比度更弱，需要相应加深文字）。</summary>
    public static bool IsLight(NoteColor color) => color switch
    {
        NoteColor.YellowLight or NoteColor.GreenLight or NoteColor.BlueLight or
        NoteColor.PinkLight or NoteColor.PurpleLight or NoteColor.GrayLight => true,
        _ => false,
    };
}
