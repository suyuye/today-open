namespace NoteApp.Models;

/// <summary>设置项模型（对应 Settings 表）。</summary>
public class AppSettings
{
    /// <summary>设置键名（主键）。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>设置值（序列化为字符串）。</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>最后修改时间。</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
