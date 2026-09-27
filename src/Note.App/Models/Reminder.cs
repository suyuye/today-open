using NoteApp.Models.Enums;

namespace NoteApp.Models;

/// <summary>提醒数据模型。</summary>
public class Reminder
{
    public int Id { get; set; }

    /// <summary>关联便签 ID。</summary>
    public Guid NoteId { get; set; }

    /// <summary>提醒时间。</summary>
    public DateTime RemindAt { get; set; }

    /// <summary>重复规则。</summary>
    public RepeatRule RepeatRule { get; set; } = RepeatRule.None;

    /// <summary>本次是否已触发。</summary>
    public bool IsCompleted { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
