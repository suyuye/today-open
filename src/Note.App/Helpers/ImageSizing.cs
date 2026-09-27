namespace NoteApp.Helpers;

/// <summary>
/// 图片尺寸计算。
///
/// 单独抽成纯函数是为了能测试：`System.Windows.Media.Imaging` 的解码/缩放
/// 需要真实图片与 UI 线程上下文，很难在单测里覆盖，但「该缩到多大」这一步
/// 恰恰是最容易算错、且错了很难发现的地方（例如把竖图当成横图处理、
/// 或对本来就很小的图反而放大导致模糊）。
/// </summary>
public static class ImageSizing
{
    /// <summary>
    /// 存库时允许的最长边。超过就等比缩小。
    /// 取 1600 是因为：截图与参考图在这个尺寸下已经够看清，
    /// 而再大的图会让数据库（以及每份备份）迅速膨胀。
    /// </summary>
    public const int MaxDimension = 1600;

    /// <summary>
    /// 按最长边不超过 <paramref name="maxDimension"/> 等比缩放，返回目标尺寸。
    /// **只缩不放**：本来就小于上限的图片原样返回，避免放大后变糊。
    /// </summary>
    public static (int Width, int Height) Fit(int width, int height, int maxDimension = MaxDimension)
    {
        // 非正尺寸属于异常输入，退回一个安全的最小值而不是抛异常
        if (width <= 0 || height <= 0)
        {
            return (1, 1);
        }

        if (maxDimension <= 0)
        {
            return (width, height);
        }

        var longest = Math.Max(width, height);
        if (longest <= maxDimension)
        {
            return (width, height);
        }

        var scale = (double)maxDimension / longest;
        return (Math.Max(1, (int)Math.Round(width * scale)),
                Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>是否需要缩放（供调用方跳过不必要的重编码）。</summary>
    public static bool NeedsResize(int width, int height, int maxDimension = MaxDimension) =>
        width > 0 && height > 0 && Math.Max(width, height) > maxDimension;
}
