using NoteApp.Models;

namespace NoteApp.Data.Repositories;

/// <summary>标签数据访问接口（含便签-标签多对多关联）。</summary>
public interface ITagRepository
{
    Tag? GetById(int id);
    List<Tag> GetAll();
    Tag? GetByName(string name);

    Tag Create(Tag tag);
    Tag Update(Tag tag);
    void Delete(int id);
    int GetUsageCount(int tagId);

    void AddTagToNote(Guid noteId, int tagId);
    void RemoveTagFromNote(Guid noteId, int tagId);
    List<Tag> GetTagsForNote(Guid noteId);
    List<Guid> GetNoteIdsForTag(int tagId);

    /// <summary>
    /// 一次读出全部「便签-标签」关联。
    /// 列表需要为每条便签显示标签，逐条调用 <see cref="GetTagsForNote"/> 会变成 N+1 查询；
    /// 改为读一次关联、配合一次 Tags 全表，在内存里拼出映射。
    /// </summary>
    List<NoteTagLink> GetAllNoteTagLinks();

    void RemoveAllTagsForNote(Guid noteId);
}
