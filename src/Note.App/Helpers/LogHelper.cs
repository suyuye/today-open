using Serilog;

namespace NoteApp.Helpers;

/// <summary>
/// 日志便捷封装，避免在业务代码中直接依赖 Serilog 的静态 Log 类型。
/// </summary>
public static class LogHelper
{
    public static void Info(string message) => Log.Information(message);
    public static void Debug(string message) => Log.Debug(message);
    public static void Warning(string message) => Log.Warning(message);
    public static void Error(string message) => Log.Error(message);
    public static void Error(string message, Exception exception) => Log.Error(exception, message);
    public static void Fatal(string message) => Log.Fatal(message);
}
