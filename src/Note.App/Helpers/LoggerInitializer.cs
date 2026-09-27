using System.IO;

using Microsoft.Extensions.Configuration;

using Serilog;
using Serilog.Events;

namespace NoteApp.Helpers;

/// <summary>
/// 负责初始化与关闭 Serilog 日志系统。
/// 日志写入 %APPDATA%\今日营业中\Logs\，按天滚动，保留指定天数。
/// </summary>
public static class LoggerInitializer
{
    /// <summary>初始化全局日志记录器（应用启动时最先调用）。</summary>
    public static void Initialize()
    {
        // 在 DI 容器构建前调用，故此处自行读取 appsettings.json
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .Build();

        // 最小级别：Debug 配置下 Debug，Release 下 Information；可由 appsettings.json 的 Logging:Level 覆盖
#if DEBUG
        var defaultLevel = LogEventLevel.Debug;
#else
        var defaultLevel = LogEventLevel.Information;
#endif
        var level = ParseLevel(config["Logging:Level"], defaultLevel);
        var retainDays = ParsePositiveInt(config["Logging:RetainDays"], 30);
        var maxFileSizeMB = ParsePositiveInt(config["Logging:MaxFileSizeMB"], 10);

        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "今日营业中", "Logs");
        Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .WriteTo.Debug()
            .WriteTo.File(
                Path.Combine(logDir, "log-.log"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: maxFileSizeMB * 1024L * 1024L,
                rollOnFileSizeLimit: true,
                retainedFileTimeLimit: TimeSpan.FromDays(retainDays),
                // 允许多进程写入同一日志文件（单实例检测时，第二实例需记录“检测到已有实例”）
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    /// <summary>刷新并关闭日志（应用退出时调用）。</summary>
    public static void CloseAndFlush() => Log.CloseAndFlush();

    private static LogEventLevel ParseLevel(string? value, LogEventLevel fallback) => value?.ToLowerInvariant() switch
    {
        "debug" => LogEventLevel.Debug,
        "information" or "info" => LogEventLevel.Information,
        "warning" or "warn" => LogEventLevel.Warning,
        "error" => LogEventLevel.Error,
        "fatal" => LogEventLevel.Fatal,
        _ => fallback,
    };

    private static int ParsePositiveInt(string? value, int fallback) =>
        int.TryParse(value, out var result) && result > 0 ? result : fallback;
}
