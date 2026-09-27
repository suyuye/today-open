using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using NoteApp.Services;

namespace NoteApp.ViewModels;

/// <summary>设置窗口 ViewModel：左侧导航分类 + 各分类子 ViewModel 容器。订阅设置变更以联动刷新相关 UI。</summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;

    public SettingsViewModel(
        ISettingsService settingsService,
        IAutostartService autostartService,
        IHotkeyService hotkeyService,
        IBackupService backupService,
        INoteService noteService,
        IImportService importService,
        IAttachmentService attachmentService)
    {
        _settingsService = settingsService;

        GeneralSettings = new GeneralSettingsViewModel(settingsService, autostartService);
        Appearance = new AppearanceSettingsViewModel(settingsService);
        HotkeySettings = new HotkeySettingsViewModel(settingsService, hotkeyService);
        DataSettings = new DataSettingsViewModel(settingsService, backupService, noteService, importService, attachmentService);
        About = new AboutViewModel();

        _settingsService.SettingChanged += OnSettingChanged;
    }

    public GeneralSettingsViewModel GeneralSettings { get; }

    public AppearanceSettingsViewModel Appearance { get; }

    public HotkeySettingsViewModel HotkeySettings { get; }

    public DataSettingsViewModel DataSettings { get; }

    public AboutViewModel About { get; }

    public ObservableCollection<string> Categories { get; } = new()
    {
        "通用",
        "外观",
        "快捷键",
        "数据",
        "关于",
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CategoryTitle))]
    private string _currentCategory = "通用";

    /// <summary>右侧标题栏文本：关于页显示「关于」，其余显示「{分类}设置」。</summary>
    public string CategoryTitle => CurrentCategory == "关于" ? "关于" : $"{CurrentCategory}设置";

    [RelayCommand]
    private void SelectCategory(string category)
    {
        CurrentCategory = category;
    }

    /// <summary>设置变更联动：备份开关变化时刷新备份列表，使 UI 与最新状态同步。</summary>
    private void OnSettingChanged(object? sender, SettingChangedEventArgs e)
    {
        if (e.Key == "data.backup_enabled")
        {
            DataSettings.RefreshBackupList();
        }
    }

    /// <summary>释放资源：取消所有事件订阅。窗口关闭时由 code-behind 调用。</summary>
    public void Cleanup()
    {
        _settingsService.SettingChanged -= OnSettingChanged;
    }
}
