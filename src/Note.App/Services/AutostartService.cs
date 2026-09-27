using System.Diagnostics;

using Microsoft.Win32;

using NoteApp.Helpers;

namespace NoteApp.Services;

/// <summary>开机自启服务：读写当前用户注册表 Run 键，实现开机自启。</summary>
public class AutostartService : IAutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TodayOpen";

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                var value = key?.GetValue(ValueName) as string;
                return !string.IsNullOrWhiteSpace(value);
            }
            catch (Exception ex)
            {
                LogHelper.Error("读取开机自启状态失败", ex);
                return false;
            }
        }
    }

    public void Enable()
    {
        try
        {
            var exePath = GetExecutablePath();
            if (exePath is null)
            {
                LogHelper.Debug("开发调试阶段，跳过开机自启设置");
                return;
            }

            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            key?.SetValue(ValueName, $"\"{exePath}\"");
            LogHelper.Info($"开机自启已启用，路径：{exePath}");
        }
        catch (Exception ex)
        {
            LogHelper.Error("启用开机自启失败", ex);
        }
    }

    public void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return;
            }

            key.DeleteValue(ValueName, throwOnMissingValue: false);
            LogHelper.Info("开机自启已禁用");
        }
        catch (Exception ex)
        {
            LogHelper.Error("禁用开机自启失败", ex);
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            Enable();
        }
        else
        {
            Disable();
        }
    }

    /// <summary>获取当前进程可执行文件路径；Debug 构建（开发调试）返回 null 跳过，避免把 bin 下的构建产物写入注册表。</summary>
    private static string? GetExecutablePath()
    {
#if DEBUG
        return null;
#else
        var fileName = Process.GetCurrentProcess().MainModule?.FileName;
        return string.IsNullOrWhiteSpace(fileName) ? null : fileName;
#endif
    }
}
