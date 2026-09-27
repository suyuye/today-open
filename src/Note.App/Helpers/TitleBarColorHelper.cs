using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace NoteApp.Helpers;

/// <summary>
/// 标题栏颜色辅助类：通过 Windows DWM API 设置窗口标题栏背景色和文字色，
/// 解决在某些系统主题下选中窗口标题栏变黑的问题。
/// 仅在 Windows 10 20H1 (build 19041) 及以上版本生效，更早版本静默忽略。
/// </summary>
public static class TitleBarColorHelper
{
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    // 浅色主题标题栏颜色（与 BgPage 一致）
    private static readonly Color LightBackground = Color.FromRgb(0xF3, 0xF0, 0xEA);
    private static readonly Color LightText = Color.FromRgb(0x1A, 0x1B, 0x1C);

    // 深色主题标题栏颜色（与 BgPage 一致）
    private static readonly Color DarkBackground = Color.FromRgb(0x1E, 0x1E, 0x1E);
    private static readonly Color DarkText = Color.FromRgb(0xE0, 0xE0, 0xE0);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    /// <summary>
    /// 将 WPF Color 转换为 DWM API 使用的 BGR 格式整数（0x00BBGGRR）。
    /// </summary>
    private static int ColorToBgr(Color color)
    {
        return (color.B << 16) | (color.G << 8) | color.R;
    }

    /// <summary>
    /// 检测当前是否为深色主题（通过读取应用资源 BgPage 判断）。
    /// </summary>
    private static bool IsDarkTheme()
    {
        try
        {
            if (Application.Current?.Resources["BgPage"] is SolidColorBrush brush)
            {
                // 深色主题 BgPage = #1E1E1E，浅色 = #F3F0EA
                return brush.Color.R < 0x80;
            }
        }
        catch
        {
            // 读取失败回退浅色
        }
        return false;
    }

    /// <summary>
    /// 根据当前应用主题（浅色/深色）自动设置窗口标题栏颜色。
    /// 建议在窗口构造函数和主题变化事件中调用。
    /// </summary>
    /// <param name="window">目标窗口</param>
    public static void ApplyByCurrentTheme(Window window)
    {
        var isDark = IsDarkTheme();
        var bg = isDark ? DarkBackground : LightBackground;
        var text = isDark ? DarkText : LightText;
        Apply(window, bg, text);
    }

    /// <summary>
    /// 为指定窗口设置标题栏背景色和文字色。
    /// </summary>
    /// <param name="window">目标窗口</param>
    /// <param name="backgroundColor">标题栏背景色（通常与窗口背景色一致）</param>
    /// <param name="textColor">标题栏文字色（通常为深色，确保在浅色背景上可读）</param>
    public static void Apply(Window window, Color backgroundColor, Color textColor)
    {
        if (window == null) return;

        // 等待窗口句柄创建完成
        if (window.IsLoaded)
        {
            ApplyToHandle(window, backgroundColor, textColor);
        }
        else
        {
            window.Loaded += (s, e) => ApplyToHandle(window, backgroundColor, textColor);
        }
    }

    private static void ApplyToHandle(Window window, Color backgroundColor, Color textColor)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            int bgBgr = ColorToBgr(backgroundColor);
            int textBgr = ColorToBgr(textColor);

            // 设置标题栏背景色
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref bgBgr, sizeof(int));

            // 设置标题栏文字色
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref textBgr, sizeof(int));
        }
        catch
        {
            // DWM API 在不支持的 Windows 版本上可能抛出异常，静默忽略即可
            // 不影响软件正常运行，只是标题栏颜色保持系统默认
        }
    }
}
