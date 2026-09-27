using System;

using NoteApp.Models.Enums;

namespace NoteApp.Services;

/// <summary>主题服务：切换浅色/深色主题（跟随系统时解析系统主题）。</summary>
public interface IThemeService
{
    /// <summary>应用主题，即时更新所有使用 DynamicResource 的颜色资源。</summary>
    void ApplyTheme(AppTheme theme);

    /// <summary>主题变化事件，参数为新主题（已解析 System 为 Light/Dark）。</summary>
    event EventHandler<AppTheme> ThemeChanged;
}
