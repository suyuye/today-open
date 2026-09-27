using NoteApp.Models;

namespace NoteApp.Data.Repositories;

/// <summary>便签数据访问接口（SQLite 读写，不含业务逻辑）。</summary>
public interface INoteRepository
{
    Note? GetById(Guid id);

    /// <summary>未删除的便签，默认排除归档；按置顶优先、最近更新排序。</summary>
    List<Note> GetAll(bool includeArchived = false);

    /// <summary>
    /// 一次读出全部便签（含已删除、已归档），供列表以「单份快照」做筛选与计数。
    /// 列表的分类切换、搜索、数量徽标全部基于这份快照在内存里计算，
    /// 避免此前一次刷新要读 3~4 次全表。
    /// </summary>
    List<Note> GetAllIncludingDeleted();

    List<Note> GetByTag(int tagId);
    List<Note> Search(string keyword);
    List<Note> GetDeleted();

    Note Create(Note note);
    Note Update(Note note);

    /// <summary>
    /// 批量导入便签：在**单个事务**内插入，保留原 Id 与创建/修改时间。
    /// 已存在相同 Id 的跳过（不覆盖既有数据）；任一条插入失败则整体回滚。
    /// 返回 (实际插入数, 因已存在而跳过数)。
    /// </summary>
    (int Inserted, int Skipped) InsertImportedBatch(IReadOnlyList<Note> notes);

    void SoftDelete(Guid id);
    void Restore(Guid id);
    void PermanentDelete(Guid id);

    int GetCount(bool includeDeleted = false);
}
