namespace NoteApp.Data.Repositories;

/// <summary>设置键值对数据访问接口。</summary>
public interface ISettingsRepository
{
    string? GetValue(string key);

    /// <summary>按键读取并转为目标类型，不存在或转换失败时返回默认值。</summary>
    T GetValue<T>(string key, T defaultValue);

    void SetValue(string key, string value);

    /// <summary>泛型写入，自动转为字符串（bool/enum 存小写，double 用不变量文化）。</summary>
    void SetValue<T>(string key, T value);

    Dictionary<string, string> GetAll();
    bool Exists(string key);
    void Remove(string key);
}
