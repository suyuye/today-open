using NoteApp.Helpers;
using NoteApp.ViewModels;
using NoteApp.Views;

using Xunit;

namespace NoteApp.Tests;

/// <summary>
/// 关于页「检查更新」的回归测试。
///
/// 历史行为：该按钮只弹一句「已是最新版本」，与用户预期（去仓库看有没有新版）不符，
/// 属于 MVP 占位实现。现在必须真的跳转到项目仓库。
///
/// 测试通过 <see cref="ProjectLinks.Opener"/> 注入记录器来断言目标地址，
/// 不能在测试里真的弹出浏览器。
/// </summary>
[Collection(StaticStateCollection.Name)]
public class AboutViewModelTests : IDisposable
{
    private readonly List<string> _opened = new();
    private readonly List<string> _toasts = new();

    public AboutViewModelTests()
    {
        ProjectLinks.Opener = url => _opened.Add(url);
        // Toast 也要挡掉：本测试线程没有消息循环，真创建窗口会抛异常
        ToastWindow.Displayer = (message, _, _) => _toasts.Add(message);
    }

    public void Dispose()
    {
        ProjectLinks.ResetOpener();
        ToastWindow.ResetDisplayer();
    }

    [Fact]
    public void Version_MatchesAppInfo()
    {
        var vm = new AboutViewModel();

        Assert.Equal(AppInfo.VersionText, vm.Version);
    }

    [Fact]
    public void RepositoryUrl_IsProjectRepo()
    {
        var vm = new AboutViewModel();

        Assert.Equal(ProjectLinks.Repository, vm.RepositoryUrl);
    }

    [Fact]
    public void CheckUpdateCommand_OpensProjectRepository()
    {
        var vm = new AboutViewModel();

        vm.CheckUpdateCommand.Execute(null);

        Assert.Single(_opened);
        // 必须落在项目仓库范围内（Releases 页），而不是随便一个地址
        Assert.StartsWith(ProjectLinks.Repository, _opened[0]);
        Assert.Equal(ProjectLinks.LatestRelease, _opened[0]);
    }

    [Fact]
    public void CheckUpdateCommand_NoLongerClaimsAlreadyLatest()
    {
        // 防回归：旧实现只弹「已是最新版本」提示、不跳转。
        var vm = new AboutViewModel();

        vm.CheckUpdateCommand.Execute(null);

        Assert.NotEmpty(_opened);
        Assert.DoesNotContain("已是最新版本", string.Join("|", _toasts));
    }
}
