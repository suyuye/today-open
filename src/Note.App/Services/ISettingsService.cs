using NoteApp.Models.Enums;

namespace NoteApp.Services;

/// <summary>设置服务。</summary>
public interface ISettingsService
{
    // 读取
    T GetValue<T>(string key, T defaultValue);
    string GetString(string key, string defaultValue = "");
    int GetInt(string key, int defaultValue = 0);
    bool GetBool(string key, bool defaultValue = false);
    double GetDouble(string key, double defaultValue = 0);

    // 写入
    void SetValue<T>(string key, T value);
    void SetString(string key, string value);
    void SetInt(string key, int value);
    void SetBool(string key, bool value);

    // 批量
    Dictionary<string, string> GetAll();
    void ResetToDefaults();

    // 便捷属性（内部走 Get/Set，供 ViewModel 绑定）
    bool Autostart { get; set; }
    int StartupDelaySeconds { get; set; }
    bool MinimizeToTray { get; set; }
    AppTheme Theme { get; set; }
    NoteColor DefaultColor { get; set; }
    int DefaultOpacity { get; set; }
    double FontSize { get; set; }
    bool BackupEnabled { get; set; }
    int BackupIntervalDays { get; set; }

    // 事件
    event EventHandler<SettingChangedEventArgs>? SettingChanged;
}
