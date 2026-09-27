using System.Reflection;

namespace NoteApp.Helpers;

/// <summary>
/// 应用级只读信息。版本号显示文本的**唯一来源**——
/// 主页（NoteListView）、设置窗口（SettingsWindow）、关于页（AboutViewModel）
/// 都从这里取，XAML 侧用 {x:Static helpers:AppInfo.VersionText}。
/// 硬编码版本字符串曾在设置窗口漏更新（恒显示 v1.0.0），勿再手拼。
/// </summary>
public static class AppInfo
{
    /// <summary>如 "v1.5.3"。取自程序集 Version，取不到时兜底 "v1.0.0"。</summary>
    public static string VersionText { get; } =
        "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
}
