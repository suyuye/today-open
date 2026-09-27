using NoteApp.Data;
using NoteApp.Data.Repositories;
using NoteApp.Helpers;
using NoteApp.Models.Enums;

namespace NoteApp.Services;

/// <summary>设置服务：封装键值对读写 + 默认值 + 变更事件，变更经 Serilog 记录。</summary>
public class SettingsService : ISettingsService
{
    private readonly ISettingsRepository _settingsRepository;

    public SettingsService(ISettingsRepository settingsRepository)
    {
        _settingsRepository = settingsRepository;
    }

    public event EventHandler<SettingChangedEventArgs>? SettingChanged;

    public T GetValue<T>(string key, T defaultValue) =>
        Execute("GetValue", () => _settingsRepository.GetValue(key, defaultValue));

    public string GetString(string key, string defaultValue = "") => GetValue(key, defaultValue);
    public int GetInt(string key, int defaultValue = 0) => GetValue(key, defaultValue);
    public bool GetBool(string key, bool defaultValue = false) => GetValue(key, defaultValue);
    public double GetDouble(string key, double defaultValue = 0) => GetValue(key, defaultValue);

    public void SetValue<T>(string key, T value)
    {
        Execute("SetValue", () => _settingsRepository.SetValue(key, value));
        LogHelper.Info($"设置 {key} = {value}");
        SettingChanged?.Invoke(this, new SettingChangedEventArgs(key, Convert.ToString(value)));
    }

    public void SetString(string key, string value) => SetValue(key, value);
    public void SetInt(string key, int value) => SetValue(key, value);
    public void SetBool(string key, bool value) => SetValue(key, value);

    public Dictionary<string, string> GetAll() =>
        Execute("GetAll", () => _settingsRepository.GetAll());

    public void ResetToDefaults()
    {
        Execute("ResetToDefaults", () =>
        {
            foreach (var (key, value) in AppDbContext.DefaultSettings)
            {
                _settingsRepository.SetValue(key, value);
            }
        });

        LogHelper.Info("重置所有设置为默认值");
    }

    // ---- 便捷属性 ----

    public bool Autostart
    {
        get => GetValue("general.autostart", true);
        set => SetValue("general.autostart", value);
    }

    public int StartupDelaySeconds
    {
        get => GetValue("general.startup_delay_seconds", 5);
        set => SetValue("general.startup_delay_seconds", value);
    }

    public bool MinimizeToTray
    {
        get => GetValue("general.minimize_to_tray", true);
        set => SetValue("general.minimize_to_tray", value);
    }

    public AppTheme Theme
    {
        get => GetValue("appearance.theme", AppTheme.Light);
        set => SetValue("appearance.theme", value);
    }

    public NoteColor DefaultColor
    {
        get => GetValue("appearance.default_color", NoteColor.YellowDeep);
        set => SetValue("appearance.default_color", value);
    }

    public int DefaultOpacity
    {
        get => GetValue("appearance.default_opacity", 100);
        set => SetValue("appearance.default_opacity", value);
    }

    public double FontSize
    {
        get => GetValue("appearance.font_size", 16.5);
        set => SetValue("appearance.font_size", value);
    }

    public bool BackupEnabled
    {
        get => GetValue("data.backup_enabled", true);
        set => SetValue("data.backup_enabled", value);
    }

    public int BackupIntervalDays
    {
        get => GetValue("data.backup_interval_days", 7);
        set => SetValue("data.backup_interval_days", value);
    }

    private static T Execute<T>(string operation, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            LogHelper.Error($"SettingsService.{operation} 执行失败", ex);
            throw;
        }
    }

    private static void Execute(string operation, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            LogHelper.Error($"SettingsService.{operation} 执行失败", ex);
            throw;
        }
    }
}
