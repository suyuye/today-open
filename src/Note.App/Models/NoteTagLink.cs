namespace NoteApp.Models;

/// <summary>
/// 便签与标签的关联（NoteTags 表的一行）。
/// 单独抽出来是为了让列表能**一次**读出全部关联，
/// 而不是对每条便签各查一次标签（那是 N+1 查询的来源）。
/// </summary>
public class NoteTagLink
{
    public Guid NoteId { get; set; }
    public int TagId { get; set; }
}
