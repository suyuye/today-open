using CommunityToolkit.Mvvm.ComponentModel;

using NoteApp.Services;

namespace NoteApp.ViewModels;

/// <summary>通用设置页 ViewModel：6 个设置项双向绑定 ISettingsService，修改即写入并触发 SettingChanged。</summary>
public partial class GeneralSettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;
    private readonly IAutostartService _autostartService;

    public GeneralSettingsViewModel(ISettingsService settingsService, IAutostartService autostartService)
    {
        _settingsService = settingsService;
        _autostartService = autostartService;

        _autostartEnabled = settingsService.GetBool("general.autostart", true);
        _startupDelaySeconds = settingsService.GetInt("general.startup_delay_seconds", 5);
        _minimizeToTray = settingsService.GetBool("general.minimize_to_tray", true);
        _newNotePosition = settingsService.GetString("general.new_note_position", "mouse");
        _autosaveIntervalMs = settingsService.GetInt("general.autosave_interval_ms", 500);
        _pinnedNoFocus = settingsService.GetBool("general.pinned_no_focus", true);
        _soundEnabled = settingsService.GetBool("reminder.sound_enabled", true);
        _snoozeMinutes = settingsService.GetInt("reminder.snooze_minutes", 10);
    }

    [ObservableProperty]
    private bool _autostartEnabled;

    [ObservableProperty]
    private int _startupDelaySeconds;

    [ObservableProperty]
    private bool _minimizeToTray;

    [ObservableProperty]
    private string _newNotePosition;

    [ObservableProperty]
    private int _autosaveIntervalMs;

    [ObservableProperty]
    private bool _pinnedNoFocus;

    [ObservableProperty]
    private bool _soundEnabled;

    [ObservableProperty]
    private int _snoozeMinutes;

    partial void OnAutostartEnabledChanged(bool value)
    {
        _settingsService.SetBool("general.autostart", value);
        _autostartService.SetEnabled(value);
    }

    partial void OnStartupDelaySecondsChanged(int value) =>
        _settingsService.SetInt("general.startup_delay_seconds", value);

    partial void OnMinimizeToTrayChanged(bool value) =>
        _settingsService.SetBool("general.minimize_to_tray", value);

    partial void OnNewNotePositionChanged(string value) =>
        _settingsService.SetString("general.new_note_position", value);

    partial void OnAutosaveIntervalMsChanged(int value) =>
        _settingsService.SetInt("general.autosave_interval_ms", value);

    partial void OnPinnedNoFocusChanged(bool value) =>
        _settingsService.SetBool("general.pinned_no_focus", value);

    partial void OnSoundEnabledChanged(bool value) =>
        _settingsService.SetBool("reminder.sound_enabled", value);

    partial void OnSnoozeMinutesChanged(int value) =>
        _settingsService.SetInt("reminder.snooze_minutes", value);
}
