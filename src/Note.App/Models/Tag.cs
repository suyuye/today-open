using NoteApp.Models.Enums;

namespace NoteApp.Models;

/// <summary>标签数据模型。</summary>
public class Tag
{
    public int Id { get; set; }

    /// <summary>标签名称（唯一）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>标签颜色，同便签色枚举。</summary>
    public NoteColor Color { get; set; } = NoteColor.GrayDeep;

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
