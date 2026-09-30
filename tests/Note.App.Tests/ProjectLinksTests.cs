using NoteApp.Helpers;

using Xunit;

namespace NoteApp.Tests;

/// <summary>
/// 「检查更新」跳转项目仓库的行为测试。
///
/// 关键点：这些用例绝不能真的弹出浏览器 —— 用 <see cref="ProjectLinks.Opener"/>
/// 的注入点换成记录器，断言「打开了哪个地址」而不是「浏览器是否弹出」。
/// </summary>
[Collection(StaticStateCollection.Name)]
public class ProjectLinksTests : IDisposable
{
    private readonly List<string> _opened = new();

    public ProjectLinksTests()
    {
        ProjectLinks.Opener = url => _opened.Add(url);
    }

    public void Dispose() => ProjectLinks.ResetOpener();

    [Fact]
    public void Repository_IsTheGitHubRepoUrl()
    {
        Assert.Equal("https://github.com/suyuye/today-open", ProjectLinks.Repository);
    }

    [Fact]
    public void LatestRelease_PointsIntoRepositoryReleases()
    {
        Assert.StartsWith(ProjectLinks.Repository + "/releases", ProjectLinks.LatestRelease);
    }

    [Fact]
    public void Issues_PointsIntoRepositoryIssues()
    {
        Assert.Equal(ProjectLinks.Repository + "/issues", ProjectLinks.Issues);
    }

    [Fact]
    public void AllLinks_AreAbsoluteHttpsUrls()
    {
        foreach (var url in new[] { ProjectLinks.Repository, ProjectLinks.LatestRelease, ProjectLinks.Issues })
        {
            Assert.True(Uri.TryCreate(url, UriKind.Absolute, out var uri), $"不是绝对地址: {url}");
            Assert.Equal(Uri.UriSchemeHttps, uri!.Scheme);
        }
    }

    [Fact]
    public void Open_InvokesConfiguredOpenerWithUrl()
    {
        ProjectLinks.Open(ProjectLinks.Repository);

        Assert.Equal(new[] { ProjectLinks.Repository }, _opened);
    }

    [Fact]
    public void Open_BlankUrl_DoesNotInvokeOpener()
    {
        ProjectLinks.Open("");
        ProjectLinks.Open("   ");

        Assert.Empty(_opened);
    }

    [Fact]
    public void ResetOpener_RestoresDefaultImplementation()
    {
        ProjectLinks.ResetOpener();

        // 只能断言「不再是我们注入的记录器」——默认实现会真的调用系统浏览器，
        // 这里不能触发它，因此只验证委托实例被换掉了。
        Assert.NotEqual(_opened.Add, ProjectLinks.Opener);
    }
}
