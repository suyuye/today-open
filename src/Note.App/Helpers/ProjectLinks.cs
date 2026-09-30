using System.Diagnostics;
using System.Reflection;

namespace NoteApp.Helpers;

/// <summary>
/// 项目外部链接的唯一来源，以及「用系统默认程序打开链接」的唯一入口。
///
/// 为什么集中到一处：这些地址会同时出现在「关于」页、设置页脚、错误提示等处，
/// 散落硬编码必然出现某一个漏改（历史上版本号就踩过同样的坑，见 <see cref="AppInfo"/>）。
/// 抽出 <see cref="Opener"/> 则是为了可测 —— 单元测试里不能真的弹浏览器。
/// </summary>
public static class ProjectLinks
{
    /// <summary>项目主页（GitHub 仓库）。「检查更新」按钮跳转的目标。</summary>
    public const string Repository = "https://github.com/suyuye/today-open";

    /// <summary>最新发布页（Releases），便于用户直接取到便携版压缩包。</summary>
    public const string LatestRelease = Repository + "/releases/latest";

    /// <summary>问题反馈页。</summary>
    public const string Issues = Repository + "/issues";

    /// <summary>
    /// 打开链接的动作。默认调用系统默认浏览器；测试中可替换为记录用的假实现，
    /// 避免在无人值守环境里真的弹出浏览器窗口。
    /// </summary>
    public static Action<string> Opener { get; set; } = OpenWithShell;

    /// <summary>用系统默认程序打开给定 URI（http/https 会走默认浏览器）。</summary>
    public static void Open(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        Opener(url);
    }

    /// <summary>恢复默认实现（供测试清理使用）。</summary>
    public static void ResetOpener() => Opener = OpenWithShell;

    private static void OpenWithShell(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // 打开失败（无默认浏览器 / 被策略拦截）不应让调用方崩溃，
            // 记日志即可；用户仍可手动复制地址访问。
            LogHelper.Warning("打开外部链接失败: " + url + " - " + ex.Message);
        }
    }

    /// <summary>当前程序集版本号，形如 "1.5.3"（与 <see cref="AppInfo.VersionText"/> 同源）。</summary>
    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
}
