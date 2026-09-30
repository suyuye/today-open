using Xunit;

namespace NoteApp.Tests;

/// <summary>
/// 会改动「进程级静态状态」（如 <c>ProjectLinks.Opener</c>、<c>ToastWindow.Displayer</c>）的
/// 测试必须放进同一个集合，让它们串行执行 —— 否则 xunit 默认跨类并行，
/// 一个测试的清理会把另一个测试正在用的替身重置掉，表现为随机失败
/// （曾真实出现：「检查更新」跳转测试偶发断言不到任何打开的地址）。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class StaticStateCollection
{
    public const string Name = "静态状态（串行）";
}
