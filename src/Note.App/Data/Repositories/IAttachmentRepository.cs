using NoteApp.Models;

namespace NoteApp.Data.Repositories;

/// <summary>图片附件数据访问接口。</summary>
public interface IAttachmentRepository
{
    /// <summary>插入一条附件（含图片字节）。</summary>
    Attachment Add(Attachment attachment);

    /// <summary>
    /// 取某条便签的全部附件**元数据**（不含字节），按创建时间升序。
    /// 展示缩略图条只需元数据，不要顺带把图片读进内存。
    /// </summary>
    List<Attachment> GetForNote(Guid noteId);

    /// <summary>按 Id 取附件（含图片字节）；不存在返回 null。</summary>
    Attachment? GetData(Guid id);

    int CountForNote(Guid noteId);

    void Delete(Guid id);

    /// <summary>
    /// 附件存储统计（总数/总字节 + 孤儿附件的数量与字节）。
    /// 供设置页「附件管理」展示；孤儿 = 所属便签已被永久删除的附件。
    /// </summary>
    AttachmentStorageSummary GetStorageSummary();

    /// <summary>删除所有孤儿附件（所属便签已被永久删除），返回删除的条数。供设置页「清理孤儿附件」。</summary>
    int DeleteOrphans();
}
