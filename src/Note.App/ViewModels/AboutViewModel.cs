using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using NoteApp.Helpers;
using NoteApp.Views;

namespace NoteApp.ViewModels;

/// <summary>关于页 ViewModel：版本号（唯一来源 AppInfo.VersionText）与检查更新占位命令。</summary>
public partial class AboutViewModel : ViewModelBase
{
    public AboutViewModel()
    {
    }

    /// <summary>版本号显示文本，如 "v1.5.3"。</summary>
    public string Version => AppInfo.VersionText;

    /// <summary>MVP 阶段占位：检查更新仅提示已是最新版本。</summary>
    [RelayCommand]
    private void CheckUpdate() => ToastWindow.Show($"已是最新版本 {Version}");
}
