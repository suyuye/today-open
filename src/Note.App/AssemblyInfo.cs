using System.Windows;

// 资源字典定位：
// 主题特定资源（如 Themes/LightTheme.xaml、DarkTheme.xaml）存放在源程序集中，
// 通过 Application.Current.Resources 合并，而非按主题名称查找外部资源。
[assembly: ThemeInfo(
    ResourceDictionaryLocation.None,
    ResourceDictionaryLocation.SourceAssembly)]
