using Microsoft.Extensions.DependencyInjection;

namespace NoteApp.Helpers;

/// <summary>
/// 全局服务定位器，提供对 <see cref="IServiceProvider"/> 的静态访问。
/// 主要用于 View 的 code-behind 等无法通过构造函数注入的场景。
/// </summary>
public static class ServiceLocator
{
    private static IServiceProvider? _provider;

    /// <summary>初始化全局服务提供器（在 App.OnStartup 中调用一次）。</summary>
    public static void Initialize(IServiceProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    /// <summary>获取服务，未注册时返回 null。</summary>
    public static T? GetService<T>() where T : class
    {
        return _provider?.GetService(typeof(T)) as T;
    }

    /// <summary>获取服务，未注册或未初始化时抛出异常。</summary>
    public static T GetRequiredService<T>() where T : notnull
    {
        if (_provider is null)
        {
            throw new InvalidOperationException("ServiceLocator 尚未初始化，请先在 App.OnStartup 中调用 Initialize。");
        }

        return (T)_provider.GetRequiredService(typeof(T));
    }
}
