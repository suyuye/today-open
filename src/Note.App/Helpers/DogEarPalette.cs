using System.Windows.Media;

namespace NoteApp.Helpers;

/// <summary>
/// 便签「卷角」的配色推导（纯函数，可单测）。
///
/// <para>
/// 卷角是纯装饰：右下角掀起的一个三角形，隐喻"这张纸被翻起了一角"。
/// 它必须跟便签自己的底色融为一体 —— 便签是 12 种颜色的，
/// 如果卷角用固定的米白，就会像贴了一张外来贴纸，破坏"颜色自洽"。
/// </para>
///
/// <para>
/// 所以折角的两面都从便签底色派生：亮面朝白偏移（背面朝光，本就比正面亮），
/// 折痕阴影朝黑偏移（掀起后下方的暗部）。偏移是**线性**的，色相不变，
/// 因此 12 种便签底色都能得到和谐的结果。
/// </para>
/// </summary>
internal static class DogEarPalette
{
    /// <summary>卷角外沿描边透明度（沿用便签描边色，仅压淡一点，避免折角边缘过重）。</summary>
    public const double FaceLightenAmount = 0.30;

    /// <summary>折痕阴影压暗幅度。</summary>
    public const double ShadowDarkenAmount = -0.22;

    /// <summary>
    /// 把颜色朝白（<paramref name="amount"/> 为正）或黑（为负）线性偏移，保持色相不变。
    /// <paramref name="amount"/> 会被夹到 [-1, 1]；偏移后的分量夹到 [0, 255]。
    /// </summary>
    public static Color ShiftBrightness(Color color, double amount)
    {
        amount = Math.Clamp(amount, -1.0, 1.0);

        if (amount >= 0)
        {
            return Color.FromRgb(
                Lerp(color.R, 255, amount),
                Lerp(color.G, 255, amount),
                Lerp(color.B, 255, amount));
        }

        // 负向：朝 0 插值，等价于整体乘 (1 + amount)
        var k = 1.0 + amount;
        return Color.FromRgb(
            (byte)Math.Clamp(color.R * k, 0, 255),
            (byte)Math.Clamp(color.G * k, 0, 255),
            (byte)Math.Clamp(color.B * k, 0, 255));
    }

    /// <summary>折角亮面：便签底色朝白偏移。</summary>
    public static Color FaceColor(Color noteBackground) =>
        ShiftBrightness(noteBackground, FaceLightenAmount);

    /// <summary>折痕阴影：便签底色朝黑偏移。</summary>
    public static Color ShadowColor(Color noteBackground) =>
        ShiftBrightness(noteBackground, ShadowDarkenAmount);

    private static byte Lerp(byte from, byte to, double t) =>
        (byte)Math.Clamp(from + (to - from) * t, 0, 255);
}
