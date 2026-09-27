using NoteApp.Data.Repositories;
using NoteApp.Helpers;
using NoteApp.Models;
using NoteApp.Models.Enums;

namespace NoteApp.Services;

/// <summary>便签业务服务：封装 Repository + 业务规则 + 事件，操作与异常经 Serilog 记录。</summary>
public class NoteService : INoteService
{
    private readonly INoteRepository _noteRepository;
    private readonly ITagRepository _tagRepository;
    private readonly IReminderRepository _reminderRepository;

    public NoteService(INoteRepository noteRepository, ITagRepository tagRepository, IReminderRepository reminderRepository)
    {
        _noteRepository = noteRepository;
        _tagRepository = tagRepository;
        _reminderRepository = reminderRepository;
    }

    public event EventHandler<NoteEventArgs>? NoteCreated;
    public event EventHandler<NoteEventArgs>? NoteUpdated;
    public event EventHandler<NoteEventArgs>? NoteDeleted;
    public event EventHandler<NoteEventArgs>? NoteRestored;
    public event EventHandler? NotesBulkChanged;
    public event EventHandler? TagAssignmentsChanged;

    public Note CreateNote(string? title = null, string content = "", NoteColor color = NoteColor.YellowDeep)
    {
        var note = Execute("CreateNote", () =>
        {
            var n = new Note { Title = title, Content = content, Color = color };
            n.Title = string.IsNullOrWhiteSpace(n.Title) ? DeriveTitle(content) : n.Title;
            return _noteRepository.Create(n);
        });

        LogHelper.Info($"创建便签 {note.Id}");
        NoteCreated?.Invoke(this, new NoteEventArgs(note));
        return note;
    }

    public Note? GetNote(Guid id) => Execute("GetNote", () => _noteRepository.GetById(id));

    public List<Note> GetAllNotes(bool includeArchived = false) =>
        Execute("GetAllNotes", () => _noteRepository.GetAll(includeArchived));

    public List<Note> GetAllNotesSnapshot() =>
        Execute("GetAllNotesSnapshot", () => _noteRepository.GetAllIncludingDeleted());

    public List<NoteTagLink> GetAllNoteTagLinks() =>
        Execute("GetAllNoteTagLinks", () => _tagRepository.GetAllNoteTagLinks());

    public List<Note> SearchNotes(string keyword) =>
        Execute("SearchNotes", () => _noteRepository.Search(keyword));

    public List<Note> GetDeletedNotes() =>
        Execute("GetDeletedNotes", () => _noteRepository.GetDeleted());

    public Note UpdateNote(Note note)
    {
        var updated = Execute("UpdateNote", () => _noteRepository.Update(note));
        LogHelper.Info($"更新便签 {updated.Id}");
        NoteUpdated?.Invoke(this, new NoteEventArgs(updated));
        return updated;
    }

    public void UpdateNoteTitle(Guid id, string? title)
    {
        var note = Execute("UpdateNoteTitle", () => UpdateField(id, n => n.Title = title));
        NotifyUpdated(note, "更新标题");
    }

    public void TogglePin(Guid id)
    {
        var note = Execute("TogglePin", () => UpdateField(id, n => n.IsPinned = !n.IsPinned));
        NotifyUpdated(note, "切换置顶");
    }

    public void ToggleLock(Guid id)
    {
        var note = Execute("ToggleLock", () => UpdateField(id, n => n.IsLocked = !n.IsLocked));
        NotifyUpdated(note, "切换锁定");
    }

    public void ToggleArchive(Guid id)
    {
        var note = Execute("ToggleArchive", () => UpdateField(id, n => n.IsArchived = !n.IsArchived));
        NotifyUpdated(note, "切换归档");
    }

    /// <summary>
    /// 切换便签形态（常规 ⇄ 折叠 ⇄ 极简）。
    ///
    /// 收口前这里叫 <c>ToggleCollapse</c>，且只翻转 <c>IsCollapsed</c> 一个字段 ——
    /// 于是它既无法表达"极简"（想切极简没有服务层入口），又可能在极简态下
    /// 直接翻转折叠位，造出「两列同时为 true」的非法组合。
    /// 现在改为**收形态参数**，由 <see cref="Note.DisplayMode"/> 统一同步两个旧列，
    /// 非法组合在类型层面就写不出来。
    /// </summary>
    public void SetDisplayMode(Guid id, NoteDisplayMode mode)
    {
        var note = Execute("SetDisplayMode", () => UpdateField(id, n => n.DisplayMode = mode));
        NotifyUpdated(note, "切换便签形态");
    }

    public void SetColor(Guid id, NoteColor color)
    {
        var note = Execute("SetColor", () => UpdateField(id, n => n.Color = color));
        NotifyUpdated(note, "设置颜色");
    }

    public void SetOpacity(Guid id, int opacity)
    {
        var clamped = Math.Clamp(opacity, 30, 100);
        var note = Execute("SetOpacity", () => UpdateField(id, n => n.Opacity = clamped));
        NotifyUpdated(note, "设置不透明度");
    }

    public void UpdatePosition(Guid id, int x, int y)
    {
        var note = Execute("UpdatePosition", () => UpdateField(id, n => { n.PositionX = x; n.PositionY = y; }));
        NotifyUpdated(note, "更新位置");
    }

    public void UpdateSize(Guid id, int width, int height)
    {
        var note = Execute("UpdateSize", () => UpdateField(id, n => { n.Width = width; n.Height = height; }));
        NotifyUpdated(note, "更新尺寸");
    }

    /// <summary>仅更新窗口几何（位置+尺寸），不触发 NoteUpdated 事件，避免移动便签时主页列表刷新。</summary>
    public void UpdateNoteGeometry(Guid id, int x, int y, int width, int height)
    {
        Execute("UpdateNoteGeometry", () => UpdateField(id, n =>
        {
            n.PositionX = x;
            n.PositionY = y;
            n.Width = width;
            n.Height = height;
        }));
        // 不触发 NoteUpdated 事件
    }

    /// <summary>仅更新贴边状态，不触发 NoteUpdated 事件（贴边是高频窗口行为，不该让主页列表重建）。</summary>
    public void UpdateDockState(Guid id, bool isDockEnabled, DockEdge edge)
    {
        Execute("UpdateDockState", () => UpdateField(id, n =>
        {
            n.IsDockEnabled = isDockEnabled;
            n.DockEdge = edge;
        }));
        // 不触发 NoteUpdated 事件
    }

    public void DeleteNote(Guid id, bool permanent = false)
    {
        var note = Execute("DeleteNote", () =>
        {
            var n = _noteRepository.GetById(id);
            if (permanent)
            {
                _noteRepository.PermanentDelete(id);
            }
            else if (n is not null)
            {
                _noteRepository.SoftDelete(id);
            }
            return n;
        });

        if (note is null && !permanent)
        {
            LogHelper.Warning($"软删除失败：便签 {id} 不存在或已删除");
            return;
        }

        LogHelper.Info(permanent ? $"永久删除便签 {id}" : $"软删除便签 {id}");
        NoteDeleted?.Invoke(this, new NoteEventArgs(note ?? new Note { Id = id }));
    }

    public void RestoreNote(Guid id)
    {
        Execute("RestoreNote", () => _noteRepository.Restore(id));
        var note = _noteRepository.GetById(id);
        LogHelper.Info($"恢复便签 {id}");
        if (note is not null)
        {
            NoteRestored?.Invoke(this, new NoteEventArgs(note));
        }
    }

    public void EmptyRecycleBin()
    {
        var count = Execute("EmptyRecycleBin", () =>
        {
            var deleted = _noteRepository.GetDeleted();
            foreach (var note in deleted)
            {
                _noteRepository.PermanentDelete(note.Id);
            }
            return deleted.Count;
        });

        LogHelper.Info($"清空回收站：永久删除 {count} 个便签");

        if (count > 0)
        {
            NotesBulkChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 按「回收站保留天数」清理过期便签。
    /// 设置页里这个滑块一直存在（7~90 天），但过去没有任何代码消费它 ——
    /// 用户调小保留天数后并不会发生任何清理，等于名不副实的设置。
    /// 这里补上真正的清理逻辑，并只删「删除时间早于阈值」的项，
    /// 不影响仍在保留期内的内容。
    /// </summary>
    public int PurgeExpiredTrash(int retainDays)
    {
        // 守卫：0 或负数意味着「不清理」，避免配置异常时误删全部回收站
        if (retainDays <= 0)
        {
            return 0;
        }

        var cutoff = DateTime.Now.AddDays(-retainDays);

        var expired = Execute("PurgeExpiredTrash", () =>
            _noteRepository.GetDeleted()
                .Where(n => n.DeletedAt is not null && n.DeletedAt.Value < cutoff)
                .ToList());

        if (expired.Count == 0)
        {
            return 0;
        }

        foreach (var note in expired)
        {
            _noteRepository.PermanentDelete(note.Id);
        }

        LogHelper.Info($"回收站自动清理：永久删除 {expired.Count} 个超过 {retainDays} 天的便签");
        NotesBulkChanged?.Invoke(this, EventArgs.Empty);
        return expired.Count;
    }

    /// <summary>批量导入便签（单事务），成功后通知列表刷新。</summary>
    public (int Inserted, int Skipped) ImportNotes(IReadOnlyList<Note> notes)
    {
        if (notes.Count == 0)
        {
            return (0, 0);
        }

        var (inserted, skipped) = Execute("ImportNotes", () => _noteRepository.InsertImportedBatch(notes));

        if (inserted > 0)
        {
            NotesBulkChanged?.Invoke(this, EventArgs.Empty);
        }

        return (inserted, skipped);
    }

    public Tag CreateTag(string name, NoteColor color = NoteColor.GrayDeep)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("标签名不能为空");
        }

        if (_tagRepository.GetByName(trimmed) is not null)
        {
            throw new InvalidOperationException($"已存在同名标签「{trimmed}」");
        }

        var tag = Execute("CreateTag", () => _tagRepository.Create(new Tag { Name = trimmed, Color = color }));
        LogHelper.Info($"创建标签 {tag.Id}「{tag.Name}」");
        TagAssignmentsChanged?.Invoke(this, EventArgs.Empty);
        return tag;
    }

    public void DeleteTag(int tagId)
    {
        // NoteTags 关联表对 TagId 设有 ON DELETE CASCADE，删除标签时关联自动清除
        Execute("DeleteTag", () => _tagRepository.Delete(tagId));
        LogHelper.Info($"删除标签 {tagId}");
        TagAssignmentsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AddTag(Guid noteId, int tagId)
    {
        Execute("AddTag", () => _tagRepository.AddTagToNote(noteId, tagId));
        LogHelper.Info($"给便签 {noteId} 添加标签 {tagId}");
        TagAssignmentsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveTag(Guid noteId, int tagId)
    {
        Execute("RemoveTag", () => _tagRepository.RemoveTagFromNote(noteId, tagId));
        LogHelper.Info($"移除便签 {noteId} 的标签 {tagId}");
        TagAssignmentsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleTag(Guid noteId, int tagId)
    {
        var note = Execute("ToggleTag", () =>
        {
            var n = _noteRepository.GetById(noteId);
            if (n is null)
            {
                return null;
            }

            var hasTag = _tagRepository.GetTagsForNote(noteId).Any(t => t.Id == tagId);
            if (hasTag)
            {
                _tagRepository.RemoveTagFromNote(noteId, tagId);
            }
            else
            {
                _tagRepository.AddTagToNote(noteId, tagId);
            }
            return n;
        });

        if (note is not null)
        {
            LogHelper.Info($"切换便签 {noteId} 的标签 {tagId}");
            TagAssignmentsChanged?.Invoke(this, EventArgs.Empty);
            NoteUpdated?.Invoke(this, new NoteEventArgs(note));
        }
    }

    public List<Tag> GetTagsForNote(Guid noteId) =>
        Execute("GetTagsForNote", () => _tagRepository.GetTagsForNote(noteId));

    public Reminder AddReminder(Guid noteId, DateTime remindAt, RepeatRule repeat = RepeatRule.None)
    {
        var reminder = Execute("AddReminder", () => _reminderRepository.Create(new Reminder
        {
            NoteId = noteId,
            RemindAt = remindAt,
            RepeatRule = repeat,
        }));

        LogHelper.Info($"为便签 {noteId} 添加提醒（{remindAt:yyyy-MM-dd HH:mm:ss}）");
        return reminder;
    }

    public void RemoveReminder(int reminderId)
    {
        Execute("RemoveReminder", () => _reminderRepository.Delete(reminderId));
        LogHelper.Info($"移除提醒 {reminderId}");
    }

    public List<Reminder> GetRemindersForNote(Guid noteId) =>
        Execute("GetRemindersForNote", () => _reminderRepository.GetByNoteId(noteId));

    /// <summary>读取便签、应用变更、写回；不存在时返回 null。</summary>
    private Note? UpdateField(Guid id, Action<Note> mutate)
    {
        var note = _noteRepository.GetById(id);
        if (note is null)
        {
            return null;
        }

        mutate(note);
        _noteRepository.Update(note);
        return note;
    }

    private void NotifyUpdated(Note? note, string action)
    {
        if (note is null)
        {
            LogHelper.Warning($"{action}失败：便签不存在");
            return;
        }

        LogHelper.Info($"{action}：便签 {note.Id}");
        NoteUpdated?.Invoke(this, new NoteEventArgs(note));
    }

    /// <summary>标题为空时取内容首行前 20 字。</summary>
    private static string DeriveTitle(string content)
    {
        var firstLine = content.Split('\n')[0].Trim();
        return firstLine.Length <= 20 ? firstLine : firstLine[..20];
    }

    private static T Execute<T>(string operation, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            LogHelper.Error($"NoteService.{operation} 执行失败", ex);
            throw;
        }
    }

    private static void Execute(string operation, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            LogHelper.Error($"NoteService.{operation} 执行失败", ex);
            throw;
        }
    }
}
