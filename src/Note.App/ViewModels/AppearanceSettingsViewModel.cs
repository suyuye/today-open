using System.Collections.Generic;

using CommunityToolkit.Mvvm.ComponentModel;

using NoteApp.Helpers;
using NoteApp.Models.Enums;
using NoteApp.Services;

namespace NoteApp.ViewModels;

/// <summary>外观设置页 ViewModel：主题、默认颜色、默认不透明度、默认字号，双向绑定 ISettingsService。</summary>
public partial class AppearanceSettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;

    public AppearanceSettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;

        _theme = settingsService.GetString("appearance.theme", "light");
        _defaultColor = settingsService.GetValue("appearance.default_color", NoteColor.YellowDeep);
        _defaultOpacity = settingsService.GetInt("appearance.default_opacity", 100);
        _fontSize = settingsService.GetDouble("appearance.font_size", 16.5);
        _noteFont = settingsService.GetString("appearance.note_font", "handwriting");
    }

    /// <summary>主题选项（值、显示名、图标键）。</summary>
    public sealed record ThemeOption(string Value, string Display, string IconKey);

    public IReadOnlyList<ThemeOption> ThemeOptions { get; } = new[]
    {
        new ThemeOption("system", "跟随系统", "IconMonitor"),
        new ThemeOption("light", "浅色", "IconSun"),
        new ThemeOption("dark", "深色", "IconMoon"),
    };

    /// <summary>便签字体选项（值、显示名）。</summary>
    public sealed record NoteFontOption(string Value, string Display);

    public IReadOnlyList<NoteFontOption> NoteFontOptions { get; } = new[]
    {
        new NoteFontOption("system", "系统默认"),
        new NoteFontOption("handwriting", "霞鹜文楷"),
    };

    /// <summary>12 色色板，按色相分组、每组先深后浅（与便签颜色菜单同源）。</summary>
    public IReadOnlyList<NoteColor> ColorOptions { get; } = NoteColorHelper.All;

    [ObservableProperty]
    private string _theme;

    [ObservableProperty]
    private NoteColor _defaultColor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewOpacity))]
    private int _defaultOpacity;

    [ObservableProperty]
    private double _fontSize;

    [ObservableProperty]
    private string _noteFont;

    /// <summary>预览卡片不透明度（0~1），由 DefaultOpacity（30~100）换算。</summary>
    public double PreviewOpacity => DefaultOpacity / 100.0;

    /// <summary>预览标题字号：比内容字号大 2pt。</summary>
    public double TitleFontSize => FontSize + 2;

    partial void OnThemeChanged(string value) =>
        _settingsService.SetString("appearance.theme", value);

    partial void OnDefaultColorChanged(NoteColor value) =>
        _settingsService.SetValue("appearance.default_color", value);

    partial void OnDefaultOpacityChanged(int value) =>
        _settingsService.SetInt("appearance.default_opacity", value);

    partial void OnFontSizeChanged(double value)
    {
        _settingsService.SetValue("appearance.font_size", value);
        OnPropertyChanged(nameof(TitleFontSize));
    }

    partial void OnNoteFontChanged(string value) =>
        _settingsService.SetString("appearance.note_font", value);
}
