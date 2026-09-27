using NoteApp.Models;
using NoteApp.Models.Enums;

namespace NoteApp.Services;

/// <summary>便签业务服务。</summary>
public interface INoteService
{
    // 创建
    Note CreateNote(string? title = null, string content = "", NoteColor color = NoteColor.YellowDeep);

    // 查询
    Note? GetNote(Guid id);
    List<Note> GetAllNotes(bool includeArchived = false);

    /// <summary>
    /// 一次读出全部便签（含已删除、已归档），供列表以单份快照做筛选与计数。
    /// </summary>
    List<Note> GetAllNotesSnapshot();

    /// <summary>一次读出全部便签-标签关联（避免逐条查询便签的标签）。</summary>
    List<NoteTagLink> GetAllNoteTagLinks();

    List<Note> SearchNotes(string keyword);
    List<Note> GetDeletedNotes();

    // 更新
    Note UpdateNote(Note note);
    void UpdateNoteTitle(Guid id, string? title);
    void TogglePin(Guid id);
    void ToggleLock(Guid id);
    void ToggleArchive(Guid id);

    /// <summary>
    /// 设置便签呈现形态（常规 / 折叠 / 极简）。
    /// 取代早期的 <c>ToggleCollapse</c> —— 那个版本只翻转折叠列，
    /// 既无法切到极简，又会在极简态下造出「两列同时为真」的非法组合。
    /// </summary>
    void SetDisplayMode(Guid id, NoteDisplayMode mode);
    void SetColor(Guid id, NoteColor color);
    void SetOpacity(Guid id, int opacity);
    void UpdatePosition(Guid id, int x, int y);
    void UpdateSize(Guid id, int width, int height);
    /// <summary>仅更新窗口几何（位置+尺寸），不触发 NoteUpdated 事件，避免移动便签时主页列表刷新。</summary>
    void UpdateNoteGeometry(Guid id, int x, int y, int width, int height);

    /// <summary>
    /// 仅更新贴边状态（是否启用 + 贴在哪条边），不触发 NoteUpdated 事件。
    /// 与几何更新同理：贴边/唤出是高频窗口行为，不该引起主页列表重建。
    /// </summary>
    void UpdateDockState(Guid id, bool isDockEnabled, DockEdge edge);

    // 删除与恢复
    void DeleteNote(Guid id, bool permanent = false);
    void RestoreNote(Guid id);
    void EmptyRecycleBin();

    /// <summary>
    /// 批量导入便签（单事务、保留原 Id 与时间戳、已存在同 Id 的跳过）。
    /// 放在这里而不是让导入服务直接写库，是为了**统一抛出 <see cref="NotesBulkChanged"/>** ——
    /// 否则绕过服务写库会让列表的内存快照一直陈旧（界面看不到导入结果）。
    /// </summary>
    (int Inserted, int Skipped) ImportNotes(IReadOnlyList<Note> notes);

    /// <summary>
    /// 清理回收站中「删除时间已超过 retainDays 天」的便签，返回实际清理数量。
    /// 对应设置页的「回收站保留天数」（data.retain_days），应用启动时调用一次。
    /// </summary>
    int PurgeExpiredTrash(int retainDays);

    // 标签
    Tag CreateTag(string name, NoteColor color = NoteColor.GrayDeep);
    /// <summary>删除标签（NoteTags 关联经外键级联自动清除）。</summary>
    void DeleteTag(int tagId);
    void AddTag(Guid noteId, int tagId);
    void RemoveTag(Guid noteId, int tagId);
    void ToggleTag(Guid noteId, int tagId);
    List<Tag> GetTagsForNote(Guid noteId);

    // 提醒
    Reminder AddReminder(Guid noteId, DateTime remindAt, RepeatRule repeat = RepeatRule.None);
    void RemoveReminder(int reminderId);
    List<Reminder> GetRemindersForNote(Guid noteId);

    // 事件
    event EventHandler<NoteEventArgs>? NoteCreated;
    event EventHandler<NoteEventArgs>? NoteUpdated;
    event EventHandler<NoteEventArgs>? NoteDeleted;
    event EventHandler<NoteEventArgs>? NoteRestored;

    /// <summary>
    /// 一批便签被整体增删（清空回收站、按保留期清理）。
    /// 这类操作不逐条抛事件（几十条会引发几十次列表刷新），改用一次批量通知。
    /// </summary>
    event EventHandler? NotesBulkChanged;

    /// <summary>
    /// 便签与标签的关联发生变化（加/删标签、标签本身增删）。
    /// 便签字段没变，但列表要显示的标签文字与「标签」分类归属变了，故单独通知。
    /// </summary>
    event EventHandler? TagAssignmentsChanged;
}
