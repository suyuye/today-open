namespace NoteApp.Models;

/// <summary>
/// 图片附件的存储占用统计。
///
/// 「孤儿附件」指其所属便签已被**永久删除**（数据库里已查不到该 NoteId）的图片 ——
/// 永久删除便签的代码路径从不清理附件表，这些 BLOB 会永远留在库里，
/// 备份文件也随之变大。设置页的「清理孤儿附件」就是冲着它们去的。
///
/// ⚠️ 回收站里（软删除）的便签**不算**孤儿：便签还能还原，图片必须跟着活下来。
/// </summary>
public class AttachmentStorageSummary
{
    /// <summary>附件总数（含孤儿）。</summary>
    public int TotalCount { get; init; }

    /// <summary>附件总字节数（含孤儿）。</summary>
    public long TotalBytes { get; init; }

    /// <summary>孤儿附件数（所属便签已被永久删除）。</summary>
    public int OrphanCount { get; init; }

    /// <summary>孤儿附件总字节数。</summary>
    public long OrphanBytes { get; init; }

    /// <summary>是否有可清理的孤儿附件。</summary>
    public bool HasOrphans => OrphanCount > 0;
}
