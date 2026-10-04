using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using NoteApp.Data;
using NoteApp.Data.Repositories;
using NoteApp.Helpers;
using NoteApp.Models.Enums;
using NoteApp.Native;
using NoteApp.Services;
using NoteApp.ViewModels;
using NoteApp.Views;

using Serilog;

namespace NoteApp;

/// <summary>
/// 应用程序入口。负责启动时的初始化（日志、单实例、DI、数据库、服务）、退出时的资源释放，
/// 以及全局未处理异常的兜底处理。
/// </summary>
public partial class App : Application
{
    public IServiceProvider Services { get; private set; } = null!;

    // 持有单实例服务引用（其内部持有 Mutex），防止 GC 回收导致锁失效
    private SingleInstanceService? _singleInstance;

    // 持有托盘服务引用，便于退出时移除图标
    private ITrayService? _trayService;

    // 持有快捷键服务引用，便于退出时注销热键
    private IHotkeyService? _hotkeyService;

    // 持有窗口服务引用，便于退出时关闭所有便签窗口
    private IWindowService? _windowService;

    // 桌面框选批量拖动的协调器（内含全局鼠标钩子，退出时必须释放）
    private MarqueeSelector? _marqueeSelector;

    /// <summary>
    /// 🔴 **仅供自动化验证**：把框选协调器以「可被合成输入驱动」的方式装上。
    ///
    /// 本机（以及很多无真实鼠标的环境）里，自动化脚本只能产生 <c>mouse_event</c> 合成输入，
    /// 而合成输入**不会**进入按线程过滤的低级钩子链 —— 于是框选在自动化里无法被驱动。
    /// 打开这个开关后，钩子按「接受任何线程」安装，验证脚本就能真实地点到钩子、
    /// 走进与线上完全相同的状态机。
    ///
    /// 由环境变量 <c>TODAY_OPEN_AUTOMATION=1</c> 打开，正常启动永远是 false。
    /// 覆盖层也必须一并关掉：它是个全屏置顶窗口，会挡住验证脚本读取便签窗口的边界。
    /// </summary>
    private static bool IsAutomationMode =>
        Environment.GetEnvironmentVariable("TODAY_OPEN_AUTOMATION") == "1";

    // 持有设置与自启服务引用，便于启动时同步注册表、设置变更时联动
    private ISettingsService? _settingsService;
    private IAutostartService? _autostartService;

    // 持有主题服务引用，便于启动时应用初始主题、设置变更时切换
    private IThemeService? _themeService;

    // 持有提醒服务引用，便于退出时停止调度器
    private IReminderService? _reminderService;

    // 提醒通知管理器（订阅提醒触发事件，右下角队列显示通知弹窗）
    private NotificationManager? _notificationManager;

    // 便签列表面板单例（重复打开时激活已有窗口）
    private NoteListView? _listView;

    // 设置面板单例（重复打开时激活已有窗口）
    private SettingsWindow? _settingsWindow;

    // 待执行的备份恢复路径。用户确认恢复后置位，在 OnExit 末尾真正执行。
    private string? _pendingRestorePath;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            // 日志系统必须最先初始化，确保后续所有步骤都能记录
            LoggerInitializer.Initialize();
            LogHelper.Info("应用启动。");

            DispatcherUnhandledException += OnDispatcherUnhandledException;

            // 单实例检查（命名 Mutex）：已有实例则激活后退出
            _singleInstance = new SingleInstanceService();
            if (_singleInstance.IsAlreadyRunning)
            {
                LogHelper.Info("检测到已有实例，激活后退出。");
                _singleInstance.ActivateExistingInstance();
                Shutdown(0);
                return;
            }

            Services = ConfigureServices();
            ServiceLocator.Initialize(Services);

            // 初始化数据库（建表 + 预设设置），确保应用使用前数据层就绪
            var dbContext = Services.GetRequiredService<AppDbContext>();
            dbContext.InitializeDatabase();

            // 启动时检查自动备份（到期则复制 db 文件到 Backup 目录）
            var backupService = Services.GetRequiredService<IBackupService>();
            backupService.CheckAndBackup();

            // 按「回收站保留天数」清理过期便签（放在备份检查之后：
            // 若本次启动恰好触发了备份，清理前已有一份含这些便签的快照作退路）
            PurgeExpiredTrash(Services);

            // 验证 DI 配置：能成功解析 INoteService 即视为容器构建正确
            var noteService = Services.GetRequiredService<INoteService>();
            LogHelper.Info($"DI 容器初始化完成。INoteService 解析成功：{noteService.GetType().Name}");

            // 初始化系统托盘（先显示，用户可提前操作）
            InitializeTray(Services);

            // 注册全局快捷键并绑定触发路由
            InitializeHotkeys(Services);

            // 桌面框选批量拖动：装全局鼠标钩子 + 注入便签间对齐所需的位置查询
            InitializeMarqueeSelection(Services);

            // 同步开机自启设置到注册表，并订阅设置变更联动
            InitializeAutostart(Services);

            // 应用初始主题（浅色/深色/跟随系统）
            _themeService = Services.GetRequiredService<IThemeService>();
            _themeService.ApplyTheme(Services.GetRequiredService<ISettingsService>().Theme);

            // 首次启动：创建欢迎便签（满足条件时），随后由恢复流程统一打开
            WelcomeNoteCreator.CreateIfFirstRun(
                Services.GetRequiredService<INoteService>(),
                Services.GetRequiredService<ISettingsService>());

            // 启动恢复：打开所有未删除未归档的便签窗口
            RestoreNoteWindows(Services);

            // 启动提醒调度器（每分钟检查到期提醒；先恢复便签窗口，便于到期提醒定位窗口）
            _reminderService = Services.GetRequiredService<IReminderService>();
            // 先订阅通知，再启动调度器，使启动时的补检也能弹出通知
            _notificationManager = new NotificationManager(_reminderService);
            _reminderService.Start();
        }
        catch (Exception ex)
        {
            LogHelper.Error("应用启动失败", ex);
            MessageBox.Show($"应用启动失败：{ex.Message}", "今日营业中", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// 启动时按设置页的「回收站保留天数」（data.retain_days）清理已过保留期的便签。
    /// 清理数大于 0 时给一条 Toast —— 自动删除用户数据不该悄无声息。
    /// 单条失败不影响启动，故整体捕获。
    /// </summary>
    private static void PurgeExpiredTrash(IServiceProvider services)
    {
        try
        {
            var settings = services.GetRequiredService<ISettingsService>();
            var retainDays = settings.GetInt("data.retain_days", 30);
            var purged = services.GetRequiredService<INoteService>().PurgeExpiredTrash(retainDays);

            if (purged > 0)
            {
                ToastWindow.Show($"已自动清理回收站 {purged} 条（保留 {retainDays} 天）");
            }
        }
        catch (Exception ex)
        {
            LogHelper.Error("回收站自动清理失败", ex);
        }
    }

    /// <summary>启动时恢复所有未删除未归档的便签窗口；单个恢复失败不影响整体。</summary>
    private static void RestoreNoteWindows(IServiceProvider services)
    {
        try
        {
            var noteService = services.GetRequiredService<INoteService>();
            var windowService = services.GetRequiredService<IWindowService>();
            var notes = noteService.GetAllNotes(includeArchived: false)
                .OrderByDescending(n => n.IsPinned)
                .ThenBy(n => n.CreatedAt)
                .ToList();

            var restored = 0;
            foreach (var note in notes)
            {
                try
                {
                    windowService.ShowNoteWindow(note.Id);
                    restored++;
                }
                catch (Exception ex)
                {
                    LogHelper.Error($"恢复便签窗口失败：{note.Id}", ex);
                }
            }

            LogHelper.Info($"启动恢复 {restored} 个便签窗口");
        }
        catch (Exception ex)
        {
            LogHelper.Error("启动恢复便签窗口失败", ex);
        }
    }

    /// <summary>初始化系统托盘，并将菜单事件路由到对应服务。</summary>
    private void InitializeTray(IServiceProvider services)
    {
        _trayService = services.GetRequiredService<ITrayService>();
        _trayService.Initialize();

        // 持有窗口服务引用，供托盘菜单与退出逻辑共用
        var windowService = services.GetRequiredService<IWindowService>();
        _windowService = windowService;
        var noteEditorService = services.GetRequiredService<INoteEditorService>();

        _trayService.NewNoteRequested += (_, _) =>
        {
            WindowNativeMethods.GetCursorPos(out var cursor);
            noteEditorService.CreateNewNote(new Point(cursor.X, cursor.Y));
        };
        _trayService.ShowAllRequested += (_, _) => windowService.ShowAllNoteWindows();
        _trayService.HideAllRequested += (_, _) => windowService.HideAllNoteWindows();
        _trayService.OpenListRequested += (_, _) => OpenNoteListView();
        _trayService.OpenSettingsRequested += (_, _) => OpenSettings();
        _trayService.ExitRequested += (_, _) => Shutdown();
    }

    /// <summary>注册全局快捷键，并将触发事件路由到对应服务。</summary>
    private void InitializeHotkeys(IServiceProvider services)
    {
        _hotkeyService = services.GetRequiredService<IHotkeyService>();

        var noteEditorService = services.GetRequiredService<INoteEditorService>();
        var windowService = services.GetRequiredService<IWindowService>();

        _hotkeyService.HotkeyTriggered += (_, e) =>
        {
            switch (e.Name)
            {
                case "hotkey.new_note":
                    WindowNativeMethods.GetCursorPos(out var cursor);
                    noteEditorService.CreateNewNote(new Point(cursor.X, cursor.Y));
                    break;
                case "hotkey.toggle_all":
                    windowService.ToggleAllNoteWindows();
                    break;
                case "hotkey.search":
                    OpenNoteListView();
                    break;
            }
        };

        _hotkeyService.RegisterDefaultHotkeys();
    }

    /// <summary>
    /// 初始化「桌面框选批量拖动」。
    ///
    /// 除了启动协调器，还要给便签窗口注入「别人都在哪」的查询回调 ——
    /// 拖动时的便签间对齐吸附需要它。放在这里注入而不是让 NoteWindow 直接
    /// 依赖 IWindowService，是为了保持窗口类的单向依赖。
    /// </summary>
    private void InitializeMarqueeSelection(IServiceProvider services)
    {
        var windowService = services.GetRequiredService<IWindowService>();

        // 便签窗口问「其它便签的可见内容矩形」时的实现：
        // 遍历当前打开的便签，扣掉各自的阴影边距后返回。
        NoteWindow.OtherNoteRectsProvider = self =>
        {
            var rects = new List<Rect>();
            foreach (var id in windowService.GetOpenWindowIds())
            {
                var other = windowService.GetWindow(id);
                if (other is null || ReferenceEquals(other, self) || !other.IsVisible)
                {
                    continue;
                }

                var margin = other.ShadowMarginValue;
                rects.Add(new Rect(
                    other.Left + margin,
                    other.Top + margin,
                    Math.Max(1, other.Width - margin * 2),
                    Math.Max(1, other.Height - margin * 2)));
            }

            return rects;
        };

        _marqueeSelector = new MarqueeSelector(windowService);

        // 便签在决定要不要走自己的拖拽前会问一句「我是不是已被框选选中」。
        // 选中了就让路给统一拖动 —— 否则被按住的那张会被两条路径同时移动、
        // 位移比组内其他成员多一截（详见 NoteWindow.IsSelectedByMarqueeProvider）。
        NoteWindow.IsSelectedByMarqueeProvider = self => _marqueeSelector!.IsSelected(self);

        if (IsAutomationMode)
        {
            // 验证模式：
            //  - 覆盖层不显示（全屏置顶窗口会干扰脚本读便签窗口边界）
            //  - 钩子按"接受任何线程"安装，使合成输入也能驱动状态机
            //  - 坐标换算注入：脚本给的是"便签窗口坐标系"的值，
            //    这里除以系统 DPI 缩放，等价于真实鼠标的物理像素→DIP 换算
            var scale = Helpers.MonitorHelper.SystemDpiScale();
            _marqueeSelector.ShowOverlayForTest = false;
            _marqueeSelector.AutomationDipScale = scale;
            _marqueeSelector.ScreenPointConverter = p =>
                new Point(p.X / scale, p.Y / scale);
            _marqueeSelector.InstallHookForAutomation();
            LogHelper.Warning($"⚠️ 框选协调器以【自动化验证模式】启动（坐标缩放 {scale}）");
        }
        else
        {
            _marqueeSelector.Start();
        }
    }

    /// <summary>同步开机自启设置到注册表，并订阅设置变更以联动更新。</summary>
    private void InitializeAutostart(IServiceProvider services)
    {
        _autostartService = services.GetRequiredService<IAutostartService>();
        _settingsService = services.GetRequiredService<ISettingsService>();

        _settingsService.SettingChanged += OnSettingChanged;

        var autostart = _settingsService.GetBool("general.autostart", true);
        _autostartService.SetEnabled(autostart);
    }

    private void OnSettingChanged(object? sender, SettingChangedEventArgs e)
    {
        if (e.Key == "general.autostart")
        {
            _autostartService?.SetEnabled(_settingsService?.GetBool("general.autostart", true) ?? true);
        }
        else if (e.Key == "appearance.theme")
        {
            if (Enum.TryParse<AppTheme>(e.Value, ignoreCase: true, out var theme))
            {
                _themeService?.ApplyTheme(theme);
            }
        }
    }

    /// <summary>打开便签列表面板（单例）；已存在时激活而非重复创建。</summary>
    private void OpenNoteListView()
    {
        if (_listView is not null)
        {
            if (_listView.WindowState == WindowState.Minimized)
            {
                _listView.WindowState = WindowState.Normal;
            }
            _listView.Show();
            _listView.Activate();
            return;
        }

        _listView = new NoteListView();
        _listView.Show();
    }

    /// <summary>打开设置面板（单例）；已存在时激活而非重复创建，关闭后置空以便下次重建。供托盘与列表面板共用。</summary>
    public void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
            {
                _settingsWindow.WindowState = WindowState.Normal;
            }
            _settingsWindow.Show();
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(Services.GetRequiredService<SettingsViewModel>());
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    /// <summary>
    /// 请求「恢复备份并重启应用」。
    ///
    /// 为什么不在点击时直接恢复：恢复要做的本质上是「用备份文件替换当前数据库」，
    /// 而此刻内存里所有 ViewModel 仍是旧数据。如果把替换放在应用运行中，
    /// 随后窗口关闭时的自动保存会把内存里的旧内容写回数据库，
    /// **静默覆盖掉刚恢复的数据** —— 用户以为抢救成功，实际可能又覆盖了一遍。
    ///
    /// 因此这里只记录意图并退出应用，真正的文件替换放在 <see cref="OnExit"/> 末尾：
    /// 那时所有窗口都已关闭、不再有写入方；且仍在单实例锁内，不会被另一个实例读写。
    /// 恢复成功后自动重新拉起应用（在释放单实例锁之后，否则新进程会被自己的锁挡掉）。
    /// </summary>
    public void RequestRestoreAndRestart(string backupFilePath)
    {
        _pendingRestorePath = backupFilePath;
        Shutdown();
    }

    /// <summary>
    /// 在退出流程末尾执行待定的恢复。返回是否需要重启应用。
    /// </summary>
    private bool ApplyPendingRestore()
    {
        if (_pendingRestorePath is not { } path)
        {
            return false;
        }

        _pendingRestorePath = null;

        try
        {
            Services.GetRequiredService<IBackupService>().RestoreFromBackup(path);
            LogHelper.Info("备份恢复已在退出流程中完成。");
            return true;
        }
        catch (Exception ex)
        {
            LogHelper.Error("退出时恢复备份失败", ex);
            // 恢复失败必须让用户知道，否则他会以为恢复成功了
            MessageBox.Show(
                $"恢复备份失败，当前数据未被改动。\n\n{ex.Message}",
                "恢复备份",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>
    /// 重新拉起自身以加载恢复后的数据库。
    /// ⚠️ 必须在单实例锁释放之后调用，否则新进程会检测到「已有实例」而自行退出。
    /// </summary>
    private static void RelaunchSelf()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                LogHelper.Warning("无法获取程序路径，恢复已完成，请手动重新打开应用。");
                return;
            }

            Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
            LogHelper.Info("备份恢复完成，已重新启动应用。");
        }
        catch (Exception ex)
        {
            LogHelper.Error("重新启动应用失败，请手动重新打开。", ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LogHelper.Info("应用正在退出...");

        // 先停止提醒调度器，避免关闭窗口期间再触发提醒
        _reminderService?.Stop();

        // 真正关闭便签列表面板（默认关闭被隐藏，需强制关闭以释放 VM 订阅）
        _listView?.ForceClose();

        // 关闭设置面板（标准窗口，直接关闭）
        _settingsWindow?.Close();

        // 关闭所有便签窗口（OnClosing 立即保存内容与位置，等待防抖的未保存内容也会落库）
        _windowService?.CloseAllNoteWindows();

        // 🔴 必须卸载全局鼠标钩子：留着会让钩子继续挂在系统输入链路上，
        // 既影响整机输入，也可能拖住进程无法正常退出。
        _marqueeSelector?.Dispose();
        NoteWindow.OtherNoteRectsProvider = null;
        NoteWindow.IsSelectedByMarqueeProvider = null;

        _hotkeyService?.UnregisterAll();
        _trayService?.Remove();

        // 待定的备份恢复放在最后执行：此时所有窗口已关闭、不再有写入方。
        // 窗口关闭时落库的都是「旧库」的数据，而旧库正是接下来要被覆盖的文件，
        // 因此不会影响恢复结果。
        var restartAfterRestore = ApplyPendingRestore();

        _singleInstance?.Release();

        // 重启必须在锁释放之后
        if (restartAfterRestore)
        {
            RelaunchSelf();
        }

        LogHelper.Info("应用已正常退出");
        LoggerInitializer.CloseAndFlush();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogHelper.Error("未处理的异常", e.Exception);
        e.Handled = true;
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // 配置（appsettings.json）
        services.AddSingleton<IConfiguration>(_ => new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .Build());

        // 数据库
        services.AddSingleton<AppDbContext>();

        // Repositories
        services.AddSingleton<INoteRepository, NoteRepository>();
        services.AddSingleton<ITagRepository, TagRepository>();
        services.AddSingleton<IReminderRepository, ReminderRepository>();
        services.AddSingleton<IAttachmentRepository, AttachmentRepository>();
        services.AddSingleton<ISettingsRepository, SettingsRepository>();

        // Services（单实例服务由 App 在启动时直接构造，不经 DI）
        services.AddSingleton<INoteService, NoteService>();
        services.AddSingleton<IWindowService, WindowService>();
        services.AddSingleton<INoteEditorService, NoteEditorService>();
        services.AddSingleton<IHotkeyService, HotkeyService>();
        services.AddSingleton<ITrayService, TrayService>();
        services.AddSingleton<IReminderService, ReminderService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IAutostartService, AutostartService>();
        services.AddSingleton<IBackupService, BackupService>();
        services.AddSingleton<IImportService, ImportService>();
        services.AddSingleton<IAttachmentService, AttachmentService>();
        services.AddSingleton<IThemeService, ThemeService>();

        // ViewModels（设置窗口每次打开重建，故注册为 Transient）
        services.AddTransient<SettingsViewModel>();

        // 日志（Serilog.ILogger 单例）
        services.AddSingleton<Serilog.ILogger>(Log.Logger);

        return services.BuildServiceProvider();
    }
}
