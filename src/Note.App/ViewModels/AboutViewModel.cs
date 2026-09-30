using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using NoteApp.Helpers;
using NoteApp.Views;

namespace NoteApp.ViewModels;

/// <summary>
/// 关于页 ViewModel：版本号（唯一来源 AppInfo.VersionText）与「检查更新」命令。
///
/// 说明：程序不自建更新检查服务（不做联网取版本号），「检查更新」直接跳转
/// 项目的 GitHub 仓库 —— 用户在仓库的 Releases 页即可看到最新版本与便携版下载。
/// 这样既不引入网络依赖与证书问题，也保证跳转目标永远是真实有效地址。
/// </summary>
public partial class AboutViewModel : ViewModelBase
{
    public AboutViewModel()
    {
    }

    /// <summary>版本号显示文本，如 "v1.5.3"。</summary>
    public string Version => AppInfo.VersionText;

    /// <summary>项目主页地址（GitHub 仓库），供界面展示。</summary>
    public string RepositoryUrl => ProjectLinks.Repository;

    /// <summary>
    /// 检查更新：打开项目 GitHub 仓库（Releases 页可见最新版本与便携版）。
    /// </summary>
    [RelayCommand]
    private void CheckUpdate()
    {
        ProjectLinks.Open(ProjectLinks.LatestRelease);
        ToastWindow.Show($"已打开项目主页，可在 Releases 查看最新版本");
    }
}
