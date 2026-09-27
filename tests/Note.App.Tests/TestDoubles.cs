using NoteApp.Models;
using NoteApp.Models.Enums;
using NoteApp.Services;
using NoteApp.ViewModels;

namespace NoteApp.Tests;

/// <summary>
/// 测试替身集合。放一起是为了让各测试文件只关心断言本身。
/// </summary>
internal static class TestDoubles
{
    /// <summary>用给定的便签与标签拼一个只读的 INoteService 替身。</summary>
    public static RecordingNoteService Service(IEnumerable<Note>? notes = null) =>
        new(notes?.ToList() ?? new List<Note>());
}

/// <summary>
/// 记录调用并返回预设数据的 INoteService 替身。
/// 未被用到的成员一律抛异常 —— 这样一旦测试代码走了意料之外的路径会立刻暴露，
/// 而不是悄悄返回 null 让断言以误导的方式失败。
/// </summary>
internal sealed class RecordingNoteService : INoteService
{
    private readonly List<Note> _notes;
    private readonly Dictionary<Guid, List<Tag>> _tagsByNote = new();

    public RecordingNoteService(List<Note> notes)
    {
        _notes = notes;
    }

    /// <summary>按顺序记录所有被调用的命令名（形如 "TogglePin:&lt;id&gt;"）。</summary>
    public List<string> Calls { get; } = new();

    // 这些事件只为满足接口契约而声明。关掉「从未使用」告警，
    // 而不是加个假的 raise 方法 —— 替身应当保持诚实。
#pragma warning disable CS0067
    public event EventHandler<NoteEventArgs>? NoteCreated;
    public event EventHandler<NoteEventArgs>? NoteUpdated;
    public event EventHandler<NoteEventArgs>? NoteDeleted;
    public event EventHandler<NoteEventArgs>? NoteRestored;
    public event EventHandler? NotesBulkChanged;
    public event EventHandler? TagAssignmentsChanged;
#pragma warning restore CS0067

    /// <summary>给某个便签预设标签。</summary>
    public RecordingNoteService WithTags(Guid noteId, params Tag[] tags)
    {
        _tagsByNote[noteId] = tags.ToList();
        return this;
    }

    public List<Note> GetAllNotes(bool includeArchived = false) =>
        _notes.Where(n => n.DeletedAt is null && (includeArchived || !n.IsArchived)).ToList();

    public List<Note> GetAllNotesSnapshot() => _notes.ToList();

    public List<NoteTagLink> GetAllNoteTagLinks() => new();

    public Note? GetNote(Guid id) => _notes.FirstOrDefault(n => n.Id == id);

    public List<Note> GetDeletedNotes() => _notes.Where(n => n.DeletedAt is not null).ToList();

    public List<Note> SearchNotes(string keyword) => new();

    public List<Tag> GetTagsForNote(Guid noteId) =>
        _tagsByNote.TryGetValue(noteId, out var tags) ? tags : new List<Tag>();

    public void TogglePin(Guid id) => Calls.Add($"TogglePin:{id}");

    public void ToggleLock(Guid id) => Calls.Add($"ToggleLock:{id}");

    public void ToggleArchive(Guid id) => Calls.Add($"ToggleArchive:{id}");

    public void DeleteNote(Guid id, bool permanent = false) =>
        Calls.Add(permanent ? $"PermanentDelete:{id}" : $"Delete:{id}");

    public void RestoreNote(Guid id) => Calls.Add($"Restore:{id}");

    // ---- 以下成员本套测试未使用 ----

    public Note CreateNote(string? title = null, string content = "", NoteColor color = NoteColor.YellowDeep) =>
        throw new NotSupportedException();

    public Note UpdateNote(Note note) => throw new NotSupportedException();

    public void UpdateNoteTitle(Guid id, string? title) => throw new NotSupportedException();

    public void SetDisplayMode(Guid id, NoteDisplayMode mode) => throw new NotSupportedException();

    public void SetColor(Guid id, NoteColor color) => throw new NotSupportedException();

    public void SetOpacity(Guid id, int opacity) => throw new NotSupportedException();

    public void UpdatePosition(Guid id, int x, int y) => throw new NotSupportedException();

    public void UpdateSize(Guid id, int width, int height) => throw new NotSupportedException();

    public void UpdateNoteGeometry(Guid id, int x, int y, int width, int height) => throw new NotSupportedException();

    public void UpdateDockState(Guid id, bool isDockEnabled, DockEdge edge) =>
        Calls.Add($"UpdateDockState:{id}:{isDockEnabled}:{edge}");

    public void EmptyRecycleBin() => throw new NotSupportedException();

    public (int Inserted, int Skipped) ImportNotes(IReadOnlyList<Note> notes) =>
        throw new NotSupportedException();

    public int PurgeExpiredTrash(int retainDays) => throw new NotSupportedException();

    public Tag CreateTag(string name, NoteColor color = NoteColor.GrayDeep) => throw new NotSupportedException();

    public void DeleteTag(int tagId) => throw new NotSupportedException();

    public void AddTag(Guid noteId, int tagId) => throw new NotSupportedException();

    public void RemoveTag(Guid noteId, int tagId) => throw new NotSupportedException();

    public void ToggleTag(Guid noteId, int tagId) => throw new NotSupportedException();

    public Reminder AddReminder(Guid noteId, DateTime remindAt, RepeatRule repeat = RepeatRule.None) =>
        throw new NotSupportedException();

    public void RemoveReminder(int reminderId) => throw new NotSupportedException();

    public List<Reminder> GetRemindersForNote(Guid noteId) => throw new NotSupportedException();
}

/// <summary>记录两个通知方法各被调用了多少次的行父级替身。</summary>
internal sealed class RecordingLineParent : INoteLineParent
{
    public int ContentChangedCount { get; private set; }

    public int TodoStateChangedCount { get; private set; }

    public void NotifyLineContentChanged() => ContentChangedCount++;

    public void NotifyTodoStateChanged() => TodoStateChangedCount++;
}
