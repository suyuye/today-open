using System;
using System.Windows;
using System.Windows.Media;

using Microsoft.Win32;

using NoteApp.Models.Enums;

namespace NoteApp.Services;

/// <summary>主题服务实现：维护浅色/深色两套主题颜色，切换时更新 Application 资源字典。</summary>
public class ThemeService : IThemeService
{
    /// <inheritdoc/>
    public event EventHandler<AppTheme>? ThemeChanged;
    /// <summary>浅色主题颜色（与 Resources/Colors.xaml 默认一致）。</summary>
    private static readonly Dictionary<string, Color> LightColors = new()
    {
        ["TextPrimary"] = Color.FromRgb(0x1A, 0x1B, 0x1C),
        ["TextSecondary"] = Color.FromRgb(0x44, 0x44, 0x44),
        ["TextTertiary"] = Color.FromRgb(0x66, 0x66, 0x66),
        ["TextPlaceholder"] = Color.FromRgb(0x99, 0x99, 0x99),
        ["BgPage"] = Color.FromRgb(0xF3, 0xF0, 0xEA),
        ["BgCard"] = Color.FromRgb(0xFF, 0xFF, 0xFF),
        ["BgSidebar"] = Color.FromRgb(0xEC, 0xE9, 0xE3),
        ["BorderPrimary"] = Color.FromRgb(0xD4, 0xD1, 0xCB),
        ["BorderLight"] = Color.FromRgb(0xE0, 0xDD, 0xD7),
    };

    /// <summary>深色主题颜色（深灰背景 + 浅色文本）。</summary>
    private static readonly Dictionary<string, Color> DarkColors = new()
    {
        ["TextPrimary"] = Color.FromRgb(0xE0, 0xE0, 0xE0),
        ["TextSecondary"] = Color.FromRgb(0xB4, 0xB4, 0xB4),
        ["TextTertiary"] = Color.FromRgb(0x8A, 0x8A, 0x8A),
        ["TextPlaceholder"] = Color.FromRgb(0x6E, 0x6E, 0x6E),
        ["BgPage"] = Color.FromRgb(0x1E, 0x1E, 0x1E),
        ["BgCard"] = Color.FromRgb(0x2A, 0x2A, 0x2A),
        ["BgSidebar"] = Color.FromRgb(0x26, 0x26, 0x26),
        ["BorderPrimary"] = Color.FromRgb(0x3F, 0x3F, 0x3F),
        ["BorderLight"] = Color.FromRgb(0x33, 0x33, 0x33),
    };

    public void ApplyTheme(AppTheme theme)
    {
        var resolvedTheme = ResolveTheme(theme);
        var isDark = resolvedTheme == AppTheme.Dark;
        var colors = isDark ? DarkColors : LightColors;

        var resources = Application.Current.Resources;
        foreach (var (key, color) in colors)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources[key] = brush;
        }

        // 触发主题变化事件
        ThemeChanged?.Invoke(this, resolvedTheme);
    }

    /// <summary>解析「跟随系统」：读注册表 AppsUseLightTheme 判断系统深浅色。</summary>
    private static AppTheme ResolveTheme(AppTheme theme)
    {
        if (theme != AppTheme.System)
        {
            return theme;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value)
            {
                return value == 0 ? AppTheme.Dark : AppTheme.Light;
            }
        }
        catch
        {
            // 读取失败回退浅色
        }

        return AppTheme.Light;
    }
}
