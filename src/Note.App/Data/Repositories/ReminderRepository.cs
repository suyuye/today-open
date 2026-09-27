using Microsoft.Data.Sqlite;
using NoteApp.Helpers;
using NoteApp.Models;
using NoteApp.Models.Enums;

namespace NoteApp.Data.Repositories;

/// <summary>提醒数据访问实现（SQLite 参数化读写，异常记录后向上抛出）。</summary>
public class ReminderRepository : IReminderRepository
{
    private const string ReminderColumns = "Id, NoteId, RemindAt, RepeatRule, IsCompleted, CreatedAt";

    private readonly AppDbContext _dbContext;

    public ReminderRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Reminder? GetById(int id) => Execute("GetById", () =>
    {
        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {ReminderColumns} FROM Reminders WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? MapReminder(reader) : null;
    });

    public List<Reminder> GetByNoteId(Guid noteId) => Execute("GetByNoteId", () =>
        QueryReminders(
            $"SELECT {ReminderColumns} FROM Reminders WHERE NoteId = $noteId ORDER BY RemindAt ASC;",
            ("$noteId", GuidHelper.ToStorage(noteId))));

    public List<Reminder> GetUpcoming(DateTime before) => Execute("GetUpcoming", () =>
        QueryReminders(
            $"SELECT {ReminderColumns} FROM Reminders WHERE RemindAt <= $before AND IsCompleted = 0 ORDER BY RemindAt ASC;",
            ("$before", DateTimeHelper.ToStorage(before))));

    public List<Reminder> GetAll() => Execute("GetAll", () =>
        QueryReminders($"SELECT {ReminderColumns} FROM Reminders ORDER BY RemindAt ASC;"));

    public Reminder Create(Reminder reminder) => Execute("Create", () =>
    {
        reminder.CreatedAt = DateTime.Now;

        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO Reminders (NoteId, RemindAt, RepeatRule, IsCompleted, CreatedAt) " +
            "VALUES ($noteId, $remindAt, $repeatRule, $isCompleted, $createdAt);";
        command.Parameters.AddWithValue("$noteId", GuidHelper.ToStorage(reminder.NoteId));
        command.Parameters.AddWithValue("$remindAt", DateTimeHelper.ToStorage(reminder.RemindAt));
        command.Parameters.AddWithValue("$repeatRule", reminder.RepeatRule.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("$isCompleted", reminder.IsCompleted ? 1 : 0);
        command.Parameters.AddWithValue("$createdAt", DateTimeHelper.ToStorage(reminder.CreatedAt));
        command.ExecuteNonQuery();

        using var idCommand = connection.CreateCommand();
        idCommand.CommandText = "SELECT last_insert_rowid();";
        reminder.Id = Convert.ToInt32(idCommand.ExecuteScalar());

        return reminder;
    });

    public Reminder Update(Reminder reminder) => Execute("Update", () =>
    {
        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE Reminders SET RemindAt = $remindAt, RepeatRule = $repeatRule, IsCompleted = $isCompleted " +
            "WHERE Id = $id;";
        command.Parameters.AddWithValue("$remindAt", DateTimeHelper.ToStorage(reminder.RemindAt));
        command.Parameters.AddWithValue("$repeatRule", reminder.RepeatRule.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("$isCompleted", reminder.IsCompleted ? 1 : 0);
        command.Parameters.AddWithValue("$id", reminder.Id);
        command.ExecuteNonQuery();

        return reminder;
    });

    public void Delete(int id) => Execute("Delete", () =>
    {
        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Reminders WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    });

    public void DeleteByNoteId(Guid noteId) => Execute("DeleteByNoteId", () =>
    {
        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Reminders WHERE NoteId = $noteId;";
        command.Parameters.AddWithValue("$noteId", GuidHelper.ToStorage(noteId));
        command.ExecuteNonQuery();
    });

    public void MarkCompleted(int id) => Execute("MarkCompleted", () =>
    {
        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Reminders SET IsCompleted = 1 WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    });

    private List<Reminder> QueryReminders(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var reminders = new List<Reminder>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            reminders.Add(MapReminder(reader));
        }
        return reminders;
    }

    private static Reminder MapReminder(SqliteDataReader reader)
    {
        return new Reminder
        {
            Id = reader.GetInt32(0),
            NoteId = Guid.Parse(reader.GetString(1)),
            RemindAt = DateTimeHelper.Parse(reader.GetString(2)),
            RepeatRule = Enum.Parse<RepeatRule>(reader.GetString(3), ignoreCase: true),
            IsCompleted = reader.GetInt32(4) != 0,
            CreatedAt = DateTimeHelper.Parse(reader.GetString(5)),
        };
    }

    private static T Execute<T>(string operation, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            LogHelper.Error($"ReminderRepository.{operation} 执行失败", ex);
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
            LogHelper.Error($"ReminderRepository.{operation} 执行失败", ex);
            throw;
        }
    }
}
