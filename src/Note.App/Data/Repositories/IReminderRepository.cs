using NoteApp.Models;

namespace NoteApp.Data.Repositories;

/// <summary>提醒数据访问接口。</summary>
public interface IReminderRepository
{
    Reminder? GetById(int id);

    /// <summary>按便签查询，按 RemindAt 升序。</summary>
    List<Reminder> GetByNoteId(Guid noteId);

    /// <summary>查询 RemindAt ≤ before 且未触发的提醒，按 RemindAt 升序。</summary>
    List<Reminder> GetUpcoming(DateTime before);

    /// <summary>全部提醒，按 RemindAt 升序。</summary>
    List<Reminder> GetAll();

    Reminder Create(Reminder reminder);
    Reminder Update(Reminder reminder);

    void Delete(int id);
    void DeleteByNoteId(Guid noteId);
    void MarkCompleted(int id);
}
